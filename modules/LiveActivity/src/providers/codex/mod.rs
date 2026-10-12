//! The Codex provider: thread state and account usage for the island.
//!
//! Design decisions worth stating, because each one exists to stop a specific
//! failure mode:
//!
//! * **Read-only supported endpoints only.** Try the official local control
//!   proxy, then fall back to a clearly marked account-only child. Never read
//!   private process state or load disk history as if it were a live thread.
//! * **Polling is a fallback, not the mechanism.** Notifications are consumed
//!   when present; a low-frequency poll keeps the display correct when they are
//!   not. The poll interval is stated in one place and is deliberately slow.
//! * **Failure is a state, not an exception.** Every path that cannot determine
//!   something produces `Unknown` or `Disconnected`, never an invented value.

pub mod app_server;
pub mod protocol;
pub mod threads;
pub mod worker;

use std::sync::{
    atomic::{AtomicBool, AtomicU64, Ordering},
    Arc,
};

use crate::activity::LiveActivity;
use crate::diagnostics;
use crate::providers::codex::app_server::{AppServer, Discovery};
use crate::providers::codex::protocol::{RateLimitWindow, RateLimits, ThreadRecord, TokenUsage};
use crate::providers::{Provider, ProviderHealth, ProviderStatus};

/// How often to re-read thread state when notifications are unavailable.
///
/// Ten seconds is chosen against the two failure modes: faster would wake a
/// resident process for no visible benefit, slower would let the island lag a
/// status change the user is waiting on. Well-behaved app-servers push
/// notifications, in which case this is only a safety net.
pub const POLL_INTERVAL: std::time::Duration = std::time::Duration::from_secs(10);

fn retain_missing_window(
    incoming: Option<RateLimitWindow>,
    old: Option<RateLimitWindow>,
) -> (Option<RateLimitWindow>, bool) {
    match (incoming, old) {
        (None, Some(old)) => (Some(old), true),
        (Some(mut new), Some(old)) => {
            // A changed quota window is a different scope, not a partial update.
            if new.window_minutes.is_some()
                && old.window_minutes.is_some()
                && new.window_minutes != old.window_minutes
            {
                return (Some(new), false);
            }
            let inherited = new.used_fraction.is_none() && old.used_fraction.is_some()
                || new.window_minutes.is_none() && old.window_minutes.is_some()
                || new.resets_at.is_none() && old.resets_at.is_some();
            new.used_fraction = new.used_fraction.or(old.used_fraction);
            new.window_minutes = new.window_minutes.or(old.window_minutes);
            new.resets_at = new.resets_at.or(old.resets_at);
            (Some(new), inherited)
        }
        (value, _) => (value, false),
    }
}

/// RPC method names, kept together so a protocol rename is a one-line change.
pub mod methods {
    pub const THREAD_LIST: &str = "thread/list";
    pub const ACCOUNT_RATE_LIMITS: &str = "account/rateLimits/read";
}

/// Methods this build knows how to parse notifications from.
const NOTIFICATION_PREFIXES: [&str; 4] = [
    "thread/status/changed",
    "thread/tokenUsage/updated",
    "thread/status",
    "account/rateLimits/updated",
];

#[derive(Debug)]
pub struct CodexProvider {
    enabled: bool,
    server: Option<AppServer>,
    /// Why the last start attempt failed, kept so the settings page can explain
    /// itself without re-attempting a spawn on every render.
    last_reason: Option<String>,
    /// Counts poll cycles so the settings page can show that the provider is
    /// alive even when there is nothing on the island.
    polls: Arc<AtomicU64>,
    /// Set when a spawn is in flight, so two overlapping calls cannot each
    /// start a child.
    starting: Arc<AtomicBool>,
    cached_threads: Vec<ThreadRecord>,
    cached_usage: Option<TokenUsage>,
    cached_limits: Option<RateLimits>,
    limits_updated_at_ms: Option<u64>,
    limits_attempt_at_ms: Option<u64>,
    limits_error: Option<String>,
    limits_stale: bool,
    threads_stale: bool,
    threads_updated_at_ms: Option<u64>,
    thread_connection_mode: Option<String>,
    /// Ticked by the caller; the provider re-reads only when due.
    since_last_read: std::time::Duration,
    last_read: Option<std::time::Instant>,
    retry_after: Option<std::time::Instant>,
    cancel: Arc<AtomicBool>,
    next_shared_probe: Option<std::time::Instant>,
}

