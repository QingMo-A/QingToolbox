//! The single owner of live activity state.
//!
//! Providers write, the overlay reads, and nobody talks to anybody else. That
//! one-way flow is the whole point of the module: it is what lets a misbehaving
//! provider be replaced without touching window code, and what lets the overlay
//! be restarted without providers having to re-announce their work.
//!
//! Every mutating entry point takes one provider message and returns the
//! resulting snapshot. There is no incremental patch API, because "what does
//! the island show now" is always answered by recomputing from the full set —
//! which is cheap at this scale and impossible to desynchronise.

use std::collections::BTreeMap;

use crate::activity::{ActivityState, LiveActivity, ProviderKind};
use crate::priority;

/// How long a success stays on the island before retiring itself.
///
/// Short, because a success is an acknowledgement and not a notification.
pub const SUCCESS_LINGER_MS: u64 = 4_000;

/// How long a failure stays before retiring itself.
///
/// Deliberately longer than a success: the user needs time to notice, and a
/// failure they never saw is worse than one they saw twice.
pub const FAILURE_LINGER_MS: u64 = 12_000;

/// How long a cancelled activity stays. Between the two, because the user is
/// usually the one who cancelled it and already knows.
pub const CANCELLED_LINGER_MS: u64 = 2_000;

/// Upper bound on activities held at once.
///
/// A provider that leaks activities must not be able to grow the overlay's
/// work without limit. The oldest, lowest-priority entries are dropped first,
/// and the drop is reported so the situation is visible rather than silent.
pub const MAX_ACTIVITIES: usize = 32;

/// The state of the island after a broker mutation.
///
/// The overlay renders exactly this and nothing else.
#[derive(Debug, Clone, PartialEq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BrokerSnapshot {
    pub activities: Vec<LiveActivity>,
    /// The activity the compact island is about, if any.
    pub focus: Option<LiveActivity>,
    /// Number of activities beyond the ones the expanded list shows inline.
    pub overflow: usize,
    /// Unix milliseconds of the last accepted mutation.
    pub revision: u64,
}

impl BrokerSnapshot {
    pub fn is_empty(&self) -> bool {
        self.activities.is_empty()
    }
}

/// What the broker did with one incoming activity, for diagnostics.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Mutation {
    Created,
    Updated,
    Removed,
    /// Rejected because the incoming value failed validation.
    Rejected,
    Unchanged,
}

#[derive(Debug, Default)]
pub struct ActivityBroker {
    /// Keyed by activity id, so an update is a replace and never a duplicate.
    activities: BTreeMap<String, LiveActivity>,
    revision: u64,
    /// Lifetime count of activities dropped by the capacity guard.
    dropped: u64,
    // A bounded tombstone prevents a polling snapshot resurrecting a finished task.
    retired: BTreeMap<String, LiveActivity>,
}

impl ActivityBroker {
    pub fn new() -> Self {
        Self::default()
    }

    /// Insert or update one activity.
    ///
    /// The same id always means the same task. A provider that wants a new
    /// stack entry must mint a new id; a provider that re-sends an existing id
    /// mutates that entry in place, which is what stops a per-tick update from
    /// flooding the stack.
    pub fn upsert(&mut self, mut incoming: LiveActivity) -> Mutation {
        let problem = validate(&incoming);
        if let Some(reason) = problem {
            crate::diagnostics::record(
                crate::diagnostics::Level::Warning,
                "broker",
                &format!("rejected activity {}: {reason}", incoming.id),
            );
            return Mutation::Rejected;
        }

        if let Some(previous) = self
            .activities
            .get(&incoming.id)
            .or_else(|| self.retired.get(&incoming.id))
        {
            incoming.updated_at = previous.updated_at;
            incoming.started_at = previous.started_at;
            if &incoming == previous {
                return Mutation::Unchanged;
            }
        }
        self.retired.remove(&incoming.id);
        // The broker owns the clock. Letting a provider set `updated_at` would
        // let a replay pin an activity to the top of the stack forever.
        incoming.touch();
        let is_new = !self.activities.contains_key(&incoming.id);
        self.activities.insert(incoming.id.clone(), incoming);
        self.revision = self.revision.saturating_add(1);
        self.enforce_capacity();

        if is_new {
            Mutation::Created
        } else {
            Mutation::Updated
        }
    }

    /// Remove one activity. Removing something absent is a no-op rather than
    /// an error, because providers race with their own teardown.
    pub fn remove(&mut self, id: &str) -> Mutation {
        if self.activities.remove(id).is_some() {
            self.revision = self.revision.saturating_add(1);
            Mutation::Removed
        } else {
            Mutation::Removed
        }
    }

