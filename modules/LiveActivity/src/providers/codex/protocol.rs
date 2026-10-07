//! Codex app-server wire types.
//!
//! Everything here is parsed defensively. The app-server is a separate program
//! that this module does not control and cannot version-pin, so a field that
//! disappears, changes type, or gains a new variant must degrade to "unknown"
//! rather than failing the whole provider.
//!
//! The parser is pure: it takes JSON and returns typed values. That is what
//! lets the mapping rules be tested against recorded shapes with no process,
//! no socket and no clock.

use serde::{Deserialize, Serialize};

use crate::activity::ActivityState;

/// A thread status exactly as the app-server reports it.
///
/// Kept as a distinct type from `ActivityState` on purpose. The two evolve for
/// different reasons — one is somebody else's protocol, the other is our UI —
/// and collapsing them would mean a rename in the app-server silently changes
/// what the island draws.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ThreadStatus {
    Active,
    Idle,
    WaitingOnApproval,
    WaitingOnUserInput,
    SystemError,
    /// A status string this build has not seen. Never treated as an error.
    Unknown,
}

impl ThreadStatus {
    pub fn from_value(value: &serde_json::Value) -> Self {
        if let Some(token) = value.as_str() {
            return Self::parse(token);
        }
        if value["type"] == "active" {
            if value["activeFlags"]
                .as_array()
                .is_some_and(|flags| flags.iter().any(|f| f == "waitingOnApproval"))
            {
                return Self::WaitingOnApproval;
            }
            if value["activeFlags"]
                .as_array()
                .is_some_and(|flags| flags.iter().any(|f| f == "waitingOnUserInput"))
            {
                return Self::WaitingOnUserInput;
            }
        }
        value["type"]
            .as_str()
            .map(Self::parse)
            .unwrap_or(Self::Unknown)
    }
    /// Parse the status token.
    ///
    /// Accepts the app-server's camelCase spellings as well as the
    /// hyphen/underscore variants seen in older builds, and falls back to
    /// `Unknown` for anything else. Refusing to guess is the point: a status we
    /// do not understand must not be rendered as "running".
    pub fn parse(value: &str) -> Self {
        let normalized = value.trim();
        match normalized {
            "active" => Self::Active,
            "idle" => Self::Idle,
            "waitingOnApproval" | "waiting-on-approval" | "waiting_on_approval" => {
                Self::WaitingOnApproval
            }
            "waitingOnUserInput" | "waiting-on-user-input" | "waiting_on_user_input" => {
                Self::WaitingOnUserInput
            }
            "systemError" | "system-error" | "system_error" => Self::SystemError,
            _ => Self::Unknown,
        }
    }

    /// Map to the module's own vocabulary.
    ///
    /// Both waiting variants collapse to `Waiting` because the island shows the
    /// same thing either way: the task needs the user. The distinction is kept
    /// in `details` so the peek row can say which kind of attention is wanted.
    pub fn to_activity_state(self) -> ActivityState {
        match self {
            Self::Active => ActivityState::Running,
            Self::WaitingOnApproval | Self::WaitingOnUserInput => ActivityState::Waiting,
            Self::Idle => ActivityState::Idle,
            Self::SystemError => ActivityState::Failed,
            Self::Unknown => ActivityState::Unknown,
        }
    }

    /// Whether the user, rather than the machine, is the blocker.
    pub fn needs_the_user(self) -> bool {
        matches!(self, Self::WaitingOnApproval | Self::WaitingOnUserInput)
    }

    /// A short label for the peek row. Not user content.
    pub fn attention_label(self) -> Option<&'static str> {
        match self {
            Self::WaitingOnApproval => Some("Approval needed"),
            Self::WaitingOnUserInput => Some("Input needed"),
            Self::SystemError => Some("Error"),
            _ => None,
        }
    }

    /// The token for diagnostics and for the activity's `status` detail.
    ///
    /// This is the provider's own vocabulary, not the island's: keeping it
    /// distinct is what lets diagnostics say "idle" where the island says
    /// nothing at all.
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Active => "active",
            Self::Idle => "idle",
            Self::WaitingOnApproval => "waitingOnApproval",
            Self::WaitingOnUserInput => "waitingOnUserInput",
            Self::SystemError => "systemError",
            Self::Unknown => "unknown",
        }
    }
}

