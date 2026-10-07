//! The shared vocabulary every provider speaks and the overlay renders.
//!
//! A `LiveActivity` is deliberately a flat, serialisable value rather than a
//! trait object with behaviour. Providers do not push state at the overlay;
//! they hand a value to the broker, and the overlay only ever reads the
//! broker's resolved output. Keeping the payload inert is what makes that
//! direction of control enforceable instead of merely a convention.

use serde::{Deserialize, Serialize};
use std::time::{SystemTime, UNIX_EPOCH};

/// Which provider an activity came from.
///
/// Serialised as a lowercase token so the Web UI never sees a Rust identifier
/// and a future provider can be added without renumbering anything.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ProviderKind {
    /// Synthetic activities used to exercise the island and its settings.
    Mock,
    /// Codex CLI / app-server threads and account usage.
    Codex,
    /// Media sessions. Reserved: no provider produces this yet.
    Media,
    /// File copies and downloads. Reserved: no provider produces this yet.
    Transfer,
}

// `ProviderKind` has no inherent methods: the wire token comes from the serde
// rename below, and which providers are implemented is the settings page's
// table (`ui-src/src/App.vue`), because the page must render a card for an
// unimplemented provider too. Duplicating either here would be a second source
// of truth that can silently disagree.

/// The lifecycle position of one activity.
///
/// This is the *task* state, never the overlay's presentation state. The
/// overlay's own four states live in `overlay.rs`; conflating the two is what
/// turns a status model into a pile of booleans.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ActivityState {
    /// Doing work with no user interaction expected.
    Running,
    /// Blocked until the user does something. The most important state to
    /// surface, because it is the only one where the user is the bottleneck.
    Waiting,
    /// Started, then deliberately stopped short of completion.
    Paused,
    /// Finished successfully. Shown briefly, then retired automatically.
    Success,
    /// Finished unsuccessfully. Outlives a success, because a failure the user
    /// never saw is worse than one they saw twice.
    Failed,
    /// Present but not currently doing anything.
    Idle,
    /// Deliberately abandoned by the user or the owning process.
    Cancelled,
    /// The provider cannot determine the state. Never guessed at.
    Unknown,
}

impl ActivityState {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Running => "running",
            Self::Waiting => "waiting",
            Self::Paused => "paused",
            Self::Success => "success",
            Self::Failed => "failed",
            Self::Idle => "idle",
            Self::Cancelled => "cancelled",
            Self::Unknown => "unknown",
        }
    }

    /// Terminal states are retired by the broker on a timer instead of
    /// lingering until the provider happens to send a removal.
    ///
    /// The broker matches on these variants directly, so this is the single
    /// place that states which ones are terminal; the test beside it is what
    /// keeps the two in step.
    pub fn is_terminal(self) -> bool {
        matches!(self, Self::Success | Self::Failed | Self::Cancelled)
    }

    /// States that describe an activity the user can still resume or inspect.
    pub fn is_active(self) -> bool {
        matches!(
            self,
            Self::Running | Self::Waiting | Self::Paused | Self::Idle
        )
    }
}

/// Progress of a bounded activity.
///
/// `total` is optional because a large class of real work (an agent thread, a
/// streaming download with no `Content-Length`) is genuinely unbounded. In
/// that case the caller reports `indeterminate`, and the island renders a
/// subtle indeterminate motion instead of a fabricated percentage.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ActivityProgress {
    pub value: f64,
    pub total: Option<f64>,
}

impl ActivityProgress {
    pub fn determinate(value: f64, total: f64) -> Self {
        Self {
            value: value.max(0.0),
            total: Some(total.max(0.0)),
        }
    }

    pub fn indeterminate(value: f64) -> Self {
        Self {
            value: value.max(0.0),
            total: None,
        }
    }

    /// A fraction in `0.0..=1.0`, or `None` when the activity is unbounded or
    /// the provider supplied a degenerate range.
    pub fn fraction(&self) -> Option<f64> {
        let total = self.total?;
        if !total.is_finite() || total <= 0.0 || !self.value.is_finite() {
            return None;
        }
        Some((self.value / total).clamp(0.0, 1.0))
    }
}

/// What the user can do about an activity from the expanded island.
///
/// The identifier is bounded and opaque. The overlay renders it and hands it
/// straight back to the module; it is never interpreted by the UI.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ActivityAction {
    pub id: String,
    pub label: String,
}

impl ActivityAction {
    pub fn new(id: impl Into<String>, label: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            label: label.into(),
        }
    }
}

/// One long-running thing worth showing.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LiveActivity {
    /// Stable identity across updates. Providers must reuse the id when a task
    /// changes state, otherwise the island treats every tick as a new task and
    /// the stack visibly flickers.
    pub id: String,
    pub provider: ProviderKind,
    /// Free-form grouping key. Lets one provider own several independent
    /// activity families without inventing synthetic providers.
    pub kind: String,
    pub title: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub subtitle: Option<String>,
    pub state: ActivityState,
    /// Explicit priority. `None` defers to the resolver's state-based default,
    /// which keeps ordinary providers from having to know the ranking.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub priority: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub progress: Option<ActivityProgress>,
    /// Unix milliseconds. `None` when the provider genuinely does not know.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub started_at: Option<u64>,
    pub updated_at: u64,
    /// Short, non-sensitive facts the Peek surface may show. Providers must
    /// never put prompt text, tokens or credentials here.
    #[serde(default, skip_serializing_if = "std::collections::BTreeMap::is_empty")]
    pub details: std::collections::BTreeMap<String, String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub actions: Vec<ActivityAction>,
    /// Provider-private correlation data. Never rendered.
    #[serde(default, skip_serializing_if = "serde_json::Map::is_empty")]
    pub metadata: serde_json::Map<String, serde_json::Value>,
}

