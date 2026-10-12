//! Own the blocking Codex transport outside the host RPC and overlay threads.
use std::sync::{
    atomic::{AtomicBool, Ordering},
    mpsc, Arc, Condvar, Mutex,
};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use super::{
    protocol::{RateLimits, ThreadRecord, TokenUsage},
    CodexProvider,
};
use crate::{
    activity::{LiveActivity, ProviderKind},
    providers::{disabled_status, Provider, ProviderStatus},
};

#[derive(Debug, Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AccountSnapshot {
    pub limits: Option<RateLimits>,
    pub updated_at_ms: Option<u64>,
    pub attempt_at_ms: Option<u64>,
    pub error: Option<String>,
    pub poll_interval_seconds: u64,
    pub connection_mode: Option<String>,
    pub working_threads: usize,
    pub waiting_threads: usize,
    pub stale: bool,
    pub threads_stale: bool,
    pub threads_updated_at_ms: Option<u64>,
    /// Provenance of the last successful task observation, not the live connection.
    pub thread_connection_mode: Option<String>,
    pub observed_state: Option<String>,
}
impl Default for AccountSnapshot {
    fn default() -> Self {
        Self {
            limits: None,
            updated_at_ms: None,
            attempt_at_ms: None,
            error: None,
            poll_interval_seconds: super::POLL_INTERVAL.as_secs(),
            connection_mode: None,
            working_threads: 0,
            waiting_threads: 0,
            stale: false,
            threads_stale: false,
            threads_updated_at_ms: None,
            thread_connection_mode: None,
            observed_state: None,
        }
    }
}

impl AccountSnapshot {
    pub fn mark_stale(&mut self, reason: &str) {
        self.stale = self.limits.is_some();
        self.threads_stale = self.threads_updated_at_ms.is_some();
        self.connection_mode = None;
        if self.limits.is_some() {
            self.error = Some(reason.into());
        }
    }

    pub fn refresh_freshness(&mut self, now_ms: u64) {
        let max_age = super::POLL_INTERVAL.as_millis() as u64 * 3;
        self.stale |= self
            .updated_at_ms
            .is_some_and(|at| now_ms.saturating_sub(at) > max_age);
        self.threads_stale |= self
            .threads_updated_at_ms
            .is_some_and(|at| now_ms.saturating_sub(at) > max_age);
    }
}

#[derive(Clone)]
struct Snapshot {
    status: ProviderStatus,
    connected: bool,
    threads: Vec<ThreadRecord>,
    usage: Option<TokenUsage>,
    limits: Option<RateLimits>,
    account: AccountSnapshot,
    activities: Vec<LiveActivity>,
}
impl Default for Snapshot {
    fn default() -> Self {
        Self {
            status: disabled_status(ProviderKind::Codex, "Codex integration is off"),
            connected: false,
            threads: Vec::new(),
            usage: None,
            limits: None,
            account: AccountSnapshot::default(),
            activities: Vec::new(),
        }
    }
}

impl Snapshot {
    fn pause(&mut self, reason: &str) {
        self.connected = false;
        self.activities.clear();
        self.account.mark_stale(reason);
        self.status = disabled_status(ProviderKind::Codex, reason);
    }
}

