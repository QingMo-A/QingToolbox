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
}
impl Default for AccountSnapshot {
    fn default() -> Self {
        Self {
            limits: None,
            updated_at_ms: None,
            attempt_at_ms: None,
            error: None,
            poll_interval_seconds: super::POLL_INTERVAL.as_secs(),
        }
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

pub struct ManagedCodex {
    enabled: bool,
    idle_seconds: u64,
    snapshot: Arc<Mutex<Snapshot>>,
    worker: Option<JoinHandle<()>>,
    commands: Option<mpsc::Sender<u64>>,
    stop: Arc<AtomicBool>,
    wake: Arc<Condvar>,
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
            *self.snapshot.lock().unwrap_or_else(|p| p.into_inner()) = Snapshot::default();
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
                        *snapshot = Snapshot::default();
                        snapshot.status =
                            disabled_status(ProviderKind::Codex, "空闲已暂停；刷新可重新连接");
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
        self.snapshot
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .activities
            .clone()
    }
    pub fn is_connected(&self) -> bool {
        self.snapshot
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .connected
    }
    pub fn status(&self) -> ProviderStatus {
        self.snapshot
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .status
            .clone()
    }
    pub fn cached(&self) -> (Vec<ThreadRecord>, Option<TokenUsage>, Option<RateLimits>) {
        let value = self.snapshot.lock().unwrap_or_else(|p| p.into_inner());
        (value.threads.clone(), value.usage, value.limits)
    }
    pub fn account_snapshot(&self) -> AccountSnapshot {
        self.snapshot
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .account
            .clone()
    }

    #[cfg(test)]
    pub fn set_limits_for_test(&mut self, limits: Option<RateLimits>) {
        self.snapshot
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .limits = limits;
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
}
