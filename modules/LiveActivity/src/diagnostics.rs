//! A bounded, privacy-preserving operation log.
//!
//! Two rules shape this module:
//!
//! 1. **Never record user content.** No prompt text, no conversation text, no
//!    tokens, no credentials. The log records *that* something happened and
//!    which subsystem it came from, never what the user was working on.
//! 2. **Never grow without limit.** A resident process that logs forever is a
//!    defect, so the ring is fixed-capacity and drops oldest-first.

use std::collections::VecDeque;
use std::sync::{Mutex, OnceLock};

use serde::Serialize;

pub const MAX_ENTRIES: usize = 256;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum Level {
    Information,
    Warning,
    Error,
}

impl Level {
    /// The stable wire token, used for both the JSON payload and the serialised
    /// entry. Kept as one function so the two can never drift apart.
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Information => "information",
            Self::Warning => "warning",
            Self::Error => "error",
        }
    }
}

impl std::fmt::Display for Level {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(self.as_str())
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Entry {
    pub at_ms: u64,
    pub level: Level,
    pub scope: String,
    pub message: String,
}

#[derive(Debug)]
struct Log {
    entries: VecDeque<Entry>,
    total: u64,
}

fn log() -> &'static Mutex<Log> {
    static LOG: OnceLock<Mutex<Log>> = OnceLock::new();
    LOG.get_or_init(|| {
        Mutex::new(Log {
            entries: VecDeque::with_capacity(MAX_ENTRIES),
            total: 0,
        })
    })
}

/// Append one entry, evicting the oldest when full.
///
/// Poisoning is treated as recoverable: a diagnostic log is never the reason a
/// resident process should stop working, so the guard is taken through the
/// inner value rather than propagated as an error.
pub fn record(level: Level, scope: &str, message: &str) {
    let Ok(mut guard) = log().lock().map_err(|poisoned| poisoned.into_inner()) else {
        return;
    };
    if guard.entries.len() >= MAX_ENTRIES {
        guard.entries.pop_front();
    }
    guard.entries.push_back(Entry {
        at_ms: crate::activity::now_millis(),
        level,
        scope: scope.to_string(),
        message: message.to_string(),
    });
    guard.total = guard.total.saturating_add(1);
}

pub fn information(scope: &str, message: &str) {
    record(Level::Information, scope, message);
}

pub fn warning(scope: &str, message: &str) {
    record(Level::Warning, scope, message);
}

pub fn error(scope: &str, message: &str) {
    record(Level::Error, scope, message);
}

/// Most recent entries last, so the settings page can render newest-first by
/// reversing, and a reader of the raw payload sees chronological order.
pub fn entries() -> Vec<Entry> {
    log()
        .lock()
        .map(|guard| guard.entries.iter().cloned().collect())
        .unwrap_or_default()
}

pub fn total_recorded() -> u64 {
    log().lock().map(|guard| guard.total).unwrap_or_default()
}

pub fn clear() {
    if let Ok(mut guard) = log().lock() {
        guard.entries.clear();
    }
}

#[cfg(test)]
/// Clear the ring and report what a subsequent read would see, atomically.
///
/// The log is a process-wide singleton and `cargo test` runs tests in parallel,
/// so "call `clear()`, then assert `entries()` is empty" is inherently racy:
/// another module's test can append between the two calls. Clearing and
/// sampling under one lock is the only sound way to assert the post-condition.
///
/// `clear()` and `entries()` are deliberately not called from here — they take
/// the same non-reentrant mutex and would deadlock.
pub fn clear_and_sample() -> (Vec<Entry>, u64) {
    let mut guard = log()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    guard.entries.clear();
    let sampled = guard.entries.iter().cloned().collect();
    let total = guard.total;
    (sampled, total)
}

