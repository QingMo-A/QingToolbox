//! Decides whether the island should be allowed on screen right now.
//!
//! The policy lives here rather than as conditionals scattered through the
//! overlay, so the rules can be tested without a window and reasoned about in
//! one place. The overlay asks one question — "may I show this?" — and does not
//! know why the answer is no.

use crate::activity::{ActivityState, LiveActivity};
use crate::settings::FullscreenPolicy;

/// Merge the current fullscreen observation with the configured policy into a
/// single decision.
///
/// `fullscreen` is supplied by the caller so the policy function stays pure and
/// testable; the Win32 query lives in `overlay.rs`.
pub fn allows(policy: FullscreenPolicy, fullscreen: bool, focus: Option<&LiveActivity>) -> bool {
    if !fullscreen {
        return true;
    }
    match policy {
        FullscreenPolicy::Always => true,
        FullscreenPolicy::Hide => false,
        // The one case worth interrupting a fullscreen app for: the user is the
        // bottleneck, so a hidden request for them is a stuck task. A failure
        // qualifies for the same reason — it is the other outcome the user must
        // act on. Everything else waits.
        FullscreenPolicy::Important => focus.is_some_and(|activity| {
            activity.state == ActivityState::Waiting || activity.state == ActivityState::Failed
        }),
    }
}

/// Whether an activity is important enough to survive `ImportantOnly`.
pub fn is_important(activity: &LiveActivity) -> bool {
    matches!(
        activity.state,
        ActivityState::Waiting | ActivityState::Failed
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::activity::ProviderKind;

    fn activity(state: ActivityState) -> LiveActivity {
        LiveActivity::running("a", ProviderKind::Mock, "demo", "Demo").with_state(state)
    }

    #[test]
    fn a_windowed_desktop_always_shows_the_island() {
        for policy in [
            FullscreenPolicy::Always,
            FullscreenPolicy::Hide,
            FullscreenPolicy::Important,
        ] {
            assert!(
                allows(policy, false, None),
                "{policy:?} must show when not fullscreen"
            );
            assert!(allows(
                policy,
                false,
                Some(&activity(ActivityState::Running))
            ));
        }
    }

    #[test]
    fn always_shows_even_in_fullscreen() {
        assert!(allows(FullscreenPolicy::Always, true, None));
        assert!(allows(
            FullscreenPolicy::Always,
            true,
            Some(&activity(ActivityState::Running))
        ));
    }

    #[test]
    fn hide_suppresses_everything_in_fullscreen() {
        for state in [
            ActivityState::Running,
            ActivityState::Waiting,
            ActivityState::Failed,
            ActivityState::Success,
        ] {
            assert!(
                !allows(FullscreenPolicy::Hide, true, Some(&activity(state))),
                "Hide must suppress {state:?}"
            );
        }
    }

    #[test]
    fn important_only_lets_user_blocking_states_through() {
        assert!(allows(
            FullscreenPolicy::Important,
            true,
            Some(&activity(ActivityState::Waiting))
        ));
        assert!(allows(
            FullscreenPolicy::Important,
            true,
            Some(&activity(ActivityState::Failed))
        ));
        // Progress and idle work are exactly what the policy exists to suppress.
        for state in [
            ActivityState::Running,
            ActivityState::Paused,
            ActivityState::Idle,
            ActivityState::Success,
            ActivityState::Unknown,
        ] {
            assert!(
                !allows(FullscreenPolicy::Important, true, Some(&activity(state))),
                "ImportantOnly must suppress {state:?}"
            );
        }
    }

    #[test]
    fn important_only_with_nothing_to_show_shows_nothing() {
        assert!(!allows(FullscreenPolicy::Important, true, None));
    }

    #[test]
    fn importance_matches_the_waiting_and_failed_states() {
        assert!(is_important(&activity(ActivityState::Waiting)));
        assert!(is_important(&activity(ActivityState::Failed)));
        assert!(!is_important(&activity(ActivityState::Running)));
        assert!(!is_important(&activity(ActivityState::Success)));
    }
}
