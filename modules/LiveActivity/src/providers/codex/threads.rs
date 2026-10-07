//! Turns Codex threads into `LiveActivity` values.
//!
//! Kept separate from the transport so the mapping can be tested against
//! recorded app-server shapes without spawning anything. This is also the layer
//! that decides what *not* to show — which threads are worth an island row at
//! all — and that decision is the difference between a useful surface and a
//! list of everything that has ever happened.

use crate::activity::{ActivityProgress, ActivityState, LiveActivity, ProviderKind};
use crate::providers::codex::protocol::{RateLimits, ThreadRecord, ThreadStatus, TokenUsage};

/// Prefix that makes a Codex thread id safe to use as an activity id, and lets
/// the broker tell a Codex activity from a mock one at a glance.
const ID_PREFIX: &str = "codex-thread-";

pub fn activity_id(thread_id: &str) -> String {
    format!("{ID_PREFIX}{thread_id}")
}

/// Whether a thread deserves a row on the island.
///
/// Idle threads are excluded on purpose. An agent tool keeps a long tail of
/// finished conversations, and surfacing them would turn the island into a
/// history view — the opposite of "what is happening now". A thread that has
/// never been given a status is also excluded, because showing it would mean
/// asserting something we were not told.
pub fn is_visible(record: &ThreadRecord) -> bool {
    match record.status.to_activity_state() {
        ActivityState::Running
        | ActivityState::Waiting
        | ActivityState::Failed
        | ActivityState::Paused => true,
        ActivityState::Idle | ActivityState::Success | ActivityState::Cancelled => false,
        ActivityState::Unknown => false,
    }
}

/// Map one thread to an activity.
///
/// `usage` and `limits` are per-account figures; they are attached only to the
/// activity that is currently the focus, which the caller decides. That is why
/// this function takes them as options rather than reading any shared state.
pub fn to_activity(
    record: &ThreadRecord,
    usage: Option<&TokenUsage>,
    limits: Option<&RateLimits>,
) -> LiveActivity {
    let state = record.status.to_activity_state();
    let title = record
        .title
        .clone()
        .unwrap_or_else(|| fallback_title(state));

    let mut activity = LiveActivity::running(
        activity_id(&record.id),
        ProviderKind::Codex,
        "thread",
        title,
    )
    .with_state(state);

    if let Some(label) = record.status.attention_label() {
        activity = activity.with_subtitle(label);
    }

    // Provenance, not content. The raw thread id lets a future action target
    // the right thread; the status token lets diagnostics distinguish "idle"
    // from "we could not tell".
    activity = activity
        .with_detail("thread", record.id.clone())
        .with_detail("status", record.status.as_str());

    // Account usage is a *running* thread's story: it describes how much of the
    // context window the work has consumed. On a waiting or failed row the user
    // is looking for what to do next, and a token count there is noise. So the
    // usage detail and the context bar are both gated on running.
    if state == ActivityState::Running {
        if let Some(usage) = usage {
            if let Some(summary) = usage.summary() {
                activity = activity.with_detail("usage", summary);
            }
            // A running thread with a known context window gets a determinate
            // bar, because that is a genuinely bounded quantity.
            if let Some(fraction) = usage.context_fraction() {
                let total = usage.context_window.unwrap_or(100) as f64;
                activity = activity.with_progress(ActivityProgress::determinate(
                    (fraction * total).round(),
                    total,
                ));
            }
        }
    }

    // Account limits belong on the waiting activity if there is one, because
    // that is the row the user is already looking at.
    if record.status.needs_the_user() {
        if let Some(summary) = limits.and_then(RateLimits::summary) {
            activity = activity.with_detail("limits", summary);
        }
        let action = match record.status {
            ThreadStatus::WaitingOnApproval => ("approve", "Review"),
            _ => ("respond", "Respond"),
        };
        activity = activity.with_action(action.0, action.1);
    }

    // updatedAt is not a task start time, and no start time is known here.
    activity.started_at = None;
    activity
}

