//! A synthetic provider used to exercise the island before any real one works.
//!
//! This exists because building an overlay against a provider that may never
//! connect means every layout and animation bug is entangled with a connection
//! bug. The mock produces every state the overlay must render, on demand, with
//! deterministic content, so a visual defect can be reproduced in one call.

use std::collections::{BTreeMap, VecDeque};

use crate::activity::{ActivityProgress, ActivityState, LiveActivity, ProviderKind};
use crate::diagnostics;
use crate::providers::{Provider, ProviderHealth, ProviderStatus};

/// One scripted activity the mock can produce.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MockScenario {
    Working,
    Waiting,
    Success,
    Failed,
    Progress,
}

impl MockScenario {
    pub fn parse(value: &str) -> Option<Self> {
        match value {
            "working" => Some(Self::Working),
            "waiting" => Some(Self::Waiting),
            "success" => Some(Self::Success),
            "failed" => Some(Self::Failed),
            "progress" => Some(Self::Progress),
            _ => None,
        }
    }

    pub fn state(self) -> ActivityState {
        match self {
            Self::Working => ActivityState::Running,
            Self::Waiting => ActivityState::Waiting,
            Self::Success => ActivityState::Success,
            Self::Failed => ActivityState::Failed,
            Self::Progress => ActivityState::Running,
        }
    }
}

#[derive(Debug, Default)]
pub struct MockProvider {
    enabled: bool,
    /// Stable identities; display order is decided by the broker.
    activities: BTreeMap<String, LiveActivity>,
    insertion_order: VecDeque<String>,
    /// Which activity ids are progress scenarios.
    ///
    /// Tracked explicitly rather than rediscovered from the id string.
    ticking: std::collections::BTreeSet<String>,
    next_serial: u64,
}

impl MockProvider {
    pub fn new() -> Self {
        Self::default()
    }

    /// Add one scripted activity and return its id.
    pub fn emit(&mut self, scenario: MockScenario) -> String {
        self.next_serial = self.next_serial.saturating_add(1);
        let serial = self.next_serial;
        let id = format!("mock-{}-{serial}", scenario_state_token(scenario));
        let mut activity = self.build(scenario, serial);
        activity.id = id.clone();
        if scenario == MockScenario::Progress {
            self.ticking.insert(id.clone());
        }
        self.activities.insert(id.clone(), activity);
        self.insertion_order.push_back(id.clone());
        while self.activities.len() > crate::broker::MAX_ACTIVITIES {
            if let Some(oldest) = self.insertion_order.pop_front() {
                self.activities.remove(&oldest);
                self.ticking.remove(&oldest);
            }
        }
        diagnostics::information(
            "provider/mock",
            &format!("created mock activity {id} ({})", scenario.state().as_str()),
        );
        id
    }

    /// Populate one of every state at once. Answers "what does a full island
    /// look like" without the caller issuing five separate commands.
    pub fn emit_all(&mut self) -> Vec<String> {
        [
            MockScenario::Waiting,
            MockScenario::Progress,
            MockScenario::Working,
            MockScenario::Failed,
            MockScenario::Success,
        ]
        .into_iter()
        .map(|scenario| self.emit(scenario))
        .collect()
    }

    fn build(&self, scenario: MockScenario, serial: u64) -> LiveActivity {
        let state = scenario.state();
        let mut activity = LiveActivity::running(
            format!("pending-{serial}"),
            ProviderKind::Mock,
            "scenario",
            title_for(scenario, serial),
        )
        .with_state(state)
        .with_subtitle(subtitle_for(scenario))
        .with_detail("source", "mock")
        .with_detail("scenario", scenario_state_token(scenario));

        match scenario {
            MockScenario::Progress => {
                // Each task starts at zero and advances independently.
                let step = 0.0;
                activity = activity
                    .with_progress(ActivityProgress::determinate(step, 20.0))
                    .with_detail("stage", format!("{}/20", step as u64));
            }
            MockScenario::Waiting => {
                // A waiting-on-user activity is the one the user must act on, so
                // it carries the action the expanded island would render.
                activity = activity.with_action("acknowledge", "Acknowledge");
            }
            _ => {}
        }
        activity
    }
}