impl Default for CodexProvider {
    fn default() -> Self {
        Self {
            enabled: false,
            server: None,
            last_reason: None,
            polls: Arc::new(AtomicU64::new(0)),
            starting: Arc::new(AtomicBool::new(false)),
            cached_threads: Vec::new(),
            cached_usage: None,
            cached_limits: None,
            limits_updated_at_ms: None,
            limits_attempt_at_ms: None,
            limits_error: None,
            limits_stale: false,
            threads_stale: false,
            threads_updated_at_ms: None,
            thread_connection_mode: None,
            since_last_read: POLL_INTERVAL,
            last_read: None,
            retry_after: None,
            cancel: Arc::new(AtomicBool::new(false)),
            next_shared_probe: None,
        }
    }
}

impl CodexProvider {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn set_cancel(&mut self, cancel: Arc<AtomicBool>) {
        self.cancel = cancel;
    }

    pub fn request_refresh(&mut self) {
        self.since_last_read = POLL_INTERVAL;
        self.retry_after = None;
        if self.server.as_ref().is_some_and(|s| !s.shared()) {
            self.next_shared_probe = Some(std::time::Instant::now());
        }
    }

    /// Whether a child process is currently running.
    pub fn is_connected(&self) -> bool {
        self.server.is_some()
    }

    pub fn polls(&self) -> u64 {
        self.polls.load(Ordering::Relaxed)
    }

    /// Copy of what was last read. Used by the settings-page preview path, which
    /// must not touch the live connection.
    pub fn cached(&self) -> (Vec<ThreadRecord>, Option<TokenUsage>, Option<RateLimits>) {
        (
            self.cached_threads.clone(),
            self.cached_usage,
            self.cached_limits,
        )
    }

    pub fn account_snapshot(&self) -> worker::AccountSnapshot {
        let mut snapshot = worker::AccountSnapshot {
            limits: self.cached_limits,
            updated_at_ms: self.limits_updated_at_ms,
            attempt_at_ms: self.limits_attempt_at_ms,
            error: self.limits_error.clone(),
            poll_interval_seconds: POLL_INTERVAL.as_secs(),
            connection_mode: self.server.as_ref().map(|s| {
                if s.shared() {
                    "shared".into()
                } else {
                    "accountOnly".into()
                }
            }),
            working_threads: self
                .cached_threads
                .iter()
                .filter(|t| t.status == protocol::ThreadStatus::Active)
                .count(),
            waiting_threads: self
                .cached_threads
                .iter()
                .filter(|t| t.status.needs_the_user())
                .count(),
            stale: self.limits_stale,
            threads_stale: self.threads_stale,
            threads_updated_at_ms: self.threads_updated_at_ms,
            thread_connection_mode: self.thread_connection_mode.clone(),
            observed_state: self.threads_updated_at_ms.map(|_| {
                if self
                    .cached_threads
                    .iter()
                    .any(|t| t.status == protocol::ThreadStatus::Active)
                {
                    "工作中"
                } else if self
                    .cached_threads
                    .iter()
                    .any(|t| t.status.needs_the_user())
                {
                    "等待处理"
                } else if self
                    .cached_threads
                    .iter()
                    .any(|t| t.status == protocol::ThreadStatus::SystemError)
                {
                    "存在异常"
                } else {
                    "空闲"
                }
                .into()
            }),
        };
        snapshot.refresh_freshness(crate::activity::now_millis());
        snapshot
    }

    pub(crate) fn restore_snapshot(
        &mut self,
        threads: Vec<ThreadRecord>,
        usage: Option<TokenUsage>,
        limits: Option<RateLimits>,
        account: &worker::AccountSnapshot,
    ) {
        self.cached_threads = threads;
        self.cached_usage = usage;
        self.cached_limits = limits;
        self.limits_updated_at_ms = account.updated_at_ms;
        self.limits_attempt_at_ms = account.attempt_at_ms;
        self.limits_error = account.error.clone();
        self.threads_updated_at_ms = account.threads_updated_at_ms;
        self.thread_connection_mode = account.thread_connection_mode.clone();
        self.limits_stale = limits.is_some();
        self.threads_stale = account.threads_updated_at_ms.is_some();
    }