pub struct ManagedCodex {
    enabled: bool,
    idle_seconds: u64,
    snapshot: Arc<Mutex<Snapshot>>,
    worker: Option<JoinHandle<()>>,
    commands: Option<mpsc::Sender<u64>>,
    stop: Arc<AtomicBool>,
    wake: Arc<Condvar>,
    #[cfg(test)]
    simulated: bool,
}
impl ManagedCodex {
    pub fn new(wake: Arc<Condvar>) -> Self {
        Self {
            enabled: false,
            idle_seconds: 300,
            snapshot: Arc::new(Mutex::new(Snapshot::default())),
            worker: None,
            commands: None,
            stop: Arc::new(AtomicBool::new(false)),
            wake,
            #[cfg(test)]
            simulated: false,
        }
    }
    pub fn configure(&mut self, enabled: bool, idle_seconds: u64) {
        let changed = self.idle_seconds != idle_seconds;
        self.idle_seconds = idle_seconds;
        self.enabled = enabled;
        if !enabled {
            self.stop.store(true, Ordering::Relaxed);
            if let Some(tx) = self.commands.take() {
                let _ = tx.send(0);
            }
            if let Some(worker) = self.worker.take() {
                let _ = worker.join();
            }
            self.snapshot
                .lock()
                .unwrap_or_else(|p| p.into_inner())
                .pause("采集已停止；保留上次成功数据");
        } else if changed {
            self.refresh();
        }
    }
    pub fn refresh(&mut self) {
        if self.enabled {
            if let Some(tx) = &self.commands {
                let _ = tx.send(self.idle_seconds);
            }
        }
    }
    fn ensure_worker(&mut self) {
        #[cfg(test)]
        if self.simulated {
            return;
        }
        if !self.enabled || self.worker.is_some() {
            return;
        }
        self.stop = Arc::new(AtomicBool::new(false));
        let stop = Arc::clone(&self.stop);
        let shared = Arc::clone(&self.snapshot);
        let wake = Arc::clone(&self.wake);
        let (tx, rx) = mpsc::channel();
        let mut idle_seconds = self.idle_seconds;
        self.commands = Some(tx);
        self.worker = Some(std::thread::spawn(move || {
            let mut provider = CodexProvider::new();
            let saved = shared.lock().unwrap_or_else(|p| p.into_inner()).clone();
            provider.restore_snapshot(saved.threads, saved.usage, saved.limits, &saved.account);
            provider.set_cancel(Arc::clone(&stop));
            provider.set_enabled(true);
            let mut idle_since = Instant::now();
            let mut paused = false;
            while !stop.load(Ordering::Relaxed) {
                if !paused {
                    let activities = provider.poll();
                    let (threads, usage, limits) = provider.cached();
                    if !activities.is_empty() {
                        idle_since = Instant::now();
                    }
                    *shared.lock().unwrap_or_else(|p| p.into_inner()) = Snapshot {
                        status: provider.status(),
                        connected: provider.is_connected(),
                        threads,
                        usage,
                        limits,
                        account: provider.account_snapshot(),
                        activities,
                    };
                    wake.notify_all();
                    if idle_seconds != 0
                        && idle_since.elapsed() >= Duration::from_secs(idle_seconds)
                    {
                        provider.stop();
                        let mut snapshot = shared.lock().unwrap_or_else(|p| p.into_inner());
                        snapshot.pause("空闲已暂停；保留上次数据，刷新可重新连接");
                        paused = true;
                        wake.notify_all();
                    }
                }
                let command = if paused {
                    rx.recv().map_err(|_| mpsc::RecvTimeoutError::Disconnected)
                } else {
                    rx.recv_timeout(Duration::from_secs(1))
                };
                match command {
                    Ok(seconds) => {
                        idle_seconds = seconds;
                        paused = false;
                        idle_since = Instant::now();
                        provider.request_refresh();
                    }
                    Err(mpsc::RecvTimeoutError::Disconnected) => break,
                    Err(mpsc::RecvTimeoutError::Timeout) => {}
                }
            }
            provider.stop();
        }));
    }
    pub fn poll(&mut self) -> Vec<LiveActivity> {
        self.ensure_worker();
        let mut snapshot = self.snapshot.lock().unwrap_or_else(|p| p.into_inner());
        snapshot
            .account
            .refresh_freshness(crate::activity::now_millis());
        if snapshot.account.threads_stale {
            Vec::new()
        } else {
            snapshot.activities.clone()
        }
    }
    pub fn is_connected(&self) -> bool {
        self.snapshot
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .connected
    }
    pub fn status(&self) -> ProviderStatus {
        let snapshot = self.snapshot.lock().unwrap_or_else(|p| p.into_inner());
        let mut account = snapshot.account.clone();
        account.refresh_freshness(crate::activity::now_millis());
        let mut status = snapshot.status.clone();
        if account.threads_stale {
            status.activity_count = 0;
        }
        status
    }
    pub fn cached(&self) -> (Vec<ThreadRecord>, Option<TokenUsage>, Option<RateLimits>) {
        let value = self.snapshot.lock().unwrap_or_else(|p| p.into_inner());
        (value.threads.clone(), value.usage, value.limits)
    }
    pub fn account_snapshot(&self) -> AccountSnapshot {
        let mut account = self
            .snapshot
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .account
            .clone();
        account.refresh_freshness(crate::activity::now_millis());
        account
    }