fn scenario_state_token(scenario: MockScenario) -> &'static str {
    match scenario {
        MockScenario::Working => "working",
        MockScenario::Waiting => "waiting",
        MockScenario::Success => "success",
        MockScenario::Failed => "failed",
        MockScenario::Progress => "progress",
    }
}

fn title_for(scenario: MockScenario, serial: u64) -> String {
    match scenario {
        MockScenario::Working => format!("Mock task {serial}"),
        MockScenario::Waiting => format!("Mock approval {serial}"),
        MockScenario::Success => format!("Mock completion {serial}"),
        MockScenario::Failed => format!("Mock failure {serial}"),
        MockScenario::Progress => "Mock export".to_string(),
    }
}

fn subtitle_for(scenario: MockScenario) -> &'static str {
    match scenario {
        MockScenario::Working => "Working in the background",
        MockScenario::Waiting => "Waiting for you",
        MockScenario::Success => "Finished",
        MockScenario::Failed => "Could not finish",
        MockScenario::Progress => "Compressing assets",
    }
}

impl Provider for MockProvider {
    fn start(&mut self) -> ProviderStatus {
        self.enabled = true;
        diagnostics::information("provider/mock", "mock provider started");
        self.status()
    }

    fn stop(&mut self) {
        self.enabled = false;
        self.activities.clear();
        self.insertion_order.clear();
        self.ticking.clear();
        diagnostics::information("provider/mock", "mock provider stopped");
    }

    fn set_enabled(&mut self, enabled: bool) {
        self.enabled = enabled;
    }

    fn poll(&mut self) -> Vec<LiveActivity> {
        if !self.enabled {
            return Vec::new();
        }
        // Advance the scripted progress so a determinate bar visibly moves.
        let advance: Vec<String> = self
            .ticking
            .iter()
            .filter(|id| self.activities.contains_key(*id))
            .cloned()
            .collect();
        for id in &advance {
            if let Some(activity) = self.activities.get_mut(id) {
                // Clamped rather than wrapped: a progress bar that jumps back to
                // zero is a lie about a task that only moves forward, and it
                // would also mean the scripted run never reaches its end.
                let step = activity
                    .progress
                    .as_ref()
                    .map(|p| p.value + 1.0)
                    .unwrap_or(1.0)
                    .min(20.0);
                activity.progress = Some(ActivityProgress::determinate(step, 20.0));
                activity.state = if step >= 20.0 {
                    self.ticking.remove(id);
                    ActivityState::Success
                } else {
                    ActivityState::Running
                };
            }
        }
        self.activities.values().cloned().collect()
    }

    fn status(&self) -> ProviderStatus {
        if !self.enabled {
            return crate::providers::disabled_status(ProviderKind::Mock, "mock provider is off");
        }
        ProviderStatus {
            kind: ProviderKind::Mock,
            health: ProviderHealth::Connected,
            detail: Some(format!("{} scripted activities", self.activities.len())),
            activity_count: self.activities.len(),
        }
    }
}