/// One thread as reported by `thread/list` or a status notification.
#[derive(Debug, Clone, PartialEq)]
pub struct ThreadRecord {
    pub id: String,
    pub status: ThreadStatus,
    /// Present only if the app-server chose to include it. Never synthesised.
    pub title: Option<String>,
    pub updated_at: Option<u64>,
}

impl ThreadRecord {
    /// Parse one thread object.
    ///
    /// Returns `None` when the object has no usable id, because an activity
    /// without a stable id cannot be updated in place and would stack up a new
    /// row on every poll.
    pub fn parse(value: &serde_json::Value) -> Option<Self> {
        let object = value.as_object()?;
        let id = object.get("id")?.as_str()?.trim();
        if id.is_empty() || id.len() > 128 {
            return None;
        }
        let status = object
            .get("status")
            .map(ThreadStatus::from_value)
            .unwrap_or(ThreadStatus::Unknown);
        let title = object
            .get("title")
            .or_else(|| object.get("name"))
            .and_then(serde_json::Value::as_str)
            .map(str::trim)
            .filter(|value| !value.is_empty())
            // A title that came from the app-server may be a user prompt. Cap
            // it hard so an arbitrary string cannot be pushed into the island's
            // fixed-width pill.
            .map(|value| value.chars().take(120).collect::<String>());
        let updated_at = object
            .get("updatedAt")
            .or_else(|| object.get("updated_at"))
            .and_then(serde_json::Value::as_u64);
        Some(Self {
            id: id.to_string(),
            status,
            title,
            updated_at,
        })
    }

    /// Parse a `thread/list` response, tolerating either a bare array or an
    /// object wrapping one under `threads`/`data`.
    pub fn parse_list(value: &serde_json::Value) -> Vec<Self> {
        let array = value
            .as_array()
            .cloned()
            .or_else(|| {
                value
                    .get("threads")
                    .or_else(|| value.get("data"))
                    .and_then(serde_json::Value::as_array)
                    .cloned()
            })
            .unwrap_or_default();
        array.iter().filter_map(Self::parse).collect()
    }
}

/// Token accounting for one thread.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct TokenUsage {
    pub input_tokens: Option<u64>,
    pub output_tokens: Option<u64>,
    /// Total context window size, when the app-server reports it.
    pub context_window: Option<u64>,
}

impl TokenUsage {
    pub fn parse(value: &serde_json::Value) -> Self {
        let object = match value.as_object() {
            Some(object) => object,
            None => return Self::default(),
        };
        let counter = |name: &str| object.get(name).and_then(serde_json::Value::as_u64);
        Self {
            input_tokens: counter("inputTokens").or_else(|| counter("input_tokens")),
            output_tokens: counter("outputTokens").or_else(|| counter("output_tokens")),
            context_window: counter("contextWindow")
                .or_else(|| counter("context_window"))
                .or_else(|| counter("modelContextWindow")),
        }
    }

    pub fn total(&self) -> Option<u64> {
        match (self.input_tokens, self.output_tokens) {
            (Some(input), Some(output)) => Some(input.saturating_add(output)),
            (Some(input), None) => Some(input),
            (None, Some(output)) => Some(output),
            (None, None) => None,
        }
    }

    /// Fraction of the context window consumed, or `None` when unknown.
    ///
    /// Returning `None` rather than `0.0` matters: the island hides the row
    /// instead of claiming the context is empty.
    pub fn context_fraction(&self) -> Option<f64> {
        let window = self.context_window?;
        if window == 0 {
            return None;
        }
        Some((self.total()? as f64 / window as f64).clamp(0.0, 1.0))
    }

    /// Render as a compact human string, or `None` when nothing is known.
    pub fn summary(&self) -> Option<String> {
        let total = self.total()?;
        Some(match self.context_fraction() {
            Some(fraction) => format!(
                "Context {}% · {}",
                (fraction * 100.0).round(),
                compact(total)
            ),
            None => format!("{} tokens", compact(total)),
        })
    }
}

/// One account quota window, never a thread's token budget.
#[derive(Debug, Clone, Copy, PartialEq, Default, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RateLimitWindow {
    pub used_fraction: Option<f64>,
    pub window_minutes: Option<u64>,
    /// Official Unix seconds, not milliseconds or a locally guessed deadline.
    pub resets_at: Option<u64>,
}

