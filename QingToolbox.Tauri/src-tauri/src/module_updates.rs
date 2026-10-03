//! Background/manual update checks. This layer never executes or downloads a module.
use crate::{
    host_update::parse_version,
    module_repository::{Candidate, DownloadSnapshot},
    modules::ModuleSummary,
    paths::ModuleSource,
};
use serde::Serialize;
use std::{collections::BTreeMap, time::Duration};

#[derive(Clone)]
pub struct CheckTicket {
    pub id: String,
    generation: u64,
}
#[derive(Default)]
struct Entry {
    generation: u64,
    status: String,
    candidate: Option<Candidate>,
}
#[derive(Default)]
pub struct UpdateState {
    sequence: u64,
    entries: BTreeMap<String, Entry>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateView {
    pub module_id: String,
    pub update_status: String,
    pub target_version: Option<String>,
    pub release_notes: Option<String>,
    pub is_update_check_enabled: bool,
    pub can_check_for_update: bool,
    pub is_update_check_busy: bool,
    pub can_download_update: bool,
    pub download_status: String,
    pub is_download_active: bool,
    pub download_bytes_received: u64,
    pub download_expected_bytes: u64,
    pub can_install_verified_update: bool,
}

pub fn selected_ids(
    installed: &[String],
    disabled: &[String],
    requested: Option<&str>,
) -> Vec<String> {
    installed
        .iter()
        .filter(|id| {
            requested
                .map(|wanted| id.as_str() == wanted)
                .unwrap_or_else(|| !disabled.contains(id))
        })
        .cloned()
        .collect()
}

impl UpdateState {
    pub fn begin(&mut self, ids: &[String]) -> Vec<CheckTicket> {
        ids.iter()
            .filter_map(|id| {
                if self.entries.get(id).is_some_and(|e| e.status == "Checking") {
                    return None;
                }
                self.sequence += 1;
                self.entries.insert(
                    id.clone(),
                    Entry {
                        generation: self.sequence,
                        status: "Checking".into(),
                        candidate: None,
                    },
                );
                Some(CheckTicket {
                    id: id.clone(),
                    generation: self.sequence,
                })
            })
            .collect()
    }
    pub fn finish(
        &mut self,
        ticket: &CheckTicket,
        result: Result<Option<Candidate>, &'static str>,
    ) {
        let Some(entry) = self
            .entries
            .get_mut(&ticket.id)
            .filter(|e| e.generation == ticket.generation)
        else {
            return;
        };
        match result {
            Ok(Some(candidate)) => {
                entry.status = "Ready".into();
                entry.candidate = Some(candidate);
            }
            Ok(None) => {
                entry.status = "NotOfficial".into();
            }
            Err("InvalidCatalog") => {
                entry.status = "SourceInvalid".into();
            }
            Err(_) => {
                entry.status = "SourceUnavailable".into();
            }
        }
    }
    pub fn set_enabled(&mut self, id: &str, enabled: bool) {
        self.sequence += 1;
        self.entries.insert(
            id.to_string(),
            Entry {
                generation: self.sequence,
                status: if enabled {
                    "NotChecked"
                } else {
                    "CheckDisabled"
                }
                .into(),
                candidate: None,
            },
        );
    }
    pub fn retain(&mut self, ids: &[String]) {
        self.entries.retain(|id, _| ids.contains(id));
    }
    pub fn download_candidate(&self, module: &ModuleSummary) -> Result<Candidate, &'static str> {
        let entry = self
            .entries
            .get(&module.id)
            .filter(|e| e.status == "Ready")
            .ok_or("SelectionUnavailable")?;
        let candidate = entry
            .candidate
            .as_ref()
            .filter(|c| c.item.can_download)
            .ok_or("SelectionUnavailable")?;
        let incoming = candidate
            .item
            .version
            .as_deref()
            .ok_or("SelectionUnavailable")?;
        if module.source != ModuleSource::User
            || !module.valid
            || parse_version(incoming).map_err(|_| "InvalidCatalog")?
                <= parse_version(&module.version).map_err(|_| "InvalidLocalVersion")?
        {
            return Err("SelectionUnavailable");
        }
        Ok(candidate.clone())
    }
    pub fn view(
        &self,
        module: &ModuleSummary,
        enabled: bool,
        locale: &str,
        download: &DownloadSnapshot,
    ) -> UpdateView {
        let entry = self.entries.get(&module.id);
        let mut status = entry.map(|e| e.status.as_str()).unwrap_or(if enabled {
            "NotChecked"
        } else {
            "CheckDisabled"
        });
        let candidate = entry.and_then(|e| e.candidate.as_ref());
        if status == "Ready" {
            status = match candidate
                .and_then(|c| c.item.version.as_deref())
                .map(parse_version)
            {
                None => "NoPublishedRelease",
                Some(Err(_)) => "SourceInvalid",
                Some(Ok(incoming)) => match parse_version(&module.version) {
                    Err(_) => "InvalidLocalVersion",
                    Ok(local) if local > incoming => "LocalVersionNewer",
                    Ok(local) if local == incoming => "UpToDate",
                    _ => match candidate
                        .map(|c| c.item.unavailable_reason.as_str())
                        .unwrap_or("")
                    {
                        "IncompatibleApi" => "ModuleApiIncompatible",
                        "IncompatibleHost" => "HostVersionIncompatible",
                        "IncompatiblePlatform" => "PlatformIncompatible",
                        _ => "UpdateAvailable",
                    },
                },
            };
        }
        let related = download.module_id == module.id
            && !(download.status == "Completed"
                && matches!(
                    status,
                    "UpdateAvailable"
                        | "Checking"
                        | "SourceInvalid"
                        | "SourceUnavailable"
                        | "NotOfficial"
                ));
        let active = related
            && matches!(
                download.status.as_str(),
                "Downloading" | "Verifying" | "Installing"
            );
        let download_status = if !related {
            "NotDownloaded"
        } else {
            match download.status.as_str() {
                "Downloading" => "Downloading",
                "Verifying" => "Verifying",
                "Installing" => "Installing",
                "Completed" => "Installed",
                "InstallFailed" => "InstallFailed",
                "Failed" => match download.error.as_str() {
                    "HashMismatch" => "HashMismatch",
                    "SizeMismatch" => "SizeMismatch",
                    "InvalidPackage" | "InvalidCatalog" => "SourceInvalid",
                    "NetworkUnavailable" => "SourceUnavailable",
                    "StorageUnavailable" => "StorageUnavailable",
                    _ => "Failed",
                },
                _ => "NotDownloaded",
            }
        };
        UpdateView {
            module_id: module.id.clone(),
            update_status: status.into(),
            target_version: candidate.and_then(|c| c.item.version.clone()),
            release_notes: candidate.and_then(|c| c.release_notes(locale)),
            is_update_check_enabled: enabled,
            can_check_for_update: status != "Checking" && !active,
            is_update_check_busy: status == "Checking",
            can_download_update: status == "UpdateAvailable"
                && self.download_candidate(module).is_ok()
                && !active,
            download_status: download_status.into(),
            is_download_active: active,
            download_bytes_received: if related { download.bytes_received } else { 0 },
            download_expected_bytes: if related {
                download.expected_bytes
            } else {
                candidate.map(|c| c.item.size).unwrap_or(0)
            },
            can_install_verified_update: false, // user-triggered download owns the entire verified install operation
        }
    }
}