fn fallback_title(state: ActivityState) -> String {
    match state {
        ActivityState::Waiting => "Codex needs you".to_string(),
        ActivityState::Failed => "Codex task failed".to_string(),
        _ => "Codex".to_string(),
    }
}

/// Map a whole `thread/list` result, dropping what should not be shown.
pub fn collect(
    records: &[ThreadRecord],
    usage: Option<&TokenUsage>,
    limits: Option<&RateLimits>,
) -> Vec<LiveActivity> {
    records
        .iter()
        // `iter()` yields `&ThreadRecord`, but `is_visible` takes `&ThreadRecord`
        // by value-of-reference; the closure adapts the extra layer so the whole
        // predicate stays one named, testable function.
        .filter(|record| is_visible(record))
        .map(|record| {
            // Attach account figures only to the highest-value thread, so the
            // island does not repeat the same usage row on every entry.
            let is_focus = record.status.needs_the_user() || record.status == ThreadStatus::Active;
            to_activity(
                record,
                if is_focus { usage } else { None },
                if is_focus { limits } else { None },
            )
        })
        .collect()
}

/// The account summary the peek row shows when nothing else is running.
pub fn account_summary(limits: Option<&RateLimits>, usage: Option<&TokenUsage>) -> Option<String> {
    limits.and_then(RateLimits::remaining_summary).or_else(|| {
        usage
            .and_then(|usage| usage.summary())
            .map(|summary| compact_line(&summary))
    })
}