impl LiveActivity {
    pub fn running(
        id: impl Into<String>,
        provider: ProviderKind,
        kind: impl Into<String>,
        title: impl Into<String>,
    ) -> Self {
        Self {
            id: id.into(),
            provider,
            kind: kind.into(),
            title: title.into(),
            subtitle: None,
            state: ActivityState::Running,
            priority: None,
            progress: None,
            started_at: Some(now_millis()),
            updated_at: now_millis(),
            details: std::collections::BTreeMap::new(),
            actions: Vec::new(),
            metadata: serde_json::Map::new(),
        }
    }

    pub fn with_subtitle(mut self, subtitle: impl Into<String>) -> Self {
        self.subtitle = Some(subtitle.into());
        self
    }

    pub fn with_state(mut self, state: ActivityState) -> Self {
        self.state = state;
        self
    }

    pub fn with_progress(mut self, progress: ActivityProgress) -> Self {
        self.progress = Some(progress);
        self
    }

    pub fn with_detail(mut self, key: impl Into<String>, value: impl Into<String>) -> Self {
        self.details.insert(key.into(), value.into());
        self
    }

    pub fn with_action(mut self, id: impl Into<String>, label: impl Into<String>) -> Self {
        self.actions.push(ActivityAction::new(id, label));
        self
    }

    /// Refresh the mutation timestamp. Called by the broker rather than by
    /// providers so a replay cannot backdate or future-date an activity.
    pub fn touch(&mut self) {
        self.updated_at = now_millis();
    }
}

/// Unix milliseconds, saturating instead of panicking on a pre-epoch clock.
pub fn now_millis() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis().min(u64::MAX as u128) as u64)
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn progress_fraction_refuses_to_invent_a_ratio() {
        assert_eq!(
            ActivityProgress::determinate(3.0, 4.0).fraction(),
            Some(0.75)
        );
        // Unbounded work has no honest percentage.
        assert_eq!(ActivityProgress::indeterminate(3.0).fraction(), None);
        // A zero or negative total is a provider bug, not a 0% or infinite bar.
        assert_eq!(ActivityProgress::determinate(1.0, 0.0).fraction(), None);
        assert_eq!(ActivityProgress::determinate(1.0, -5.0).fraction(), None);
        // Overrun clamps rather than producing a bar wider than its track.
        assert_eq!(
            ActivityProgress::determinate(9.0, 4.0).fraction(),
            Some(1.0)
        );
    }

    #[test]
    fn states_split_into_terminal_active_and_unknown() {
        assert!(ActivityState::Success.is_terminal());
        assert!(ActivityState::Failed.is_terminal());
        assert!(ActivityState::Cancelled.is_terminal());
        assert!(!ActivityState::Running.is_terminal());
        assert!(ActivityState::Running.is_active());
        assert!(ActivityState::Waiting.is_active());
        // Unknown is neither: it must not be retired on a success timer, and it
        // must not be treated as work in progress either.
        assert!(!ActivityState::Unknown.is_active());
        assert!(!ActivityState::Unknown.is_terminal());
    }

    #[test]
    fn provider_kinds_have_stable_wire_tokens() {
        // Media and Transfer are declared so the wire format and the settings
        // page have a stable name, even though nothing produces them yet. The
        // token comes from the serde rename, and is what the settings page keys
        // its provider cards on, so it must not drift.
        for (kind, token) in [
            (ProviderKind::Mock, "mock"),
            (ProviderKind::Codex, "codex"),
            (ProviderKind::Media, "media"),
            (ProviderKind::Transfer, "transfer"),
        ] {
            assert_eq!(
                serde_json::to_value(kind).expect("serialise"),
                serde_json::Value::String(token.to_string())
            );
        }
    }

    #[test]
    fn activity_round_trips_through_camel_case_json() {
        let activity = LiveActivity::running("a-1", ProviderKind::Codex, "thread", "Working")
            .with_subtitle("Editing main.rs")
            .with_progress(ActivityProgress::determinate(1.0, 4.0))
            .with_detail("context", "72%")
            .with_action("open", "Open");
        let json = serde_json::to_value(&activity).expect("serialise");
        assert_eq!(json["provider"], "codex");
        assert_eq!(json["state"], "running");
        assert!(json.get("subtitle").is_some());
        let decoded: LiveActivity = serde_json::from_value(json).expect("deserialise");
        assert_eq!(decoded, activity);
    }

    #[test]
    fn optional_fields_are_omitted_rather_than_sent_as_null() {
        let json = serde_json::to_value(LiveActivity::running(
            "a-1",
            ProviderKind::Mock,
            "demo",
            "Demo",
        ))
        .expect("serialise");
        // `started_at` is populated by `running`, so it is deliberately not in
        // this list; everything here is genuinely absent until a builder sets it.
        for absent in [
            "subtitle", "priority", "progress", "details", "actions", "metadata",
        ] {
            assert!(json.get(absent).is_none(), "{absent} should be omitted");
        }
        // The two timestamps a real activity always carries.
        assert!(json.get("updatedAt").is_some());
        assert!(json.get("startedAt").is_some());
    }
}