impl RateLimitWindow {
    fn parse(value: &serde_json::Value) -> Option<Self> {
        let object = value.as_object()?;
        let read = |name: &str| object.get(name);
        let used = read("usedPercent")
            .or_else(|| read("used_percent"))
            .and_then(serde_json::Value::as_f64)
            .map(|percent| percent / 100.0)
            .or_else(|| read("used").and_then(serde_json::Value::as_f64));
        let window_minutes = read("windowMinutes")
            .or_else(|| read("windowDurationMins"))
            .or_else(|| read("window_minutes"))
            .and_then(serde_json::Value::as_u64);
        let resets_at = read("resetsAt")
            .or_else(|| read("resets_at"))
            .and_then(serde_json::Value::as_u64)
            .filter(|seconds| *seconds <= 253_402_300_799);
        let window = Self {
            used_fraction: used
                .filter(|value| value.is_finite())
                .map(|value| value.clamp(0.0, 1.0)),
            window_minutes,
            resets_at,
        };
        (window.used_fraction.is_some()
            || window.window_minutes.is_some()
            || window.resets_at.is_some())
        .then_some(window)
    }

    pub fn remaining_percent(&self) -> Option<f64> {
        self.used_fraction.map(|fraction| (1.0 - fraction) * 100.0)
    }

    fn label(&self) -> String {
        match self.window_minutes {
            Some(10_080) => "每周".into(),
            Some(minutes) if minutes > 0 && minutes % 60 == 0 => format!("{}小时", minutes / 60),
            Some(minutes) if minutes > 0 => format!("{minutes}分钟"),
            _ => "额度".into(),
        }
    }

    pub fn reset_in(&self, now: u64) -> Option<String> {
        let at = self.resets_at?;
        let remaining = at.saturating_sub(now).div_ceil(60);
        Some(if at <= now {
            "待刷新".into()
        } else if remaining >= 1440 {
            format!("{}天{}小时", remaining / 1440, remaining % 1440 / 60)
        } else if remaining >= 60 {
            format!("{}小时{}分", remaining / 60, remaining % 60)
        } else {
            format!("{remaining}分")
        })
    }
}

/// Account-wide rate limit state from `account/rateLimits/read`.
#[derive(Debug, Clone, Copy, PartialEq, Default, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RateLimits {
    pub primary: Option<RateLimitWindow>,
    pub secondary: Option<RateLimitWindow>,
}

impl RateLimits {
    pub fn parse(value: &serde_json::Value) -> Self {
        // A present multi-bucket view is authoritative. Never substitute an
        // unrelated model's bucket when it does not contain Codex.
        let value =
            if let Some(buckets) = value.get("rateLimitsByLimitId").filter(|v| v.is_object()) {
                buckets.get("codex").unwrap_or(&serde_json::Value::Null)
            } else {
                value.get("rateLimits").unwrap_or(value)
            };
        Self {
            primary: RateLimitWindow::parse(value.get("primary").unwrap_or(value)),
            secondary: value.get("secondary").and_then(RateLimitWindow::parse),
        }
    }

    pub fn summary(&self) -> Option<String> {
        let window = self.primary.or(self.secondary)?;
        let fraction = window.used_fraction?;
        Some(match window.window_minutes {
            Some(minutes) if minutes >= 1440 => {
                format!("Weekly usage {}%", (fraction * 100.0).round())
            }
            Some(minutes) => format!("Usage {}% · {}m", (fraction * 100.0).round(), minutes),
            None => format!("Usage {}%", (fraction * 100.0).round()),
        })
    }

    pub fn remaining_summary(&self) -> Option<String> {
        let rows: Vec<_> = [self.primary, self.secondary]
            .into_iter()
            .flatten()
            .filter_map(|window| {
                window
                    .remaining_percent()
                    .map(|percent| format!("{}剩余 {percent:.0}%", window.label()))
            })
            .collect();
        if rows.is_empty() {
            return None;
        }
        let now = crate::activity::now_millis() / 1000;
        let resets: Vec<_> = [self.primary, self.secondary]
            .into_iter()
            .flatten()
            .filter_map(|window| {
                window
                    .reset_in(now)
                    .map(|time| format!("{} {time}", window.label()))
            })
            .collect();
        let mut summary = format!("Codex · {}", rows.join(" · "));
        if !resets.is_empty() {
            summary.push_str(&format!("\n重置 · {}", resets.join(" / ")));
        }
        Some(summary)
    }