    /// Drop every activity, optionally limited to one provider.
    pub fn clear(&mut self, provider: Option<ProviderKind>) {
        let before = self.activities.len();
        match provider {
            Some(kind) => self.activities.retain(|_, item| item.provider != kind),
            None => self.activities.clear(),
        }
        if self.activities.len() != before {
            self.revision = self.revision.saturating_add(1);
        }
    }

    /// Retire terminal activities whose linger window has elapsed.
    ///
    /// Called on every tick rather than on a per-activity timer. One sweep over
    /// at most `MAX_ACTIVITIES` entries is far cheaper than maintaining a timer
    /// wheel, and it cannot leak a timer when an activity is replaced.
    pub fn expire(&mut self, now: u64) -> usize {
        let expired = self
            .activities
            .values()
            .filter(|activity| {
                linger_ms(activity.state)
                    .is_some_and(|linger| now >= activity.updated_at.saturating_add(linger))
            })
            .map(|activity| activity.id.clone())
            .collect::<Vec<_>>();
        for id in &expired {
            if let Some(activity) = self.activities.remove(id) {
                self.retired.insert(id.clone(), activity);
            }
        }
        while self.retired.len() > MAX_ACTIVITIES {
            if let Some(id) = self.retired.keys().next().cloned() {
                self.retired.remove(&id);
            }
        }
        if !expired.is_empty() {
            self.revision = self.revision.saturating_add(1);
        }
        expired.len()
    }

    /// Earliest moment at which `expire` would remove something.
    ///
    /// Lets the caller sleep until there is actually work instead of polling.
    /// `None` means nothing on the island is due to retire, so the overlay has
    /// no reason to wake at all.
    pub fn next_expiry(&self) -> Option<u64> {
        self.activities
            .values()
            .filter_map(|activity| {
                linger_ms(activity.state).map(|linger| activity.updated_at.saturating_add(linger))
            })
            .min()
    }

    pub fn activities(&self) -> Vec<LiveActivity> {
        self.activities.values().cloned().collect()
    }

    pub fn revision(&self) -> u64 {
        self.revision
    }

    pub fn dropped(&self) -> u64 {
        self.dropped
    }

    /// Recompute the public snapshot.
    ///
    /// Sorted by the resolver's total order, so the list the expanded island
    /// renders and the single activity the compact island picks can never
    /// contradict each other.
    pub fn snapshot(&self, visible_limit: usize) -> BrokerSnapshot {
        let ordered = priority::ordered(&self.activities());
        let focus = ordered.first().cloned();
        let overflow = ordered.len().saturating_sub(visible_limit.max(1));
        BrokerSnapshot {
            activities: ordered,
            focus,
            overflow,
            revision: self.revision,
        }
    }

    fn enforce_capacity(&mut self) {
        while self.activities.len() > MAX_ACTIVITIES {
            // Evict the least important entry: lowest effective priority, then
            // oldest. `min_by` on the resolver's comparator gives exactly that,
            // so eviction uses the same notion of importance as display.
            let victim = self
                .activities
                .values()
                .min_by(|left, right| priority::compare(left, right))
                .map(|activity| activity.id.clone());
            match victim {
                Some(id) => {
                    self.activities.remove(&id);
                    self.dropped = self.dropped.saturating_add(1);
                    crate::diagnostics::record(
                        crate::diagnostics::Level::Warning,
                        "broker",
                        &format!("evicted activity {id}: activity limit reached"),
                    );
                }
                None => break,
            }
        }
    }
}

/// The linger window for a terminal state, or `None` for a state that stays
/// until the provider says otherwise.
pub fn linger_ms(state: ActivityState) -> Option<u64> {
    match state {
        ActivityState::Success => Some(SUCCESS_LINGER_MS),
        ActivityState::Failed => Some(FAILURE_LINGER_MS),
        ActivityState::Cancelled => Some(CANCELLED_LINGER_MS),
        _ => None,
    }
}

