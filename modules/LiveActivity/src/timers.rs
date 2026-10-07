//! Monotonic timers owned by the module, never by the settings WebView.
use serde::{Deserialize, Serialize};
use std::time::Instant;

pub const MAX_COUNTDOWN_SECONDS: u64 = 604_800;

#[derive(Debug, Clone, Copy, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum TimerKind {
    Stopwatch,
    Countdown,
}
#[derive(Debug, Clone, Copy, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Action {
    Start,
    Pause,
    Reset,
}
#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Command {
    pub kind: TimerKind,
    pub action: Action,
}

#[derive(Default)]
struct Clock {
    elapsed_ms: u64,
    started_ms: Option<u64>,
    started_once: bool,
    finished: bool,
}
impl Clock {
    fn elapsed(&self, now: u64) -> u64 {
        self.elapsed_ms.saturating_add(
            self.started_ms
                .map(|at| now.saturating_sub(at))
                .unwrap_or(0),
        )
    }
    fn pause(&mut self, now: u64) {
        self.elapsed_ms = self.elapsed(now);
        self.started_ms = None;
    }
    fn apply(&mut self, action: Action, now: u64) {
        match action {
            Action::Start if self.started_ms.is_none() => {
                if self.finished {
                    *self = Self::default();
                }
                self.started_once = true;
                self.started_ms = Some(now);
            }
            Action::Pause => self.pause(now),
            Action::Reset => *self = Self::default(),
            _ => {}
        }
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ClockView {
    pub running: bool,
    pub started: bool,
    pub finished: bool,
    pub seconds: u64,
    pub text: String,
}
#[derive(Serialize)]
pub struct Snapshot {
    pub stopwatch: ClockView,
    pub countdown: ClockView,
}
pub struct Timers {
    epoch: Instant,
    stopwatch: Clock,
    countdown: Clock,
}
impl Default for Timers {
    fn default() -> Self {
        Self {
            epoch: Instant::now(),
            stopwatch: Clock::default(),
            countdown: Clock::default(),
        }
    }
}
pub fn format_seconds(seconds: u64) -> String {
    format!(
        "{:02}:{:02}:{:02}",
        seconds / 3600,
        seconds / 60 % 60,
        seconds % 60
    )
}
impl Timers {
    fn now(&self) -> u64 {
        self.epoch.elapsed().as_millis().min(u64::MAX as u128) as u64
    }
    pub fn command(&mut self, command: Command) {
        self.command_at(command, self.now());
    }
    fn command_at(&mut self, command: Command, now: u64) {
        match command.kind {
            TimerKind::Stopwatch => self.stopwatch.apply(command.action, now),
            TimerKind::Countdown => self.countdown.apply(command.action, now),
        }
    }
    pub fn pause_all(&mut self) {
        let now = self.now();
        self.stopwatch.pause(now);
        self.countdown.pause(now);
    }
    pub fn reset_countdown(&mut self) {
        self.countdown = Clock::default();
    }
    pub fn running(&self) -> bool {
        self.stopwatch.started_ms.is_some() || self.countdown.started_ms.is_some()
    }
    pub fn tick(&mut self, duration: u64) -> bool {
        self.tick_at(duration, self.now())
    }
    fn tick_at(&mut self, duration: u64, now: u64) -> bool {
        if self.countdown.started_ms.is_some() && self.countdown.elapsed(now) >= duration * 1000 {
            self.countdown.elapsed_ms = duration * 1000;
            self.countdown.started_ms = None;
            self.countdown.finished = true;
            return true;
        }
        false
    }
    pub fn snapshot(&self, duration: u64) -> Snapshot {
        self.snapshot_at(duration, self.now())
    }
    fn snapshot_at(&self, duration: u64, now: u64) -> Snapshot {
        let elapsed = self.stopwatch.elapsed(now) / 1000;
        // Round up remaining seconds, so 00:00:00 means truly finished.
        let remaining = (duration * 1000)
            .saturating_sub(self.countdown.elapsed(now))
            .div_ceil(1000);
        let view = |c: &Clock, seconds| ClockView {
            running: c.started_ms.is_some(),
            started: c.started_once,
            finished: c.finished,
            seconds,
            text: format_seconds(seconds),
        };
        Snapshot {
            stopwatch: view(&self.stopwatch, elapsed),
            countdown: view(&self.countdown, remaining),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn cmd(kind: TimerKind, action: Action) -> Command {
        Command { kind, action }
    }
    #[test]
    fn pause_resume_and_reset_do_not_drift_or_double_start() {
        let mut t = Timers::default();
        t.command_at(cmd(TimerKind::Stopwatch, Action::Start), 100);
        t.command_at(cmd(TimerKind::Stopwatch, Action::Start), 1000);
        t.command_at(cmd(TimerKind::Stopwatch, Action::Pause), 2100);
        assert_eq!(t.snapshot_at(60, 9999).stopwatch.seconds, 2);
        t.command_at(cmd(TimerKind::Stopwatch, Action::Start), 10000);
        assert_eq!(t.snapshot_at(60, 11000).stopwatch.seconds, 3);
        t.command_at(cmd(TimerKind::Stopwatch, Action::Reset), 12000);
        assert!(!t.snapshot_at(60, 12000).stopwatch.started);
    }
    #[test]
    fn countdown_rounds_up_finishes_once_and_restarts_cleanly() {
        let mut t = Timers::default();
        t.command_at(cmd(TimerKind::Countdown, Action::Start), 0);
        assert_eq!(t.snapshot_at(2, 1001).countdown.seconds, 1);
        assert!(t.tick_at(2, 2000));
        assert!(!t.tick_at(2, 9000));
        let s = t.snapshot_at(2, 9000);
        assert!(s.countdown.finished);
        assert_eq!(s.countdown.text, "00:00:00");
        t.command_at(cmd(TimerKind::Countdown, Action::Start), 9000);
        assert_eq!(t.snapshot_at(2, 9000).countdown.seconds, 2);
        assert!(!t.snapshot_at(2, 9000).countdown.finished);
    }
}
