//! Decides which activity the compact island is about when several compete.
//!
//! The resolver is a pure function over a slice of activities. It owns no
//! state and performs no I/O, which is what makes the ordering rules testable
//! without a window, a provider or a clock.

use crate::activity::{ActivityState, LiveActivity};

/// State-derived baseline priority.
///
/// These are the numbers from the module specification. A provider may
/// override the value per activity, but the *default* has to come from the
/// state so an ordinary provider never has to reason about ranking.
pub fn state_priority(state: ActivityState) -> i32 {
    match state {
        // The user is the bottleneck. Nothing outranks a request for them.
        ActivityState::Waiting => 100,
        // A failure the user has not seen is worse than a success they have.
        ActivityState::Failed => 90,
        ActivityState::Success => 80,
        ActivityState::Running => 60,
        ActivityState::Paused => 50,
        ActivityState::Idle => 20,
        ActivityState::Cancelled => 15,
        ActivityState::Unknown => 10,
    }
}

/// An activity with nothing bounded to report ranks below one that can show
/// progress, because a determinate bar is strictly more informative.
fn progress_rank(activity: &LiveActivity) -> u8 {
    match activity
        .progress
        .as_ref()
        .and_then(|progress| progress.fraction())
    {
        Some(_) => 2,
        None if activity.progress.is_some() => 1,
        None => 0,
    }
}

/// Effective priority: the provider's explicit value, else the state default.
pub fn effective_priority(activity: &LiveActivity) -> i32 {
    activity
        .priority
        .unwrap_or_else(|| state_priority(activity.state))
}

/// Pick the activity that should occupy the compact island.
///
/// Ordering, in decreasing significance:
/// 1. effective priority (descending)
/// 2. has a determinate fraction, then any progress at all
/// 3. most recently updated
/// 4. id, ascending, so an exact tie is still deterministic
///
/// The final id comparison is not decoration: without it two activities that
/// tie on every meaningful field would swap places between calls and the
/// island would oscillate on every broker tick.
pub fn resolve(activities: &[LiveActivity]) -> Option<&LiveActivity> {
    activities.iter().max_by(|left, right| compare(left, right))
}

/// Total order used by the resolver. Exposed so the task-stack ordering in the
/// expanded island can use the exact same rules as the compact pick.
pub fn compare(left: &LiveActivity, right: &LiveActivity) -> std::cmp::Ordering {
    effective_priority(left)
        .cmp(&effective_priority(right))
        .then_with(|| progress_rank(left).cmp(&progress_rank(right)))
        .then_with(|| left.updated_at.cmp(&right.updated_at))
        // Ascending id on tie, so the *stable* winner of `compare` is the
        // largest id; `max_by` therefore picks the same element every time.
        .then_with(|| left.id.cmp(&right.id))
}