    fn apply_account_read(&mut self, result: Result<serde_json::Value, String>, now_ms: u64) {
        self.limits_attempt_at_ms = Some(now_ms);
        self.limits_error = None;
        match result {
            Ok(value) => {
                let mut limits = RateLimits::parse(&value);
                if limits.primary.is_some() || limits.secondary.is_some() {
                    let mut inherited = false;
                    if let Some(old) = self.cached_limits {
                        let primary = retain_missing_window(limits.primary, old.primary);
                        let secondary = retain_missing_window(limits.secondary, old.secondary);
                        limits.primary = primary.0;
                        limits.secondary = secondary.0;
                        inherited = primary.1 || secondary.1;
                    }
                    self.cached_limits = Some(limits);
                    self.limits_stale = inherited;
                    if inherited {
                        self.limits_error = Some("部分额度字段未刷新；保留上次数据".into());
                    } else {
                        self.limits_updated_at_ms = Some(now_ms);
                    }
                } else {
                    self.limits_stale = self.cached_limits.is_some();
                    self.limits_error = Some("接口未返回 Codex 额度信息".into());
                }
            }
            Err(_) => {
                self.limits_stale = self.cached_limits.is_some();
                // Do not expose an RPC response body or credentials in the UI.
                self.limits_error = Some("额度读取失败，可刷新重试".into());
                diagnostics::warning("codex", "account/rateLimits/read failed");
            }
        }
    }

    /// Read thread state and account figures over the existing connection.
    ///
    /// A read failure tears the connection down instead of retrying on a
    /// possibly half-dead child. The next tick re-spawns cleanly, which is a
    /// more reliable recovery than trying to reason about a broken pipe.
    fn read(&mut self) -> Result<(), String> {
        let Some(server) = self.server.as_mut() else {
            return Err("app-server is not running".to_string());
        };

        // Only this owned app-server's loaded threads have meaningful runtime status.
        // A disk-history thread/list is not a monitor of another Codex desktop.
        let shared = server.shared();
        let list = if shared {
            server.request("thread/loaded/list", serde_json::json!({}))?
        } else {
            serde_json::json!({"data":[]})
        };
        let mut records = Vec::new();
        let ids = list["data"]
            .as_array()
            .ok_or("thread list response was invalid")?;
        for value in ids.iter().take(32) {
            let id = value.as_str().ok_or("thread identifier was invalid")?;
            let value = server.request(
                "thread/read",
                serde_json::json!({"threadId":id,"includeTurns":false}),
            )?;
            let record =
                ThreadRecord::parse(&value["thread"]).ok_or("thread read response was invalid")?;
            if record.status == protocol::ThreadStatus::Unknown {
                return Err("thread status was unavailable".into());
            }
            records.push(record);
        }

        // Account figures are best effort. A server that does not implement
        // them yields `None`, and the island simply omits that row.
        let limits = server.request(methods::ACCOUNT_RATE_LIMITS, serde_json::json!({}));

        if shared {
            self.cached_threads = records;
            self.threads_updated_at_ms = Some(crate::activity::now_millis());
            self.thread_connection_mode = Some("shared".into());
            self.threads_stale = false;
        } else {
            self.threads_stale = self.threads_updated_at_ms.is_some();
        }
        // Quota percentages are not a thread's token/context usage.
        self.cached_usage = None;
        self.apply_account_read(limits, crate::activity::now_millis());
        self.last_read = Some(std::time::Instant::now());
        Ok(())
    }