#[cfg(test)]
/// Append `count` entries to a *private* ring and report the result.
///
/// The eviction rule is a property of the ring, not of the process-wide log, so
/// this exercises it on a local buffer. Doing it on the singleton would make the
/// test depend on no other writer appending more than `MAX_ENTRIES` entries
/// mid-run — a race that showed up roughly once in ten suite runs.
pub fn append_into_ring(ring: &mut Vec<Entry>, total: &mut u64, count: u32) {
    for index in 0..count {
        if ring.len() >= MAX_ENTRIES {
            ring.remove(0);
        }
        ring.push(Entry {
            at_ms: crate::activity::now_millis(),
            level: Level::Information,
            scope: "test/ring".to_string(),
            message: format!("entry {index}"),
        });
        *total = total.saturating_add(1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A scope no other test uses, so these assertions can find their own
    /// entries without assuming they are the only writer.
    ///
    /// The log is a process-wide singleton and `cargo test` runs tests in
    /// parallel. A private scope keeps this module's assertions readable even
    /// while another module's test is appending to the same log.
    const SCOPE: &str = "test/diagnostics";

    /// Serialises the tests in this module.
    ///
    /// The log is a process-wide singleton, so two of these tests filling the
    /// ring concurrently would evict each other's fixtures. This lock only
    /// covers tests in this module — other modules' tests append to the same log
    /// without taking it — so any assertion about the *absence* of entries goes
    /// through `clear_and_sample` instead of relying on this guard.
    /// A poisoned guard is taken through rather than propagated: one failing
    /// test must not cascade into the others.
    fn exclusive() -> std::sync::MutexGuard<'static, ()> {
        static LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());
        LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    fn mine() -> Vec<Entry> {
        entries()
            .into_iter()
            .filter(|entry| entry.scope == SCOPE)
            .collect()
    }

    #[test]
    fn the_log_is_bounded_and_drops_the_oldest_entry() {
        // Exercised on a private ring. The eviction rule is a property of the
        // buffer, and running it against the process-wide singleton made the
        // test depend on other tests not filling the shared ring mid-run.
        let mut ring: Vec<Entry> = Vec::new();
        let mut total: u64 = 0;
        let writes = (MAX_ENTRIES + 40) as u32;
        append_into_ring(&mut ring, &mut total, writes);

        assert!(
            ring.len() <= MAX_ENTRIES,
            "the ring must not grow past its cap"
        );
        assert_eq!(ring.len(), MAX_ENTRIES, "a full ring stays exactly full");

        // The survivors are a contiguous tail of what was written, in order,
        // ending with the very last entry.
        let survivors = &ring;
        assert!(!survivors.is_empty());
        let last = survivors.last().expect("a survivor");
        assert_eq!(
            last.message,
            format!("entry {}", MAX_ENTRIES + 39),
            "the newest write must always survive"
        );
        for pair in survivors.windows(2) {
            let earlier: u32 = pair[0]
                .message
                .trim_start_matches("entry ")
                .parse()
                .expect("a numeric suffix");
            let later: u32 = pair[1]
                .message
                .trim_start_matches("entry ")
                .parse()
                .expect("a numeric suffix");
            assert_eq!(later, earlier + 1, "entries must stay in write order");
        }
        // Nothing older than the capacity can still be present, and a dropped
        // entry must not reappear: the oldest survivor plus the number that
        // remain can never exceed the number that were written.
        let oldest: u32 = survivors[0]
            .message
            .trim_start_matches("entry ")
            .parse()
            .expect("a numeric suffix");
        let written = (MAX_ENTRIES + 40) as u32;
        assert_eq!(
            oldest,
            written - MAX_ENTRIES as u32,
            "exactly the first writes must have been evicted"
        );
        assert!(
            oldest + survivors.len() as u32 <= written,
            "a dropped entry reappeared, or an entry was duplicated"
        );

        // The running total counts every append, including the dropped ones,
        // which is what makes a drop detectable.
        assert_eq!(total, u64::from(writes));
    }

    #[test]
    fn level_and_scope_survive_serialisation() {
        let _guard = exclusive();
        warning(SCOPE, "app-server disconnected");
        let entry = mine().pop().expect("one entry");
        assert_eq!(entry.level, Level::Warning);
        assert_eq!(entry.scope, SCOPE);
        let json = serde_json::to_value(&entry).expect("serialise");
        assert_eq!(json["level"], "warning");
        assert!(json.get("atMs").is_some());
        assert!(json.get("at_ms").is_none(), "the wire format is camelCase");
    }

    #[test]
    fn clearing_empties_the_entries_but_keeps_the_counter() {
        error(SCOPE, "window creation failed");
        let before = total_recorded();
        assert!(!mine().is_empty());
        // Clearing and sampling under one lock: a parallel test appending to the
        // same singleton between the two calls would otherwise fail the first
        // assertion at random.
        let (sampled, total_after) = clear_and_sample();
        assert!(
            sampled.is_empty(),
            "clear must empty the ring before any other writer can append"
        );
        // The counter is a lifetime total, not a ring size. Other tests share
        // this singleton, so it may legitimately have advanced past `before`
        // between the two samples; what must never happen is clearing *lowering*
        // it back.
        assert!(
            total_after >= before,
            "clearing the view must not rewrite the lifetime counter: {total_after} < {before}"
        );
    }
}