/// Activities ordered for the expanded stack: highest priority first, using
/// the same rules as the compact pick so the top row never disagrees with the
/// collapsed pill.
pub fn ordered(activities: &[LiveActivity]) -> Vec<LiveActivity> {
    let mut sorted = activities.to_vec();
    sorted.sort_by(|left, right| compare(right, left));
    sorted
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::activity::{ActivityProgress, ProviderKind};

    fn activity(id: &str, state: ActivityState) -> LiveActivity {
        LiveActivity::running(id, ProviderKind::Mock, "demo", id).with_state(state)
    }

    #[test]
    fn waiting_outranks_every_other_state() {
        let waiting = activity("w", ActivityState::Waiting);
        for state in [
            ActivityState::Failed,
            ActivityState::Success,
            ActivityState::Running,
            ActivityState::Paused,
            ActivityState::Idle,
            ActivityState::Unknown,
        ] {
            assert!(
                effective_priority(&waiting) > effective_priority(&activity("x", state)),
                "waiting must outrank {state:?}"
            );
        }
    }

    #[test]
    fn failures_outlive_successes_in_the_ranking() {
        assert!(
            state_priority(ActivityState::Failed) > state_priority(ActivityState::Success),
            "a failure must be harder to displace than a success"
        );
        assert!(state_priority(ActivityState::Success) > state_priority(ActivityState::Running));
    }

    #[test]
    fn resolver_picks_the_highest_priority_activity() {
        let activities = vec![
            activity("running", ActivityState::Running),
            activity("waiting", ActivityState::Waiting),
            activity("success", ActivityState::Success),
        ];
        assert_eq!(resolve(&activities).map(|a| a.id.as_str()), Some("waiting"));
    }

    #[test]
    fn resolver_prefers_a_measurable_activity_at_equal_priority() {
        let mut sketched = activity("sketch", ActivityState::Running);
        sketched.progress = Some(ActivityProgress::indeterminate(1.0));
        let mut measured = activity("measured", ActivityState::Running);
        measured.progress = Some(ActivityProgress::determinate(1.0, 4.0));
        let mut silent = activity("silent", ActivityState::Running);

        let activities = vec![silent.clone(), sketched.clone(), measured];
        assert_eq!(
            resolve(&activities).map(|a| a.id.as_str()),
            Some("measured"),
            "a determinate bar beats an indeterminate one at equal priority"
        );

        // And any progress beats none. `silent` is made the most recent here, so
        // the only way `sketch` can win is through its progress rank.
        silent.updated_at = u64::MAX;
        let slowest = LiveActivity {
            updated_at: 0,
            ..sketched
        };
        let activities = vec![silent, slowest];
        assert_eq!(resolve(&activities).map(|a| a.id.as_str()), Some("sketch"));
    }

    #[test]
    fn equal_rank_resolves_by_recency_then_id() {
        let mut older = activity("b-older", ActivityState::Running);
        older.updated_at = 100;
        let mut newer = activity("a-newer", ActivityState::Running);
        newer.updated_at = 200;
        assert_eq!(
            resolve(&[older.clone(), newer.clone()]).map(|a| a.id.as_str()),
            Some("a-newer")
        );

        // Identical priority, progress and timestamp: the id breaks the tie, and
        // it must do so the same way on every call.
        let mut left = activity("aaa", ActivityState::Running);
        left.updated_at = 500;
        let mut right = activity("bbb", ActivityState::Running);
        right.updated_at = 500;
        for _ in 0..8 {
            assert_eq!(
                resolve(&[left.clone(), right.clone()]).map(|a| a.id.as_str()),
                Some("bbb")
            );
            assert_eq!(
                resolve(&[right.clone(), left.clone()]).map(|a| a.id.as_str()),
                Some("bbb")
            );
        }
    }

    #[test]
    fn explicit_priority_overrides_the_state_default() {
        let mut pinned = activity("pinned", ActivityState::Idle);
        pinned.priority = Some(500);
        let waiting = activity("waiting", ActivityState::Waiting);
        assert_eq!(
            resolve(&[pinned, waiting]).map(|a| a.id.as_str()),
            Some("pinned"),
            "a provider-supplied priority must win over the state baseline"
        );
    }

    #[test]
    fn empty_input_has_no_answer() {
        assert!(resolve(&[]).is_none());
        assert!(ordered(&[]).is_empty());
    }

    #[test]
    fn stacked_order_matches_the_compact_pick() {
        let activities = vec![
            activity("running", ActivityState::Running),
            activity("waiting", ActivityState::Waiting),
            activity("idle", ActivityState::Idle),
        ];
        let sorted = ordered(&activities);
        assert_eq!(sorted[0].id, "waiting");
        assert_eq!(
            Some(sorted[0].id.as_str()),
            resolve(&activities).map(|a| a.id.as_str()),
            "the top of the stack must be the compact pick"
        );
        assert_eq!(
            sorted.iter().map(|a| a.id.as_str()).collect::<Vec<_>>(),
            vec!["waiting", "running", "idle"]
        );
    }

    #[test]
    fn comparator_is_antisymmetric() {
        let left = activity("a", ActivityState::Running);
        let right = activity("b", ActivityState::Waiting);
        assert_eq!(compare(&left, &right), compare(&right, &left).reverse());
        assert_eq!(compare(&left, &left), std::cmp::Ordering::Equal);
    }
}