impl MockProvider {
    /// The activities as they currently stand, without advancing any counter.
    /// Used by the preview path, which must not disturb real state.
    pub fn peek(&self) -> Vec<LiveActivity> {
        self.activities.values().cloned().collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn scenario_tokens_round_trip_and_reject_nonsense() {
        for token in ["working", "waiting", "success", "failed", "progress"] {
            assert!(MockScenario::parse(token).is_some(), "{token} must parse");
        }
        assert!(MockScenario::parse("exploded").is_none());
        assert!(MockScenario::parse("").is_none());
    }

    #[test]
    fn emitting_every_scenario_covers_every_renderable_state() {
        let mut provider = MockProvider::new();
        provider.start();
        provider.emit_all();
        let states = provider
            .peek()
            .into_iter()
            .map(|activity| activity.state)
            .collect::<Vec<_>>();
        for expected in [
            ActivityState::Waiting,
            ActivityState::Running,
            ActivityState::Failed,
            ActivityState::Success,
        ] {
            assert!(
                states.contains(&expected),
                "scenario set must include {expected:?}"
            );
        }
    }

    #[test]
    fn ids_are_unique_and_stable_across_polls() {
        let mut provider = MockProvider::new();
        provider.start();
        provider.emit(MockScenario::Working);
        provider.emit(MockScenario::Working);

        let first = provider.poll();
        assert_eq!(first.len(), 2);
        assert_ne!(
            first[0].id, first[1].id,
            "each emitted activity needs its own identity"
        );

        let second = provider.poll();
        assert_eq!(
            first.iter().map(|a| a.id.clone()).collect::<Vec<_>>(),
            second.iter().map(|a| a.id.clone()).collect::<Vec<_>>(),
            "polling an unchanged scenario must not reshuffle the stack"
        );
    }

    #[test]
    fn adding_a_different_scenario_never_renames_existing_tasks() {
        let mut provider = MockProvider::new();
        provider.start();
        let id = provider.emit(MockScenario::Working);
        assert!(provider.poll().iter().any(|a| a.id == id));
        provider.emit(MockScenario::Waiting);
        assert!(provider
            .poll()
            .iter()
            .any(|a| a.id == id && a.state == ActivityState::Running));
    }

    #[test]
    fn new_progress_starts_at_zero_after_many_idle_ticks() {
        let mut provider = MockProvider::new();
        provider.start();
        for _ in 0..100 {
            provider.poll();
        }
        provider.emit(MockScenario::Progress);
        let activity = provider.poll().pop().unwrap();
        assert_eq!(activity.state, ActivityState::Running);
        assert_eq!(activity.progress.unwrap().value, 1.0);
    }

    #[test]
    fn scripted_provider_storage_is_bounded_before_broker_ingestion() {
        let mut provider = MockProvider::new();
        provider.start();
        let oldest = provider.emit(MockScenario::Progress);
        for _ in 0..crate::broker::MAX_ACTIVITIES + 10 {
            provider.emit(MockScenario::Progress);
        }
        assert_eq!(provider.peek().len(), crate::broker::MAX_ACTIVITIES);
        assert_eq!(provider.ticking.len(), crate::broker::MAX_ACTIVITIES);
        assert_eq!(
            provider.insertion_order.len(),
            crate::broker::MAX_ACTIVITIES
        );
        assert!(!provider.activities.contains_key(&oldest));
        provider.stop();
        assert!(provider.insertion_order.is_empty());
    }

    #[test]
    fn progress_advances_and_then_lands_on_success() {
        let mut provider = MockProvider::new();
        provider.start();
        provider.emit(MockScenario::Progress);

        let mut fractions = Vec::new();
        for _ in 0..21 {
            let activities = provider.poll();
            let activity = activities.first().expect("progress activity");
            if let Some(fraction) = activity.progress.as_ref().and_then(|p| p.fraction()) {
                fractions.push(fraction);
            }
        }
        assert!(
            fractions.windows(2).any(|pair| pair[1] != pair[0]),
            "a determinate bar must actually move"
        );
        let last = provider.poll();
        assert_eq!(
            last[0].state,
            ActivityState::Success,
            "a finished mock must report success so the linger path is exercised"
        );
    }

    #[test]
    fn stopping_clears_activities_so_nothing_is_left_on_screen() {
        let mut provider = MockProvider::new();
        provider.start();
        provider.emit_all();
        assert!(!provider.peek().is_empty());
        provider.stop();
        assert!(provider.peek().is_empty());
        assert_eq!(provider.status().health, ProviderHealth::Disabled);
        assert_eq!(provider.poll().len(), 0);
    }

    #[test]
    fn a_stopped_provider_reports_disabled_rather_than_an_error() {
        let provider = MockProvider::new();
        let status = provider.status();
        assert_eq!(status.health, ProviderHealth::Disabled);
        assert_eq!(status.activity_count, 0);
    }

    #[test]
    fn waiting_scenarios_carry_the_action_the_expanded_island_renders() {
        let mut provider = MockProvider::new();
        provider.start();
        provider.emit(MockScenario::Waiting);
        let activities = provider.peek();
        assert_eq!(activities[0].state, ActivityState::Waiting);
        assert!(
            !activities[0].actions.is_empty(),
            "a waiting activity with no action gives the user nothing to do"
        );
    }

    #[test]
    fn mock_details_never_contain_user_content() {
        let mut provider = MockProvider::new();
        provider.start();
        provider.emit_all();
        for activity in provider.peek() {
            for (key, value) in &activity.details {
                assert!(
                    !value.contains('@') && !value.contains("sk-"),
                    "mock detail {key} looks like it could leak content: {value}"
                );
            }
        }
    }
}