    #[cfg(test)]
    pub fn set_limits_for_test(&mut self, limits: Option<RateLimits>) {
        self.simulated = true;
        let mut snapshot = self.snapshot.lock().unwrap_or_else(|p| p.into_inner());
        snapshot.limits = limits;
        snapshot.account.limits = limits;
        snapshot.account.updated_at_ms = limits.map(|_| crate::activity::now_millis());
    }

    #[cfg(test)]
    pub fn configured_for_test(&self) -> bool {
        self.enabled
    }
}
impl Drop for ManagedCodex {
    fn drop(&mut self) {
        self.configure(false, 0);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn enable_configuration_alone_does_not_spawn_or_block_the_host() {
        let mut provider = ManagedCodex::new(Arc::new(Condvar::new()));
        provider.configure(true, 10);
        assert!(provider.worker.is_none());
        provider.configure(false, 10);
        assert!(!provider.is_connected());
    }

    #[test]
    fn pause_and_restart_preserve_quota_and_stamp_without_live_tasks() {
        let mut provider = ManagedCodex::new(Arc::new(Condvar::new()));
        let limits = RateLimits::parse(&serde_json::json!({"usedPercent":25}));
        provider.set_limits_for_test(Some(limits));
        let stamp = provider.account_snapshot().updated_at_ms;
        provider.configure(false, 0);
        let cached = provider.account_snapshot();
        assert_eq!(cached.limits, Some(limits));
        assert_eq!(cached.updated_at_ms, stamp);
        assert!(cached.stale);
        assert!(provider.poll().is_empty());
        provider.configure(true, 0);
        assert_eq!(provider.account_snapshot().limits, Some(limits));
    }

    #[test]
    fn stalled_worker_data_becomes_historical_without_disappearing() {
        let mut account = AccountSnapshot {
            limits: Some(RateLimits::parse(&serde_json::json!({"usedPercent":25}))),
            updated_at_ms: Some(100),
            threads_updated_at_ms: Some(100),
            ..Default::default()
        };
        account.refresh_freshness(30101);
        assert!(account.stale && account.threads_stale);
        assert!(account.limits.is_some());
        assert_eq!(account.updated_at_ms, Some(100));
    }

    #[test]
    fn stalled_task_snapshot_is_not_republished_as_current_work() {
        let mut provider = ManagedCodex::new(Arc::new(Condvar::new()));
        provider.simulated = true;
        {
            let mut snapshot = provider.snapshot.lock().unwrap();
            snapshot.account.threads_updated_at_ms = Some(crate::activity::now_millis());
            snapshot.account.working_threads = 1;
            snapshot.status.activity_count = 1;
            snapshot.activities.push(LiveActivity::running(
                "t",
                ProviderKind::Codex,
                "task",
                "task",
            ));
        }
        assert_eq!(provider.poll().len(), 1);
        provider
            .snapshot
            .lock()
            .unwrap()
            .account
            .threads_updated_at_ms = Some(1);
        assert!(provider.poll().is_empty());
        assert_eq!(provider.status().activity_count, 0);
        assert_eq!(provider.account_snapshot().working_threads, 1);
        assert!(provider.account_snapshot().threads_stale);
    }
}