/// Monotonic timer, independent of wall-clock changes. Startup and recurring
/// checks are separate preferences; interval zero disables only recurring checks.
#[derive(Default)]
pub struct Schedule {
    started: bool,
    last_attempt: Duration,
    startup_pending: bool,
}
impl Schedule {
    pub fn due(&mut self, elapsed: Duration, on_startup: bool, interval_minutes: u32) -> bool {
        if !self.started {
            self.started = true;
            self.startup_pending = on_startup;
            self.last_attempt = elapsed;
        }
        self.startup_pending
            || interval_minutes > 0
                && elapsed.saturating_sub(self.last_attempt)
                    >= Duration::from_secs(u64::from(interval_minutes) * 60)
    }
    pub fn attempted(&mut self, elapsed: Duration) {
        self.startup_pending = false;
        self.last_attempt = elapsed;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn automatic_checks_skip_disabled_modules_but_manual_checks_can_override() {
        let installed = vec!["qing.one".into(), "qing.two".into()];
        let disabled = vec!["qing.two".into()];
        assert_eq!(selected_ids(&installed, &disabled, None), vec!["qing.one"]);
        assert_eq!(
            selected_ids(&installed, &disabled, Some("qing.two")),
            vec!["qing.two"]
        );
        assert!(selected_ids(&installed, &disabled, Some("qing.missing")).is_empty());
        assert!(selected_ids(&installed, &installed, None).is_empty());
    }
    #[test]
    fn startup_and_periodic_schedule_are_independent_and_busy_attempts_retry() {
        let mut schedule = Schedule::default();
        assert!(!schedule.due(Duration::ZERO, false, 5));
        assert!(!schedule.due(Duration::from_secs(299), false, 5));
        assert!(schedule.due(Duration::from_secs(300), false, 5));
        assert!(schedule.due(Duration::from_secs(305), false, 5)); // busy did not consume the check
        schedule.attempted(Duration::from_secs(305));
        assert!(!schedule.due(Duration::from_secs(604), false, 5));
        assert!(!schedule.due(Duration::from_secs(99999), false, 0));
        let mut startup = Schedule::default();
        assert!(startup.due(Duration::ZERO, true, 0));
        startup.attempted(Duration::ZERO);
        assert!(!startup.due(Duration::from_secs(99999), true, 0));
    }
    #[test]
    fn disabled_module_discards_an_inflight_check_and_old_generation_cannot_win() {
        let mut state = UpdateState::default();
        let tickets = state.begin(&["qing.test".into()]);
        assert!(state.begin(&["qing.test".into()]).is_empty());
        state.set_enabled("qing.test", false);
        state.finish(&tickets[0], Ok(None));
        assert_eq!(state.entries["qing.test"].status, "CheckDisabled");
        state.set_enabled("qing.test", true);
        let next = state.begin(&["qing.test".into()]);
        state.finish(&tickets[0], Err("NetworkUnavailable"));
        assert_eq!(state.entries["qing.test"].status, "Checking");
        state.finish(&next[0], Ok(None));
        assert_eq!(state.entries["qing.test"].status, "NotOfficial");
    }
    #[test]
    fn failed_repository_request_is_not_reported_as_missing_official_identity() {
        let mut state = UpdateState::default();
        let tickets = state.begin(&["qing.test".into()]);
        state.finish(&tickets[0], Err("NetworkUnavailable"));
        assert_eq!(state.entries["qing.test"].status, "SourceUnavailable");
    }
}