fn compact_line(value: &str) -> String {
    // Keeps the account row to one short line even if a provider sends more.
    value.chars().take(48).collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn thread(id: &str, status: &str) -> ThreadRecord {
        ThreadRecord::parse(&json!({"id": id, "status": status})).expect("record")
    }

    #[test]
    fn active_threads_are_running_and_waiting_threads_ask_for_you() {
        let running = to_activity(&thread("t1", "active"), None, None);
        assert_eq!(running.state, ActivityState::Running);
        assert_eq!(running.provider, ProviderKind::Codex);
        assert_eq!(running.kind, "thread");

        let approval = to_activity(&thread("t2", "waitingOnApproval"), None, None);
        assert_eq!(approval.state, ActivityState::Waiting);
        assert_eq!(approval.subtitle.as_deref(), Some("Approval needed"));
        assert!(
            !approval.actions.is_empty(),
            "a waiting row must offer an action"
        );

        let input = to_activity(&thread("t3", "waitingOnUserInput"), None, None);
        assert_eq!(input.state, ActivityState::Waiting);
        assert_eq!(input.subtitle.as_deref(), Some("Input needed"));
    }

    #[test]
    fn system_errors_map_to_failed_with_a_readable_title() {
        let failed = to_activity(&thread("t1", "systemError"), None, None);
        assert_eq!(failed.state, ActivityState::Failed);
        assert_eq!(failed.title, "Codex task failed");
    }

    #[test]
    fn an_unknown_status_never_pretends_to_be_running() {
        let unknown = to_activity(&thread("t1", "brand-new-status"), None, None);
        assert_eq!(unknown.state, ActivityState::Unknown);
        assert_eq!(
            unknown.details.get("status").map(String::as_str),
            Some("unknown"),
            "the raw status must be preserved for diagnosis"
        );
    }

    #[test]
    fn idle_and_unknown_threads_are_not_shown() {
        assert!(!is_visible(&thread("t1", "idle")));
        assert!(!is_visible(&thread("t2", "brand-new-status")));
        assert!(is_visible(&thread("t3", "active")));
        assert!(is_visible(&thread("t4", "waitingOnApproval")));
        assert!(is_visible(&thread("t5", "waitingOnUserInput")));
        assert!(is_visible(&thread("t6", "systemError")));
    }

    #[test]
    fn a_visible_subset_of_a_realistic_list_is_collected() {
        let records = vec![
            thread("done-1", "idle"),
            thread("live-1", "active"),
            thread("ask-1", "waitingOnUserInput"),
            thread("stale-1", "someFutureStatus"),
            thread("bad-1", "systemError"),
        ];
        let activities = collect(&records, None, None);
        assert_eq!(
            activities.len(),
            3,
            "only live, waiting and failed threads belong on the island"
        );

        let ids = activities.iter().map(|a| a.id.as_str()).collect::<Vec<_>>();
        assert!(ids.contains(&"codex-thread-live-1"));
        assert!(ids.contains(&"codex-thread-ask-1"));
        assert!(ids.contains(&"codex-thread-bad-1"));
    }

    #[test]
    fn activity_ids_are_prefixed_so_providers_cannot_collide() {
        assert_eq!(activity_id("abc"), "codex-thread-abc");
        let mock = LiveActivity::running("mock-0", ProviderKind::Mock, "x", "x");
        let codex = to_activity(&thread("0", "active"), None, None);
        assert_ne!(mock.id, codex.id);
    }

    #[test]
    fn usage_is_attached_only_to_a_running_thread() {
        let usage = TokenUsage::parse(&json!({
            "inputTokens": 72, "contextWindow": 100
        }));

        let running = to_activity(&thread("t1", "active"), Some(&usage), None);
        assert_eq!(
            running.details.get("usage").map(String::as_str),
            Some("Context 72% · 72")
        );
        assert!(
            running.progress.is_some(),
            "a running thread shows its context bar"
        );

        // A waiting thread's row is about the user, not about tokens.
        let waiting = to_activity(&thread("t2", "waitingOnApproval"), Some(&usage), None);
        assert!(!waiting.details.contains_key("usage"));
        assert!(waiting.progress.is_none());
    }

    #[test]
    fn account_limits_land_on_the_waiting_row() {
        let limits = RateLimits::parse(&json!({"usedPercent": 76, "windowMinutes": 10080}));
        let waiting = to_activity(&thread("t1", "waitingOnApproval"), None, Some(&limits));
        assert_eq!(
            waiting.details.get("limits").map(String::as_str),
            Some("Weekly usage 76%")
        );
        // A running thread does not repeat the account figure.
        let running = to_activity(&thread("t2", "active"), None, Some(&limits));
        assert!(!running.details.contains_key("limits"));
    }

    #[test]
    fn no_fabricated_numbers_appear_when_usage_is_unavailable() {
        let activity = to_activity(&thread("t1", "active"), None, None);
        assert!(activity.progress.is_none());
        assert!(!activity.details.contains_key("usage"));
        assert!(!activity.details.contains_key("limits"));
    }

    #[test]
    fn an_account_summary_is_suppressed_when_nothing_is_known() {
        assert!(account_summary(None, None).is_none());
        assert!(account_summary(Some(&RateLimits::default()), None).is_none());
        assert_eq!(
            account_summary(Some(&RateLimits::parse(&json!({"usedPercent": 76}))), None).as_deref(),
            Some("Codex · 额度剩余 24%")
        );
    }

    #[test]
    fn details_never_contain_thread_bodies() {
        // The app-server may include a body; the activity must not carry it.
        let record = ThreadRecord::parse(&json!({
            "id": "t1",
            "status": "active",
            "title": "Fix the parser",
            "messages": [{"role": "user", "content": "my secret prompt"}]
        }))
        .expect("record");
        let activity = to_activity(&record, None, None);
        let serialized = serde_json::to_string(&activity).expect("serialise");
        assert!(
            !serialized.contains("my secret prompt"),
            "conversation content must never reach the activity payload"
        );
    }

    #[test]
    fn a_thread_that_reports_no_title_still_gets_a_useful_one() {
        let record = ThreadRecord::parse(&json!({"id": "t1", "status": "waitingOnUserInput"}))
            .expect("record");
        let activity = to_activity(&record, None, None);
        assert_eq!(activity.title, "Codex needs you");
        assert_eq!(activity.subtitle.as_deref(), Some("Input needed"));
    }

    #[test]
    fn repeated_mapping_of_one_thread_is_stable() {
        let record = thread("t1", "active");
        let first = to_activity(&record, None, None);
        let second = to_activity(&record, None, None);
        assert_eq!(
            first.id, second.id,
            "the same thread must keep one island row"
        );
        assert_eq!(first.details.get("thread"), second.details.get("thread"));
    }
}