    /// The primary quota in the clock header; the expanded footer retains both.
    pub fn header_summary(&self) -> Option<String> {
        let window = [self.primary, self.secondary]
            .into_iter()
            .flatten()
            .find(|w| w.used_fraction.is_some())?;
        let percent = window.remaining_percent()?;
        let reset = window
            .reset_in(crate::activity::now_millis() / 1000)
            .map(|time| {
                if time == "待刷新" {
                    "等待刷新".into()
                } else {
                    format!("{time}后重置")
                }
            })
            .unwrap_or_else(|| "重置时间未知".into());
        Some(format!("{}剩余 {percent:.0}% · {reset}", window.label()))
    }
}

/// Compact large counts for a pill that has room for four characters.
pub fn compact(value: u64) -> String {
    if value >= 1_000_000_000 {
        format!("{:.1}B", value as f64 / 1_000_000_000.0)
    } else if value >= 1_000_000 {
        format!("{:.0}M", value as f64 / 1_000_000.0)
    } else if value >= 1_000 {
        format!("{:.0}K", value as f64 / 1_000.0)
    } else {
        value.to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn every_documented_thread_status_parses() {
        assert_eq!(ThreadStatus::parse("active"), ThreadStatus::Active);
        assert_eq!(ThreadStatus::parse("idle"), ThreadStatus::Idle);
        assert_eq!(
            ThreadStatus::parse("waitingOnApproval"),
            ThreadStatus::WaitingOnApproval
        );
        assert_eq!(
            ThreadStatus::parse("waitingOnUserInput"),
            ThreadStatus::WaitingOnUserInput
        );
        assert_eq!(
            ThreadStatus::parse("systemError"),
            ThreadStatus::SystemError
        );
    }

    #[test]
    fn official_object_status_and_nested_quota_shape_are_supported() {
        assert_eq!(
            ThreadStatus::from_value(&json!({"type":"active","activeFlags":["waitingOnApproval"]})),
            ThreadStatus::WaitingOnApproval
        );
        assert_eq!(
            ThreadRecord::parse(&json!({"id":"a","status":{"type":"active","activeFlags":[]}}))
                .unwrap()
                .status,
            ThreadStatus::Active
        );
        let limits = RateLimits::parse(
            &json!({"rateLimits":{"primary":{"usedPercent":0.76,"windowDurationMins":300}}}),
        );
        assert_eq!(limits.primary.unwrap().used_fraction, Some(0.0076));
        assert_eq!(limits.primary.unwrap().window_minutes, Some(300));
    }

    #[test]
    fn an_unrecognised_status_is_unknown_rather_than_assumed_active() {
        // Leading and trailing whitespace is trimmed, because a transport that
        // pads a value has not changed the value. Casing, however, is part of
        // the protocol: `ACTIVE` is not `active`, and accepting it would mean
        // silently tolerating a protocol change.
        assert_eq!(ThreadStatus::parse("  active  "), ThreadStatus::Active);

        for token in [
            "", "  ", "exploded", "ACTIVE", "Active", "waiting", "running",
        ] {
            assert_eq!(
                ThreadStatus::parse(token),
                ThreadStatus::Unknown,
                "{token:?} must not be guessed at"
            );
        }
    }

    #[test]
    fn status_maps_to_the_module_vocabulary() {
        assert_eq!(
            ThreadStatus::Active.to_activity_state(),
            ActivityState::Running
        );
        assert_eq!(
            ThreadStatus::WaitingOnApproval.to_activity_state(),
            ActivityState::Waiting
        );
        assert_eq!(
            ThreadStatus::WaitingOnUserInput.to_activity_state(),
            ActivityState::Waiting
        );
        assert_eq!(ThreadStatus::Idle.to_activity_state(), ActivityState::Idle);
        assert_eq!(
            ThreadStatus::SystemError.to_activity_state(),
            ActivityState::Failed
        );
        assert_eq!(
            ThreadStatus::Unknown.to_activity_state(),
            ActivityState::Unknown
        );
    }

    #[test]
    fn only_the_waiting_statuses_claim_the_user_is_the_blocker() {
        assert!(ThreadStatus::WaitingOnApproval.needs_the_user());
        assert!(ThreadStatus::WaitingOnUserInput.needs_the_user());
        for status in [
            ThreadStatus::Active,
            ThreadStatus::Idle,
            ThreadStatus::SystemError,
            ThreadStatus::Unknown,
        ] {
            assert!(
                !status.needs_the_user(),
                "{status:?} must not claim user attention"
            );
        }
    }

    #[test]
    fn a_thread_without_an_id_is_dropped() {
        assert!(ThreadRecord::parse(&json!({"status": "active"})).is_none());
        assert!(ThreadRecord::parse(&json!({"id": "", "status": "active"})).is_none());
        assert!(ThreadRecord::parse(&json!({"id": "   "})).is_none());
        assert!(ThreadRecord::parse(&json!({"id": "t1"})).is_some());
    }

    #[test]
    fn a_missing_status_becomes_unknown_not_running() {
        let record = ThreadRecord::parse(&json!({"id": "t1"})).expect("parsed");
        assert_eq!(record.status, ThreadStatus::Unknown);
        assert_eq!(record.status.to_activity_state(), ActivityState::Unknown);
    }

    #[test]
    fn malformed_thread_shapes_do_not_panic() {
        for value in [
            json!(null),
            json!(42),
            json!("a string"),
            json!([]),
            json!({"id": 123}),
            json!({"id": "t1", "status": 7}),
            json!({"id": "t1", "title": false}),
        ] {
            let _ = ThreadRecord::parse(&value);
        }
    }

    #[test]
    fn an_over_long_title_is_truncated_rather_than_pushed_into_the_pill() {
        let long = "x".repeat(5000);
        let record = ThreadRecord::parse(&json!({"id": "t1", "title": long})).expect("parsed");
        assert_eq!(record.title.expect("title").chars().count(), 120);
    }

    #[test]
    fn thread_lists_parse_from_all_three_shapes() {
        let bare = json!([{"id": "a"}, {"id": "b"}]);
        assert_eq!(ThreadRecord::parse_list(&bare).len(), 2);

        let wrapped = json!({"threads": [{"id": "a"}]});
        assert_eq!(ThreadRecord::parse_list(&wrapped).len(), 1);

        let data = json!({"data": [{"id": "a"}, {"id": "b"}, {"id": "c"}]});
        assert_eq!(ThreadRecord::parse_list(&data).len(), 3);

        // An empty or unrecognised shape is an empty list, not a panic.
        assert!(ThreadRecord::parse_list(&json!({})).is_empty());
        assert!(ThreadRecord::parse_list(&json!(null)).is_empty());
        assert!(ThreadRecord::parse_list(&json!({"threads": "nope"})).is_empty());
    }

    #[test]
    fn token_usage_reports_unknown_instead_of_zero() {
        let usage = TokenUsage::parse(&json!({}));
        assert_eq!(usage.total(), None);
        assert_eq!(usage.context_fraction(), None);
        assert_eq!(usage.summary(), None);

        let usage = TokenUsage::parse(&json!({"inputTokens": 1000, "outputTokens": 500}));
        assert_eq!(usage.total(), Some(1500));
        assert_eq!(
            usage.context_fraction(),
            None,
            "no window means no percentage"
        );
        // 1500 is rendered through the same compactor the pill uses, so it reads
        // as "2K" rather than a five-digit number in a narrow row.
        assert_eq!(usage.summary().as_deref(), Some("2K tokens"));
    }

    #[test]
    fn context_percentage_uses_the_reported_window() {
        let usage = TokenUsage::parse(&json!({
            "inputTokens": 72, "outputTokens": 0, "contextWindow": 100
        }));
        assert_eq!(usage.context_fraction(), Some(0.72));
        assert_eq!(usage.summary().as_deref(), Some("Context 72% · 72"));

        // Overrun clamps rather than reporting more than full.
        let usage = TokenUsage::parse(&json!({
            "inputTokens": 500, "contextWindow": 100
        }));
        assert_eq!(usage.context_fraction(), Some(1.0));

        // A zero window is a provider bug, not a 0% reading.
        let usage = TokenUsage::parse(&json!({"inputTokens": 5, "contextWindow": 0}));
        assert_eq!(usage.context_fraction(), None);
    }

    #[test]
    fn token_usage_accepts_snake_case_and_missing_halves() {
        let usage = TokenUsage::parse(&json!({"input_tokens": 30, "output_tokens": 20}));
        assert_eq!(usage.total(), Some(50));
        let usage = TokenUsage::parse(&json!({"inputTokens": 30}));
        assert_eq!(usage.total(), Some(30));
        let usage = TokenUsage::parse(&json!({"outputTokens": 20}));
        assert_eq!(usage.total(), Some(20));
    }

    #[test]
    fn rate_limits_normalise_a_percentage_and_a_fraction_alike() {
        let percent = RateLimits::parse(&json!({"usedPercent": 76}));
        assert_eq!(percent.primary.unwrap().used_fraction, Some(0.76));
        let fraction = RateLimits::parse(&json!({"used": 0.76}));
        assert_eq!(fraction.primary.unwrap().used_fraction, Some(0.76));
        assert_eq!(
            RateLimits::parse(&json!({"primary": {"usedPercent": 76}}))
                .primary
                .unwrap()
                .used_fraction,
            Some(0.76)
        );
    }

    #[test]
    fn rate_limits_report_nothing_when_nothing_is_known() {
        assert_eq!(RateLimits::parse(&json!({})).primary, None);
        assert_eq!(RateLimits::parse(&json!(null)).summary(), None);
        assert_eq!(
            RateLimits::parse(&json!({"usedPercent": "lots"})).summary(),
            None
        );
    }

    #[test]
    fn both_codex_quota_windows_preserve_official_resets_and_zero_values() {
        let limits = RateLimits::parse(&json!({
            "rateLimits": {"primary":{"usedPercent":99}},
            "rateLimitsByLimitId": {"codex":{
                "primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1800000000_u64},
                "secondary":{"usedPercent":0,"windowDurationMins":10080,"resetsAt":1800500000_u64}
            }}
        }));
        assert_eq!(limits.primary.unwrap().remaining_percent(), Some(75.0));
        assert_eq!(limits.secondary.unwrap().remaining_percent(), Some(100.0));
        let serialized = serde_json::to_value(limits).unwrap();
        assert_eq!(serialized["primary"]["resetsAt"], 1800000000_u64);
        assert_eq!(serialized["secondary"]["windowMinutes"], 10080);
        let summary = limits.remaining_summary().unwrap();
        assert!(summary.contains("5小时剩余 75%"));
        assert!(summary.contains("每周剩余 100%"));
        assert!(summary.contains("\n重置 ·"));
    }

    #[test]
    fn clock_header_uses_one_matching_quota_window_and_never_invents_a_reset() {
        let limits = RateLimits::parse(&json!({
            "primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":null},
            "secondary":{"usedPercent":50,"windowDurationMins":10080,"resetsAt":1800500000_u64}
        }));
        assert_eq!(
            limits.header_summary().as_deref(),
            Some("5小时剩余 75% · 重置时间未知")
        );
        let secondary_only = RateLimits::parse(
            &json!({"primary":null,"secondary":{"usedPercent":0,"windowDurationMins":10080,"resetsAt":1800500000_u64}}),
        );
        let header = secondary_only.header_summary().unwrap();
        assert!(header.starts_with("每周剩余 100% · "));
        assert!(header.ends_with("后重置"));
        assert!(RateLimits::default().header_summary().is_none());
    }

    #[test]
    fn unknown_quota_and_invalid_resets_never_become_zero_usage_or_other_buckets() {
        let limits = RateLimits::parse(
            &json!({"primary":{"usedPercent":null,"resetsAt":-1},"secondary":{"usedPercent":100,"resetsAt":null}}),
        );
        assert!(limits.primary.is_none());
        assert_eq!(limits.secondary.unwrap().remaining_percent(), Some(0.0));
        assert!(limits.secondary.unwrap().resets_at.is_none());
        assert!(RateLimits::parse(&json!({"resetsAt":u64::MAX}))
            .primary
            .is_none());
        assert!(RateLimits::parse(&json!({"rateLimitsByLimitId":{"other":{"primary":{"usedPercent":12}}},"rateLimits":{"primary":{"usedPercent":22}}})).primary.is_none());
    }

    #[test]
    fn rate_limit_summary_names_a_long_window_as_weekly() {
        let weekly = RateLimits::parse(&json!({"usedPercent": 76, "windowMinutes": 10080}));
        assert_eq!(weekly.summary().as_deref(), Some("Weekly usage 76%"));
        let hourly = RateLimits::parse(&json!({"usedPercent": 40, "windowMinutes": 300}));
        assert_eq!(hourly.summary().as_deref(), Some("Usage 40% · 300m"));
    }

    #[test]
    fn compact_counts_stay_readable() {
        assert_eq!(compact(0), "0");
        assert_eq!(compact(999), "999");
        assert_eq!(compact(1_500), "2K");
        assert_eq!(compact(184_000_000), "184M");
        assert_eq!(compact(2_500_000_000), "2.5B");
    }
}
