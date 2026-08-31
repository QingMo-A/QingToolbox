use serde::Serialize;

/// The host-update contract is intentionally backend-owned. Until the native
/// updater is enabled, this snapshot keeps the Vue panel truthful while still
/// exposing the real host version from Cargo.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HostUpdateSnapshot {
    pub generated_at: String,
    pub state: String,
    pub current_version: String,
    pub latest_version: String,
    pub published_at: String,
    pub last_checked: String,
    pub summary: String,
    pub show_banner: bool,
    pub download_state: String,
    pub bytes_received: u64,
    pub expected_bytes: u64,
    pub download_error: String,
    pub can_check: bool,
    pub can_download: bool,
    pub can_cancel_download: bool,
    pub can_install: bool,
    pub installation_supported: bool,
    pub install_message: String,
}

impl HostUpdateSnapshot {
    pub fn unavailable(
        current_version: impl Into<String>,
        generated_at: impl Into<String>,
    ) -> Self {
        Self {
            generated_at: generated_at.into(),
            state: "DisabledByEnvironment".to_string(),
            current_version: current_version.into(),
            latest_version: String::new(),
            published_at: String::new(),
            last_checked: String::new(),
            summary: "Tauri updater integration is not enabled yet.".to_string(),
            show_banner: false,
            download_state: "DisabledByEnvironment".to_string(),
            bytes_received: 0,
            expected_bytes: 0,
            download_error: String::new(),
            can_check: false,
            can_download: false,
            can_cancel_download: false,
            can_install: false,
            installation_supported: false,
            install_message: String::new(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::HostUpdateSnapshot;

    #[test]
    fn unavailable_snapshot_is_bounded_and_uses_real_version() {
        let snapshot = HostUpdateSnapshot::unavailable("0.1.0", "2026-08-30T00:00:00Z");
        assert_eq!(snapshot.current_version, "0.1.0");
        assert_eq!(snapshot.state, "DisabledByEnvironment");
        assert!(!snapshot.can_check);
        assert!(!snapshot.show_banner);
        assert!(snapshot.latest_version.is_empty());
    }
}