/// Reject values that would make the overlay render something meaningless.
///
/// This is a guard against provider bugs, not a security boundary: the module
/// process is the trust boundary, and providers run inside it.
fn validate(activity: &LiveActivity) -> Option<&'static str> {
    if activity.id.trim().is_empty() {
        return Some("empty id");
    }
    if activity.id.len() > 128 {
        return Some("id too long");
    }
    if activity.title.trim().is_empty() {
        return Some("empty title");
    }
    // A title is rendered in a fixed-width pill; an unbounded one would push
    // the island past its own window.
    if activity.title.chars().count() > 160 {
        return Some("title too long");
    }
    if activity
        .subtitle
        .as_ref()
        .is_some_and(|value| value.chars().count() > 240)
    {
        return Some("subtitle too long");
    }
    if activity.actions.len() > 4 {
        return Some("too many actions");
    }
    if activity.details.len() > 16 {
        return Some("too many details");
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::activity::now_millis;
    use crate::activity::{ActivityProgress, ProviderKind};

    fn running(id: &str) -> LiveActivity {
        LiveActivity::running(id, ProviderKind::Mock, "demo", id)
    }

    #[test]
    fn upsert_creates_then_updates_in_place() {
        let mut broker = ActivityBroker::new();
        assert_eq!(broker.upsert(running("a")), Mutation::Created);
        assert_eq!(broker.activities().len(), 1);

        // The same id is the same task: no second stack entry.
        let mut update = running("a");
        update.state = ActivityState::Success;
        assert_eq!(broker.upsert(update), Mutation::Updated);
        assert_eq!(broker.activities().len(), 1);
        assert_eq!(broker.activities()[0].state, ActivityState::Success);
    }

    #[test]
    fn duplicate_updates_do_not_grow_the_stack() {
        let mut broker = ActivityBroker::new();
        for _ in 0..50 {
            broker.upsert(running("polling-task"));
        }
        assert_eq!(
            broker.activities().len(),
            1,
            "a polling provider must not flood the stack"
        );
        assert_eq!(
            broker.revision(),
            1,
            "identical snapshots must not repaint or reset linger"
        );
    }

    #[test]
    fn repeated_terminal_snapshots_do_not_pin_or_resurrect_completion() {
        let mut broker = ActivityBroker::new();
        let done = running("done").with_state(ActivityState::Success);
        broker.upsert(done.clone());
        let deadline = broker.next_expiry().unwrap();
        assert_eq!(broker.upsert(done.clone()), Mutation::Unchanged);
        assert_eq!(broker.next_expiry(), Some(deadline));
        assert_eq!(broker.expire(deadline), 1);
        assert_eq!(broker.upsert(done), Mutation::Unchanged);
        assert!(broker.activities().is_empty());
        assert_eq!(broker.upsert(running("done")), Mutation::Created);
    }

    #[test]
    fn broker_owns_the_timestamp_so_a_replay_cannot_pin_an_activity() {
        let mut broker = ActivityBroker::new();
        let mut stale = running("a");
        stale.updated_at = 1; // a provider trying to look old
        broker.upsert(stale);
        assert!(broker.activities()[0].updated_at > 1);

        let mut future = running("b");
        future.updated_at = u64::MAX - 1;
        broker.upsert(future);
        assert!(broker
            .activities()
            .iter()
            .all(|a| a.updated_at < u64::MAX - 1));
    }

    #[test]
    fn malformed_activities_are_rejected_without_touching_state() {
        let mut broker = ActivityBroker::new();
        broker.upsert(running("keep"));

        let mut blank_id = running("a");
        blank_id.id = "  ".to_string();
        assert_eq!(broker.upsert(blank_id), Mutation::Rejected);

        let mut blank_title = running("b");
        blank_title.title = "".to_string();
        assert_eq!(broker.upsert(blank_title), Mutation::Rejected);

        let mut long_title = running("c");
        long_title.title = "x".repeat(200);
        assert_eq!(broker.upsert(long_title), Mutation::Rejected);

        let mut many_actions = running("d");
        for index in 0..5 {
            many_actions
                .actions
                .push(crate::activity::ActivityAction::new(
                    format!("a{index}"),
                    "x",
                ));
        }
        assert_eq!(broker.upsert(many_actions), Mutation::Rejected);

        assert_eq!(
            broker.activities().len(),
            1,
            "rejection must not mutate state"
        );
        assert_eq!(broker.activities()[0].id, "keep");
    }

    #[test]
    fn success_retires_itself_and_failure_outlives_it() {
        let mut broker = ActivityBroker::new();
        let mut success = running("s");
        success.state = ActivityState::Success;
        broker.upsert(success);
        let mut failure = running("f");
        failure.state = ActivityState::Failed;
        broker.upsert(failure);

        let now = now_millis();
        // Both are still inside their window.
        assert_eq!(broker.expire(now), 0);

        // Past the success window but not the failure window.
        assert_eq!(broker.expire(now + SUCCESS_LINGER_MS + 1), 1);
        assert_eq!(broker.activities().len(), 1);
        assert_eq!(broker.activities()[0].id, "f");

        assert_eq!(broker.expire(now + FAILURE_LINGER_MS + 1), 1);
        assert!(broker.activities().is_empty());
    }

    #[test]
    fn non_terminal_activities_never_expire_on_their_own() {
        let mut broker = ActivityBroker::new();
        for (index, state) in [
            ActivityState::Running,
            ActivityState::Waiting,
            ActivityState::Paused,
            ActivityState::Idle,
            ActivityState::Unknown,
        ]
        .into_iter()
        .enumerate()
        {
            let mut activity = running(&format!("a{index}"));
            activity.state = state;
            broker.upsert(activity);
        }
        assert_eq!(broker.expire(u64::MAX - 1), 0);
        assert_eq!(broker.activities().len(), 5);
        // And with nothing terminal present there is no reason to wake up.
        assert!(broker.next_expiry().is_none());
    }

    #[test]
    fn next_expiry_reports_the_soonest_deadline() {
        let mut broker = ActivityBroker::new();
        assert_eq!(broker.next_expiry(), None);

        let mut failure = running("f");
        failure.state = ActivityState::Failed;
        broker.upsert(failure);
        let mut success = running("s");
        success.state = ActivityState::Success;
        broker.upsert(success);

        let soonest = broker.next_expiry().expect("a terminal activity was added");
        let success_at = broker
            .activities()
            .iter()
            .find(|a| a.id == "s")
            .map(|a| a.updated_at + SUCCESS_LINGER_MS)
            .expect("success present");
        assert_eq!(
            soonest, success_at,
            "the shortest linger window must be reported"
        );
    }

    #[test]
    fn removal_is_idempotent_and_clear_can_target_one_provider() {
        let mut broker = ActivityBroker::new();
        broker.upsert(running("mock-1"));
        broker.upsert(LiveActivity::running(
            "codex-1",
            ProviderKind::Codex,
            "thread",
            "t",
        ));

        assert_eq!(broker.remove("codex-1"), Mutation::Removed);
        // Removing something already gone must not fail a provider's teardown.
        assert_eq!(broker.remove("codex-1"), Mutation::Removed);
        assert_eq!(broker.remove("never-existed"), Mutation::Removed);
        assert_eq!(broker.activities().len(), 1);

        broker.upsert(LiveActivity::running(
            "codex-2",
            ProviderKind::Codex,
            "thread",
            "t",
        ));
        broker.clear(Some(ProviderKind::Codex));
        assert_eq!(broker.activities().len(), 1);
        assert_eq!(broker.activities()[0].provider, ProviderKind::Mock);

        broker.clear(None);
        assert!(broker.activities().is_empty());
    }

    #[test]
    fn capacity_guard_evicts_the_least_important_first() {
        let mut broker = ActivityBroker::new();
        // One activity the user must see.
        let mut waiting = running("waiting");
        waiting.state = ActivityState::Waiting;
        broker.upsert(waiting);

        // Then more than the cap allows of low-value idle work.
        for index in 0..MAX_ACTIVITIES + 8 {
            let mut idle = running(&format!("idle-{index:03}"));
            idle.state = ActivityState::Idle;
            broker.upsert(idle);
        }

        assert_eq!(broker.activities().len(), MAX_ACTIVITIES);
        assert!(
            broker.dropped() >= 8,
            "the guard must report what it dropped"
        );
        assert!(
            broker.activities().iter().any(|a| a.id == "waiting"),
            "a waiting-on-user activity must survive eviction pressure"
        );
    }

    #[test]
    fn snapshot_exposes_focus_overflow_and_is_ordered() {
        let mut broker = ActivityBroker::new();
        for index in 0..8 {
            broker.upsert(running(&format!("a{index}")));
        }
        let mut waiting = running("urgent");
        waiting.state = ActivityState::Waiting;
        broker.upsert(waiting);

        let snapshot = broker.snapshot(3);
        assert_eq!(
            snapshot.focus.as_ref().map(|a| a.id.as_str()),
            Some("urgent")
        );
        assert_eq!(snapshot.activities.len(), 9);
        assert_eq!(snapshot.overflow, 6);
        // The compact focus is always the head of the expanded list.
        assert_eq!(snapshot.activities[0].id, "urgent");
        assert!(!snapshot.is_empty());
    }

    #[test]
    fn snapshot_of_an_empty_broker_is_well_formed() {
        let broker = ActivityBroker::new();
        let snapshot = broker.snapshot(3);
        assert!(snapshot.is_empty());
        assert!(snapshot.focus.is_none());
        assert_eq!(snapshot.overflow, 0);
        assert_eq!(snapshot.revision, 0);
    }

    #[test]
    fn progress_survives_an_update_without_a_progress_field() {
        let mut broker = ActivityBroker::new();
        broker.upsert(running("a").with_progress(ActivityProgress::determinate(1.0, 4.0)));
        assert!(broker.activities()[0].progress.is_some());
        broker.upsert(running("a"));
        assert!(
            broker.activities()[0].progress.is_none(),
            "an update replaces the whole record rather than merging, so a provider \
             that stops reporting progress clears the bar instead of freezing it"
        );
    }

    #[test]
    fn snapshot_round_trips_through_json() {
        let mut broker = ActivityBroker::new();
        broker.upsert(running("a").with_detail("context", "72%"));
        let snapshot = broker.snapshot(3);
        let json = serde_json::to_string(&snapshot).expect("serialise");
        let decoded: BrokerSnapshot = serde_json::from_str(&json).expect("deserialise");
        assert_eq!(decoded, snapshot);
    }
}