    /// Bring the child up. Separate from `Provider::start` so a reconnect can
    /// reuse it without re-reading settings.
    fn connect(&mut self) -> ProviderStatus {
        if self.server.is_some() {
            return self.status();
        }
        // A concurrent caller is already spawning; report progress rather than
        // starting a second child.
        if self.starting.swap(true, Ordering::SeqCst) {
            return ProviderStatus {
                kind: crate::activity::ProviderKind::Codex,
                health: ProviderHealth::Disconnected,
                detail: Some("connecting".to_string()),
                activity_count: 0,
            };
        }

        let status = match app_server::discover() {
            Discovery::NotFound => {
                self.last_reason = Some(
                    "The Codex CLI was not found, so thread state is unavailable.".to_string(),
                );
                diagnostics::information(
                    "codex",
                    "Codex CLI not found; provider reports unavailable",
                );
                ProviderStatus {
                    kind: crate::activity::ProviderKind::Codex,
                    health: ProviderHealth::Unavailable,
                    detail: self.last_reason.clone(),
                    activity_count: 0,
                }
            }
            Discovery::Found(executable) => {
                match AppServer::connect_observer(&executable, Arc::clone(&self.cancel)) {
                    Ok(server) => {
                        self.server = Some(server);
                        self.next_shared_probe =
                            Some(std::time::Instant::now() + std::time::Duration::from_secs(60));
                        self.last_reason = None;
                        diagnostics::information("codex", "app-server connected");
                        if let Err(error) = self.read() {
                            diagnostics::warning(
                                "codex",
                                &format!("connected but could not read thread state: {error}"),
                            );
                            self.last_reason = Some(error);
                            self.disconnect("initial read failed");
                        }
                        self.status()
                    }
                    Err(error) => {
                        self.last_reason = Some(error.clone());
                        diagnostics::warning("codex", &error);
                        ProviderStatus {
                            kind: crate::activity::ProviderKind::Codex,
                            health: ProviderHealth::Disconnected,
                            detail: Some(error),
                            activity_count: 0,
                        }
                    }
                }
            }
        };

        self.starting.store(false, Ordering::SeqCst);
        self.retry_after = Some(std::time::Instant::now() + POLL_INTERVAL * 3);
        status
    }

    /// Drop the transport, retaining last-known data as explicitly stale history.
    /// Cached task records never become live activities while disconnected.
    fn disconnect(&mut self, reason: &str) {
        if self.server.take().is_some() {
            diagnostics::information("codex", &format!("app-server disconnected: {reason}"));
        }
        self.limits_stale = self.cached_limits.is_some();
        self.threads_stale = self.threads_updated_at_ms.is_some();
        if self.cached_limits.is_some() {
            self.limits_error = Some("当前未连接；显示上次成功数据".into());
        }
        self.next_shared_probe = None;
    }
}

impl Provider for CodexProvider {
    fn start(&mut self) -> ProviderStatus {
        self.enabled = true;
        self.connect()
    }

    fn stop(&mut self) {
        // `stop` only releases the child. `enabled` is left alone, because
        // `stop` is also how the module shuts down, and that must not silently
        // rewrite the user's preference.
        self.disconnect("stopped");
    }

    fn set_enabled(&mut self, enabled: bool) {
        self.enabled = enabled;
        if !enabled {
            self.disconnect("disabled by the user");
        }
    }

    fn poll(&mut self) -> Vec<LiveActivity> {
        self.polls.fetch_add(1, Ordering::Relaxed);
        if !self.enabled {
            return Vec::new();
        }

        // A child that died must not be polled; reconnecting is the next tick's
        // job, so this tick reports nothing rather than a stale reading.
        //
        // No transport means historical data, not an empty successful result.
        let alive = self.server.as_mut().is_some_and(AppServer::is_alive);
        if !alive {
            if self.server.is_some() {
                self.disconnect("child process exited");
            } else {
                self.limits_stale = self.cached_limits.is_some();
                self.threads_stale = self.threads_updated_at_ms.is_some();
            }
            if self
                .retry_after
                .map_or(true, |at| std::time::Instant::now() >= at)
                && !self.cancel.load(Ordering::Relaxed)
            {
                self.connect();
            }
            return Vec::new();
        }

        // Consume any notifications first: they are free, and they arrive
        // A newly opened shared endpoint can replace account-only collection
        // without restarting or changing the user's Codex instance.
        if self.server.as_ref().is_some_and(|s| !s.shared())
            && self
                .next_shared_probe
                .is_some_and(|at| std::time::Instant::now() >= at)
            && !self.cancel.load(Ordering::Relaxed)
        {
            self.next_shared_probe =
                Some(std::time::Instant::now() + std::time::Duration::from_secs(60));
            if let Discovery::Found(executable) = app_server::discover() {
                if let Ok(shared) = AppServer::spawn_proxy(&executable, Arc::clone(&self.cancel)) {
                    self.server = Some(shared);
                    self.threads_stale = self.threads_updated_at_ms.is_some();
                    self.since_last_read = POLL_INTERVAL;
                    diagnostics::information(
                        "codex",
                        "upgraded to supported shared control endpoint",
                    );
                }
            }
        }

        // Notifications arrive
        // between polls. They are not parsed into state here because a
        // notification carries only a delta; the periodic read is what produces
        // a complete picture. Draining them keeps the channel from filling.
        if let Some(server) = self.server.as_mut() {
            let pending = server.drain_notifications(16);
            if !pending.is_empty() {
                let recognised = pending
                    .iter()
                    .filter(|frame| {
                        frame
                            .get("method")
                            .and_then(serde_json::Value::as_str)
                            .is_some_and(|method| {
                                NOTIFICATION_PREFIXES
                                    .iter()
                                    .any(|prefix| method.starts_with(prefix))
                            })
                    })
                    .count();
                if recognised > 0 {
                    // A real status change is worth reading immediately instead
                    // of waiting for the interval.
                    self.since_last_read = POLL_INTERVAL;
                }
            }
        }

        if self
            .last_read
            .map_or(true, |at| at.elapsed() >= POLL_INTERVAL)
        {
            self.since_last_read = POLL_INTERVAL;
        }
        if self.since_last_read >= POLL_INTERVAL {
            self.since_last_read = std::time::Duration::ZERO;
            if let Err(error) = self.read() {
                // A failed read discards stale state; retry is rate-limited.
                diagnostics::warning("codex", &format!("thread read failed: {error}"));
                self.last_reason = Some(error);
                self.retry_after = Some(std::time::Instant::now() + POLL_INTERVAL * 3);
                self.disconnect("read failed");
                return Vec::new();
            }
        }

        if self.threads_stale {
            return Vec::new();
        }
        threads::collect(
            &self.cached_threads,
            self.cached_usage.as_ref(),
            self.cached_limits.as_ref(),
        )
    }

