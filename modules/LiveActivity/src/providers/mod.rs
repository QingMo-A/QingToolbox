//! Providers turn external reality into `LiveActivity` values.
//!
//! The trait is deliberately the narrowest thing that works: a provider may
//! produce activities and nothing else. It cannot see the broker, cannot see
//! the overlay, and cannot ask the host to do anything. A provider that wants
//! to change what is on screen has exactly one option — emit an activity — and
//! that keeps the data flow from turning into a graph.

pub mod codex;
pub mod mock;

use crate::activity::{LiveActivity, ProviderKind};

/// How a provider's last attempt to connect went.
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ProviderHealth {
    /// Available and reporting.
    Connected,
    /// Configured but not currently usable. Expected, not an error.
    Disconnected,
    /// The provider cannot run at all here (missing binary, wrong platform).
    Unavailable,
    /// Off by the user's choice.
    Disabled,
}

impl ProviderHealth {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Connected => "connected",
            Self::Disconnected => "disconnected",
            Self::Unavailable => "unavailable",
            Self::Disabled => "disabled",
        }
    }
}

/// What a provider reports about itself for the settings page.
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProviderStatus {
    pub kind: ProviderKind,
    pub health: ProviderHealth,
    /// Human-readable explanation. Never contains user content.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub detail: Option<String>,
    /// Activities this provider currently owns.
    pub activity_count: usize,
}

/// One source of long-running work.
pub trait Provider: Send {
    /// Bring the provider up. Must not block indefinitely: a provider that
    /// cannot connect reports `Disconnected` and keeps retrying rather than
    /// stalling startup.
    fn start(&mut self) -> ProviderStatus;

    /// Stop whatever is running. Must be safe to call when not started, and
    /// must not leave a child process behind.
    fn stop(&mut self);

    /// Apply a configuration change without a full restart.
    fn set_enabled(&mut self, enabled: bool);

    /// Current activities, replacing whatever this provider reported before.
    /// Returning an empty vector means "I own nothing right now".
    fn poll(&mut self) -> Vec<LiveActivity>;

    fn status(&self) -> ProviderStatus;
}

/// Helper for providers that need the standard "disabled" reporting.
pub fn disabled_status(kind: ProviderKind, detail: &str) -> ProviderStatus {
    ProviderStatus {
        kind,
        health: ProviderHealth::Disabled,
        detail: Some(detail.to_string()),
        activity_count: 0,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn health_tokens_are_stable_lowercase_strings() {
        assert_eq!(ProviderHealth::Connected.as_str(), "connected");
        assert_eq!(ProviderHealth::Disconnected.as_str(), "disconnected");
        assert_eq!(ProviderHealth::Unavailable.as_str(), "unavailable");
        assert_eq!(ProviderHealth::Disabled.as_str(), "disabled");
    }

    #[test]
    fn status_serialises_without_a_detail_when_there_is_none() {
        let status = ProviderStatus {
            kind: ProviderKind::Media,
            health: ProviderHealth::Unavailable,
            detail: None,
            activity_count: 0,
        };
        let json = serde_json::to_value(&status).expect("serialise");
        assert_eq!(json["kind"], "media");
        assert!(json.get("detail").is_none());
        assert_eq!(json["activityCount"], 0);
    }

    #[test]
    fn disabled_status_carries_a_reason() {
        let status = disabled_status(ProviderKind::Codex, "off by user choice");
        assert_eq!(status.health, ProviderHealth::Disabled);
        assert_eq!(status.detail.as_deref(), Some("off by user choice"));
        assert_eq!(status.activity_count, 0);
    }
}