    fn status(&self) -> ProviderStatus {
        let kind = crate::activity::ProviderKind::Codex;
        if !self.enabled {
            return crate::providers::disabled_status(kind, "Codex integration is off");
        }
        if let Some(reason) = self
            .last_reason
            .as_deref()
            .filter(|_| self.server.is_none())
        {
            let health = if reason.contains("was not found") {
                ProviderHealth::Unavailable
            } else {
                ProviderHealth::Disconnected
            };
            return ProviderStatus {
                kind,
                health,
                detail: Some(reason.to_string()),
                activity_count: 0,
            };
        }
        if self.server.is_some() {
            return ProviderStatus {
                kind,
                health: ProviderHealth::Connected,
                detail: Some(format!("{} threads observed", self.cached_threads.len())),
                activity_count: if self.threads_stale {
                    0
                } else {
                    self.cached_threads
                        .iter()
                        .filter(|t| threads::is_visible(t))
                        .count()
                },
            };
        }
        ProviderStatus {
            kind,
            health: ProviderHealth::Disconnected,
            detail: Some("not connected".to_string()),
            activity_count: 0,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::activity::ProviderKind;

    #[test]
    fn explicit_refresh_clears_backoff_and_marks_snapshot_due() {
        let mut provider = CodexProvider::new();
        provider.retry_after = Some(std::time::Instant::now() + POLL_INTERVAL * 3);
        provider.since_last_read = std::time::Duration::ZERO;
        provider.request_refresh();
        assert!(provider.retry_after.is_none());
        assert_eq!(provider.since_last_read, POLL_INTERVAL);
        assert!(!provider.is_connected());
    }

    #[test]
    fn a_disabled_provider_starts_nothing_and_reports_why() {
        let mut provider = CodexProvider::new();
        let status = provider.status();
        assert_eq!(status.health, ProviderHealth::Disabled);
        assert_eq!(status.kind, ProviderKind::Codex);
        assert!(!provider.is_connected());
        // Polling while disabled must not attempt a spawn.
        assert!(provider.poll().is_empty());
    }

    #[test]
    fn disabling_preserves_history_without_publishing_live_activities() {
        let mut provider = CodexProvider::new();
        provider.enabled = true;
        // Stand in for a populated cache without needing a live child.
        provider.cached_threads =
            vec![
                ThreadRecord::parse(&serde_json::json!({"id": "t1", "status": "active"}))
                    .expect("record"),
            ];
        provider.cached_usage = Some(TokenUsage::parse(&serde_json::json!({"inputTokens": 10})));
        provider.threads_updated_at_ms = Some(crate::activity::now_millis());
        provider.thread_connection_mode = Some("shared".into());

        provider.set_enabled(false);
        assert!(!provider.is_connected());
        let (threads, usage, limits) = provider.cached();
        assert_eq!(threads.len(), 1);
        assert!(usage.is_some());
        assert!(limits.is_none());
        assert!(provider.account_snapshot().threads_stale);
        assert!(provider.poll().is_empty());
    }

    #[test]
    fn a_missing_cli_is_reported_as_unavailable_not_as_an_error() {
        let mut provider = CodexProvider::new();
        provider.enabled = true;
        // Simulate the discovery outcome directly: the point under test is the
        // status mapping, not whether this machine happens to have the CLI.
        provider.last_reason =
            Some("The Codex CLI was not found, so thread state is unavailable.".to_string());
        let status = provider.status();
        assert_eq!(
            status.health,
            ProviderHealth::Unavailable,
            "a missing optional dependency is a normal state, not a failure"
        );
        assert!(status.detail.is_some());
    }

    #[test]
    fn a_start_failure_is_reported_as_disconnected_with_its_reason() {
        let mut provider = CodexProvider::new();
        provider.enabled = true;
        provider.last_reason = Some("could not start codex app-server: access denied".to_string());
        let status = provider.status();
        assert_eq!(status.health, ProviderHealth::Disconnected);
        assert!(status
            .detail
            .as_deref()
            .unwrap_or_default()
            .contains("access denied"));
    }

    #[test]
    fn a_dead_child_keeps_history_but_does_not_emit_running_activities() {
        let mut provider = CodexProvider::new();
        provider.enabled = true;
        provider.retry_after = Some(std::time::Instant::now() + POLL_INTERVAL);
        provider.cached_threads =
            vec![
                ThreadRecord::parse(&serde_json::json!({"id": "t1", "status": "active"}))
                    .expect("record"),
            ];
        // No server attached: `poll` must notice there is nothing alive to ask,
        // report nothing, and not invent a reading from the stale cache.
        assert!(provider.poll().is_empty());
        assert_eq!(provider.cached().0.len(), 1);
    }

    #[test]
    fn polling_advances_the_counter_so_the_settings_page_can_show_liveness() {
        let mut provider = CodexProvider::new();
        assert_eq!(provider.polls(), 0);
        provider.poll();
        provider.poll();
        assert_eq!(provider.polls(), 2);
    }

    #[test]
    fn stopping_does_not_rewrite_the_users_preference() {
        let mut provider = CodexProvider::new();
        provider.set_enabled(true);
        provider.enabled = true;
        provider.stop();
        // `stop` is also the shutdown path, so it must leave `enabled` intact.
        assert!(
            provider.enabled,
            "shutdown must not silently disable the provider"
        );
        assert!(!provider.is_connected());
    }

    #[test]
    fn recognised_notification_prefixes_cover_the_documented_methods() {
        for method in [
            "thread/status/changed",
            "thread/tokenUsage/updated",
            "thread/status",
            "account/rateLimits/updated",
        ] {
            assert!(
                NOTIFICATION_PREFIXES
                    .iter()
                    .any(|prefix| method.starts_with(prefix)),
                "{method} should be recognised"
            );
        }
        // An unrelated notification must not be mistaken for a status change.
        assert!(!NOTIFICATION_PREFIXES
            .iter()
            .any(|prefix| "account/login/completed".starts_with(prefix)));
    }

    #[test]
    fn quota_read_retains_values_and_success_time_until_the_next_success() {
        let mut provider = CodexProvider::new();
        let now = crate::activity::now_millis();
        provider.apply_account_read(
            Ok(serde_json::json!({"primary":{"usedPercent":30,"resetsAt":1800000000}})),
            now,
        );
        let snapshot = provider.account_snapshot();
        assert_eq!(snapshot.updated_at_ms, Some(now));
        assert_eq!(snapshot.attempt_at_ms, Some(now));
        assert!(!snapshot.stale);
        assert_eq!(
            snapshot
                .limits
                .unwrap()
                .primary
                .unwrap()
                .remaining_percent(),
            Some(70.0)
        );
        assert!(snapshot.error.is_none());
        provider.apply_account_read(Err("a raw response must stay private".into()), now + 1);
        let failed = provider.account_snapshot();
        assert_eq!(failed.limits, snapshot.limits);
        assert_eq!(failed.updated_at_ms, Some(now));
        assert_eq!(failed.attempt_at_ms, Some(now + 1));
        assert!(failed.stale);
        assert_eq!(failed.error.as_deref(), Some("额度读取失败，可刷新重试"));
        provider.apply_account_read(Ok(serde_json::json!({"rateLimits":null})), now + 2);
        assert_eq!(
            provider.account_snapshot().error.as_deref(),
            Some("接口未返回 Codex 额度信息")
        );
        assert_eq!(provider.account_snapshot().limits, snapshot.limits);
        provider.set_enabled(false);
        assert!(provider.account_snapshot().stale);
        assert_eq!(provider.account_snapshot().limits, snapshot.limits);
        assert_eq!(provider.account_snapshot().updated_at_ms, Some(now));
        provider.apply_account_read(
            Ok(serde_json::json!({"primary":{"usedPercent":50,"resetsAt":1800000001}})),
            now + 3,
        );
        assert!(!provider.account_snapshot().stale);
        assert_eq!(provider.account_snapshot().updated_at_ms, Some(now + 3));
        assert_eq!(
            provider
                .account_snapshot()
                .limits
                .unwrap()
                .primary
                .unwrap()
                .remaining_percent(),
            Some(50.0)
        );
    }

    #[test]
    fn partial_quota_responses_preserve_missing_values_with_historical_labels() {
        let mut provider = CodexProvider::new();
        let now = crate::activity::now_millis();
        provider.apply_account_read(Ok(serde_json::json!({"primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1800000000},"secondary":{"usedPercent":50,"windowDurationMins":10080}})), now);
        provider.apply_account_read(
            Ok(serde_json::json!({"primary":{"resetsAt":1800000001}})),
            now + 1,
        );
        let partial = provider.account_snapshot();
        assert_eq!(
            partial.limits.unwrap().primary.unwrap().remaining_percent(),
            Some(75.0)
        );
        assert_eq!(
            partial
                .limits
                .unwrap()
                .secondary
                .unwrap()
                .remaining_percent(),
            Some(50.0)
        );
        assert_eq!(partial.updated_at_ms, Some(now));
        assert!(partial.stale);
        assert_eq!(
            partial.error.as_deref(),
            Some("部分额度字段未刷新；保留上次数据")
        );
        let changed = retain_missing_window(
            Some(RateLimitWindow {
                window_minutes: Some(60),
                ..Default::default()
            }),
            partial.limits.unwrap().primary,
        );
        assert!(changed.0.unwrap().used_fraction.is_none());
        assert!(!changed.1);
    }

    #[test]
    fn first_failure_does_not_invent_a_cached_quota_and_restore_keeps_provenance() {
        let mut provider = CodexProvider::new();
        provider.apply_account_read(
            Err("private response".into()),
            crate::activity::now_millis(),
        );
        assert!(provider.account_snapshot().limits.is_none());
        assert!(provider.account_snapshot().updated_at_ms.is_none());
        let saved = worker::AccountSnapshot {
            limits: Some(RateLimits::parse(&serde_json::json!({"usedPercent":20}))),
            updated_at_ms: Some(42),
            threads_updated_at_ms: Some(41),
            thread_connection_mode: Some("shared".into()),
            ..Default::default()
        };
        provider.restore_snapshot(
            vec![ThreadRecord::parse(&serde_json::json!({"id":"t","status":"active"})).unwrap()],
            None,
            saved.limits,
            &saved,
        );
        let restored = provider.account_snapshot();
        assert_eq!(restored.updated_at_ms, Some(42));
        assert_eq!(restored.working_threads, 1);
        assert!(restored.stale && restored.threads_stale);
        assert!(restored.connection_mode.is_none());
        assert_eq!(restored.thread_connection_mode.as_deref(), Some("shared"));
    }

    #[test]
    fn the_poll_interval_is_slow_enough_to_be_polite() {
        // Guards against someone "optimising" responsiveness by polling a
        // resident child process many times a second.
        assert!(POLL_INTERVAL >= std::time::Duration::from_secs(5));
        assert!(POLL_INTERVAL <= std::time::Duration::from_secs(30));
    }

    #[test]
    fn provider_metadata_never_carries_a_user_secret() {
        let mut provider = CodexProvider::new();
        provider.enabled = true;
        provider.last_reason = Some("connection refused".to_string());
        let status = provider.status();
        let serialized = serde_json::to_string(&status).expect("serialise");
        for forbidden in ["sk-", "Bearer ", "token="] {
            assert!(
                !serialized.contains(forbidden),
                "provider status must not contain {forbidden}"
            );
        }
    }
}
