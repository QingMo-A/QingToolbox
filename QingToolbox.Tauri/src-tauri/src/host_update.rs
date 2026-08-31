use std::cmp::Ordering;

use serde::{Deserialize, Serialize};
use serde_json::Value;

const OFFICIAL_RELEASES_API: &str = "https://api.github.com/repos/QingMo-A/QingToolbox/releases";
const MAX_RELEASE_RESPONSE_BYTES: usize = 2 * 1024 * 1024;
const MAX_RELEASES: usize = 64;
const MAX_ASSETS_PER_RELEASE: usize = 64;
const MAX_TEXT_CHARS: usize = 320;
const MAX_ASSET_NAME_CHARS: usize = 256;
const MAX_ASSET_URL_CHARS: usize = 2048;
pub const MAX_INSTALLER_BYTES: u64 = 512 * 1024 * 1024;
pub const MAX_CHECKSUM_BYTES: u64 = 4 * 1024;

/// The host-update contract is intentionally backend-owned. The Vue shell
/// receives this projection and never receives a release download URL or
/// installer path.
#[derive(Debug, Clone, Serialize, Deserialize)]
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

#[derive(Debug, Clone)]
pub struct HostUpdateState {
    snapshot: HostUpdateSnapshot,
    generation: u64,
    release: Option<HostReleaseInfo>,
}

impl HostUpdateState {
    pub fn new(current_version: impl Into<String>, generated_at: impl Into<String>) -> Self {
        Self {
            snapshot: HostUpdateSnapshot::unavailable(current_version, generated_at),
            generation: 0,
            release: None,
        }
    }

    pub fn snapshot(&self) -> HostUpdateSnapshot {
        self.snapshot.clone()
    }

    pub fn begin_check(&mut self, generated_at: impl Into<String>) -> u64 {
        self.generation = self.generation.saturating_add(1);
        self.snapshot.state = "Checking".to_string();
        self.snapshot.generated_at = generated_at.into();
        self.snapshot.summary = "正在检查官方更新…".to_string();
        self.snapshot.latest_version.clear();
        self.snapshot.published_at.clear();
        self.snapshot.download_error.clear();
        self.snapshot.can_check = false;
        self.snapshot.show_banner = false;
        self.snapshot.download_state = "NotDownloaded".to_string();
        self.snapshot.bytes_received = 0;
        self.snapshot.expected_bytes = 0;
        self.snapshot.can_download = false;
        self.snapshot.can_cancel_download = false;
        self.snapshot.can_install = false;
        self.snapshot.installation_supported = false;
        self.snapshot.install_message.clear();
        self.release = None;
        self.generation
    }

    pub fn finish_check(
        &mut self,
        generation: u64,
        current_version: &str,
        checked_at: impl Into<String>,
        result: Result<Option<HostReleaseInfo>, String>,
    ) -> HostUpdateSnapshot {
        if generation != self.generation {
            return self.snapshot();
        }
        let checked_at = checked_at.into();
        self.snapshot.generated_at = checked_at.clone();
        self.snapshot.last_checked = checked_at;
        self.snapshot.current_version = current_version.to_string();
        self.snapshot.can_check = true;
        self.snapshot.download_state = "DisabledByEnvironment".to_string();
        self.snapshot.can_download = false;
        self.snapshot.can_cancel_download = false;
        self.snapshot.can_install = false;
        self.snapshot.installation_supported = false;
        self.snapshot.show_banner = false;
        self.snapshot.bytes_received = 0;
        self.snapshot.expected_bytes = 0;
        self.snapshot.download_error.clear();
        self.snapshot.install_message =
            "Tauri updater download and installation are not enabled yet.".to_string();

        match result {
            Ok(Some(release)) => {
                self.snapshot.state = "UpdateAvailable".to_string();
                self.snapshot.latest_version = release.version.clone();
                self.snapshot.published_at = release.published_at.clone();
                self.snapshot.summary = if release.summary.is_empty() {
                    "发现新的 QingToolbox 版本。".to_string()
                } else {
                    release.summary.clone()
                };
                self.release = Some(release);
            }
            Ok(None) => {
                self.snapshot.state = "UpToDate".to_string();
                self.snapshot.latest_version.clear();
                self.snapshot.published_at.clear();
                self.snapshot.summary = "当前已是最新版本。".to_string();
                self.release = None;
            }
            Err(_) => {
                self.snapshot.state = "Failed".to_string();
                self.snapshot.latest_version.clear();
                self.snapshot.published_at.clear();
                self.snapshot.summary = "无法检查官方更新。".to_string();
                self.release = None;
            }
        }
        self.snapshot()
    }

    #[cfg(test)]
    fn release(&self) -> Option<&HostReleaseInfo> {
        self.release.as_ref()
    }
}

#[derive(Debug, Clone)]
pub struct HostUpdateCheck {
    pub checked_at: String,
    pub release: Option<HostReleaseInfo>,
}

#[allow(dead_code)]
#[derive(Debug, Clone)]
pub struct HostReleaseInfo {
    pub version: String,
    pub published_at: String,
    pub summary: String,
    installer: HostReleaseAsset,
    checksum: HostReleaseAsset,
}

#[allow(dead_code)]
#[derive(Debug, Clone)]
struct HostReleaseAsset {
    id: u64,
    name: String,
    url: String,
    size: u64,
}

#[derive(Debug, Clone)]
struct ReleaseRecord {
    tag_name: String,
    draft: bool,
    published_at: String,
    body: Option<String>,
    assets: Vec<HostReleaseAsset>,
}

#[derive(Debug, Clone, Eq, PartialEq)]
struct Version {
    major: u64,
    minor: u64,
    patch: u64,
    prerelease: Option<Vec<VersionIdentifier>>,
    build: Option<String>,
}

#[derive(Debug, Clone, Eq, PartialEq)]
enum VersionIdentifier {
    Numeric(u64),
    Text(String),
}

impl Ord for Version {
    fn cmp(&self, other: &Self) -> Ordering {
        self.major
            .cmp(&other.major)
            .then(self.minor.cmp(&other.minor))
            .then(self.patch.cmp(&other.patch))
            .then_with(|| match (&self.prerelease, &other.prerelease) {
                (None, None) => Ordering::Equal,
                (None, Some(_)) => Ordering::Greater,
                (Some(_), None) => Ordering::Less,
                (Some(left), Some(right)) => {
                    for (left, right) in left.iter().zip(right) {
                        let ordering = match (left, right) {
                            (VersionIdentifier::Numeric(a), VersionIdentifier::Numeric(b)) => {
                                a.cmp(b)
                            }
                            (VersionIdentifier::Numeric(_), VersionIdentifier::Text(_)) => {
                                Ordering::Less
                            }
                            (VersionIdentifier::Text(_), VersionIdentifier::Numeric(_)) => {
                                Ordering::Greater
                            }
                            (VersionIdentifier::Text(a), VersionIdentifier::Text(b)) => a.cmp(b),
                        };
                        if ordering != Ordering::Equal {
                            return ordering;
                        }
                    }
                    left.len().cmp(&right.len())
                }
            })
    }
}

impl PartialOrd for Version {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl std::fmt::Display for Version {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(formatter, "{}.{}.{}", self.major, self.minor, self.patch)?;
        if let Some(parts) = &self.prerelease {
            write!(
                formatter,
                "-{}",
                parts
                    .iter()
                    .map(|part| match part {
                        VersionIdentifier::Numeric(value) => value.to_string(),
                        VersionIdentifier::Text(value) => value.clone(),
                    })
                    .collect::<Vec<_>>()
                    .join(".")
            )?;
        }
        if let Some(build) = &self.build {
            write!(formatter, "+{build}")?;
        }
        Ok(())
    }
}

pub fn check_official_release(
    current_version: &str,
    checked_at: String,
) -> Result<HostUpdateCheck, ()> {
    let current = parse_version(current_version)?;
    let json = fetch_official_releases()?;
    let records = parse_releases(&json)?;
    Ok(HostUpdateCheck {
        checked_at,
        release: select_best_release(&records, &current),
    })
}

fn parse_version(raw: &str) -> Result<Version, ()> {
    if raw.is_empty() || raw.chars().count() > 128 || raw.chars().any(char::is_whitespace) {
        return Err(());
    }
    let (without_build, build) = match raw.split_once('+') {
        Some((core, build)) if !build.is_empty() && valid_identifiers(build, false) => {
            (core, Some(build.to_string()))
        }
        Some(_) => return Err(()),
        None => (raw, None),
    };
    let (core, prerelease) = match without_build.split_once('-') {
        Some((core, prerelease))
            if !prerelease.is_empty() && valid_identifiers(prerelease, true) =>
        {
            (core, Some(parse_identifiers(prerelease)?))
        }
        Some(_) => return Err(()),
        None => (without_build, None),
    };
    let mut parts = core.split('.');
    let major = parse_core_number(parts.next().ok_or(())?)?;
    let minor = parse_core_number(parts.next().ok_or(())?)?;
    let patch = parse_core_number(parts.next().ok_or(())?)?;
    if parts.next().is_some() {
        return Err(());
    }
    Ok(Version {
        major,
        minor,
        patch,
        prerelease,
        build,
    })
}

fn parse_core_number(raw: &str) -> Result<u64, ()> {
    if raw.is_empty() || (raw.len() > 1 && raw.starts_with('0')) {
        return Err(());
    }
    raw.parse().map_err(|_| ())
}

fn valid_identifiers(raw: &str, reject_numeric_leading_zero: bool) -> bool {
    raw.split('.').all(|part| {
        !part.is_empty()
            && part
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-')
            && (!reject_numeric_leading_zero
                || part.len() == 1
                || !part.bytes().all(|byte| byte.is_ascii_digit())
                || !part.starts_with('0'))
    })
}

fn parse_identifiers(raw: &str) -> Result<Vec<VersionIdentifier>, ()> {
    raw.split('.')
        .map(|part| {
            if part.bytes().all(|byte| byte.is_ascii_digit()) {
                Ok(VersionIdentifier::Numeric(part.parse().map_err(|_| ())?))
            } else {
                Ok(VersionIdentifier::Text(part.to_string()))
            }
        })
        .collect()
}

fn release_channel(version: &Version) -> u8 {
    let Some(VersionIdentifier::Text(label)) =
        version.prerelease.as_ref().and_then(|parts| parts.first())
    else {
        return 3;
    };
    if label.starts_with("rc") {
        2
    } else if label.starts_with("beta") {
        1
    } else {
        0
    }
}

fn parse_releases(json: &str) -> Result<Vec<ReleaseRecord>, ()> {
    if json.len() > MAX_RELEASE_RESPONSE_BYTES {
        return Err(());
    }
    let root: Value = serde_json::from_str(json).map_err(|_| ())?;
    let releases = root.as_array().ok_or(())?;
    if releases.len() > MAX_RELEASES {
        return Err(());
    }
    let mut result = Vec::with_capacity(releases.len());
    for release in releases {
        let Some(tag_name) = release.get("tag_name").and_then(Value::as_str) else {
            continue;
        };
        if tag_name.chars().count() > 128 {
            continue;
        }
        let Some(assets_value) = release.get("assets").and_then(Value::as_array) else {
            continue;
        };
        if assets_value.len() > MAX_ASSETS_PER_RELEASE {
            continue;
        }
        let assets = assets_value
            .iter()
            .filter_map(parse_asset)
            .collect::<Vec<_>>();
        let published_at = release
            .get("published_at")
            .and_then(Value::as_str)
            .unwrap_or_default();
        if published_at.chars().count() > 128 {
            continue;
        }
        result.push(ReleaseRecord {
            tag_name: tag_name.to_string(),
            draft: release
                .get("draft")
                .and_then(Value::as_bool)
                .unwrap_or(false),
            published_at: published_at.to_string(),
            body: release
                .get("body")
                .and_then(Value::as_str)
                .map(str::to_string),
            assets,
        });
    }
    Ok(result)
}

fn parse_asset(value: &Value) -> Option<HostReleaseAsset> {
    let name = value.get("name").and_then(Value::as_str)?;
    let url = value.get("browser_download_url").and_then(Value::as_str)?;
    let id = value.get("id").and_then(Value::as_u64)?;
    let size = value.get("size").and_then(Value::as_u64)?;
    if id == 0
        || size == 0
        || name.is_empty()
        || name.chars().count() > MAX_ASSET_NAME_CHARS
        || url.chars().count() > MAX_ASSET_URL_CHARS
        || !is_official_asset_url(url)
    {
        return None;
    }
    Some(HostReleaseAsset {
        id,
        name: name.to_string(),
        url: url.to_string(),
        size,
    })
}

fn is_official_asset_url(url: &str) -> bool {
    url.starts_with("https://github.com/QingMo-A/QingToolbox/releases/download/")
        && !url.contains(['?', '#'])
}

fn select_best_release(records: &[ReleaseRecord], current: &Version) -> Option<HostReleaseInfo> {
    let current_channel = release_channel(current);
    records
        .iter()
        .filter_map(|record| {
            if record.draft {
                return None;
            }
            let tag = record
                .tag_name
                .strip_prefix('v')
                .or_else(|| record.tag_name.strip_prefix('V'))
                .unwrap_or(&record.tag_name);
            let version = parse_version(tag).ok()?;
            if version <= *current || release_channel(&version) < current_channel {
                return None;
            }
            let version_text = version.to_string();
            let installer_name = format!("QingToolbox-{version_text}-win-x64-setup.exe");
            let checksum_name = format!("{installer_name}.sha256");
            let installers = record
                .assets
                .iter()
                .filter(|asset| asset.name == installer_name && asset.size <= MAX_INSTALLER_BYTES)
                .collect::<Vec<_>>();
            let checksums = record
                .assets
                .iter()
                .filter(|asset| asset.name == checksum_name && asset.size <= MAX_CHECKSUM_BYTES)
                .collect::<Vec<_>>();
            if installers.len() != 1 || checksums.len() != 1 {
                return None;
            }
            Some((
                version,
                HostReleaseInfo {
                    version: version_text,
                    published_at: record.published_at.clone(),
                    summary: summarize(record.body.as_deref()),
                    installer: installers[0].clone(),
                    checksum: checksums[0].clone(),
                },
            ))
        })
        .max_by(|left, right| left.0.cmp(&right.0))
        .map(|(_, release)| release)
}

fn summarize(body: Option<&str>) -> String {
    let Some(body) = body else {
        return String::new();
    };
    let plain = body.replace(['\r', '\n'], " ").replace(['#', '*', '`'], "");
    let plain = plain.split_whitespace().collect::<Vec<_>>().join(" ");
    let mut result = plain.chars().take(MAX_TEXT_CHARS).collect::<String>();
    if plain.chars().count() > MAX_TEXT_CHARS {
        result.truncate(MAX_TEXT_CHARS.saturating_sub(1));
        result.push('…');
    }
    result
}

#[cfg(windows)]
fn fetch_official_releases() -> Result<String, ()> {
    use std::{ffi::c_void, mem, ptr};
    use windows_sys::Win32::Networking::WinHttp::{
        WinHttpCloseHandle, WinHttpConnect, WinHttpOpen, WinHttpOpenRequest,
        WinHttpQueryDataAvailable, WinHttpQueryHeaders, WinHttpReadData, WinHttpReceiveResponse,
        WinHttpSendRequest, WinHttpSetTimeouts, HTTP_STATUS_OK, INTERNET_DEFAULT_HTTPS_PORT,
        WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_FLAG_SECURE, WINHTTP_QUERY_FLAG_NUMBER,
        WINHTTP_QUERY_STATUS_CODE,
    };

    struct Handle(*mut c_void);
    impl Drop for Handle {
        fn drop(&mut self) {
            if !self.0.is_null() {
                unsafe { WinHttpCloseHandle(self.0) };
            }
        }
    }
    fn wide(value: &str) -> Vec<u16> {
        value.encode_utf16().chain(std::iter::once(0)).collect()
    }
    unsafe {
        let agent = wide("QingToolbox");
        let session = Handle(WinHttpOpen(
            agent.as_ptr(),
            WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
            ptr::null(),
            ptr::null(),
            0,
        ));
        if session.0.is_null() {
            return Err(());
        }
        if WinHttpSetTimeouts(session.0, 15_000, 15_000, 15_000, 15_000) == 0 {
            return Err(());
        }
        let host = wide("api.github.com");
        let connection = Handle(WinHttpConnect(
            session.0,
            host.as_ptr(),
            INTERNET_DEFAULT_HTTPS_PORT,
            0,
        ));
        if connection.0.is_null() {
            return Err(());
        }
        let verb = wide("GET");
        let api_path = OFFICIAL_RELEASES_API
            .strip_prefix("https://api.github.com")
            .ok_or(())?;
        let path = wide(api_path);
        let request = Handle(WinHttpOpenRequest(
            connection.0,
            verb.as_ptr(),
            path.as_ptr(),
            ptr::null(),
            ptr::null(),
            ptr::null(),
            WINHTTP_FLAG_SECURE,
        ));
        if request.0.is_null() {
            return Err(());
        }
        let headers =
            wide("Accept: application/vnd.github+json\r\nX-GitHub-Api-Version: 2022-11-28\r\n");
        if WinHttpSendRequest(
            request.0,
            headers.as_ptr(),
            (headers.len() - 1) as u32,
            ptr::null(),
            0,
            0,
            0,
        ) == 0
            || WinHttpReceiveResponse(request.0, ptr::null_mut()) == 0
        {
            return Err(());
        }
        let mut status = 0u32;
        let mut status_len = mem::size_of::<u32>() as u32;
        if WinHttpQueryHeaders(
            request.0,
            WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
            ptr::null(),
            &mut status as *mut u32 as *mut c_void,
            &mut status_len,
            ptr::null_mut(),
        ) == 0
            || status != HTTP_STATUS_OK
        {
            return Err(());
        }
        let mut body = Vec::new();
        loop {
            let mut available = 0u32;
            if WinHttpQueryDataAvailable(request.0, &mut available) == 0 {
                return Err(());
            }
            if available == 0 {
                break;
            }
            if body.len().saturating_add(available as usize) > MAX_RELEASE_RESPONSE_BYTES {
                return Err(());
            }
            let start = body.len();
            body.resize(start + available as usize, 0);
            let mut read = 0u32;
            if WinHttpReadData(
                request.0,
                body[start..].as_mut_ptr() as *mut c_void,
                available,
                &mut read,
            ) == 0
            {
                return Err(());
            }
            if read == 0 {
                return Err(());
            }
            body.truncate(start + read as usize);
        }
        String::from_utf8(body).map_err(|_| ())
    }
}

#[cfg(not(windows))]
fn fetch_official_releases() -> Result<String, ()> {
    Err(())
}

#[cfg(test)]
mod tests {
    use super::{
        parse_releases, parse_version, select_best_release, HostUpdateSnapshot, HostUpdateState,
    };

    const RELEASES: &str = r##"[
      {"tag_name":"v0.2.10-alpha","draft":false,"published_at":"2026-08-30T00:00:00Z","body":"New *features*","assets":[
        {"id":10,"name":"QingToolbox-0.2.10-alpha-win-x64-setup.exe","browser_download_url":"https://github.com/QingMo-A/QingToolbox/releases/download/v0.2.10-alpha/QingToolbox-0.2.10-alpha-win-x64-setup.exe","size":1000},
        {"id":11,"name":"QingToolbox-0.2.10-alpha-win-x64-setup.exe.sha256","browser_download_url":"https://github.com/QingMo-A/QingToolbox/releases/download/v0.2.10-alpha/QingToolbox-0.2.10-alpha-win-x64-setup.exe.sha256","size":96}]},
      {"tag_name":"v0.3.0","draft":false,"published_at":"2026-08-30T00:00:00Z","body":"stable","assets":[]},
      {"tag_name":"v0.2.11-beta","draft":false,"published_at":"2026-08-30T00:00:00Z","body":"beta","assets":[]}
    ]"##;

    #[test]
    fn semver_parsing_and_channel_order_are_strict() {
        assert!(parse_version("0.2.10-alpha.1").is_ok());
        assert!(parse_version("0.2.01").is_err());
        assert!(parse_version("0.2.10-alpha.01").is_err());
        assert!(parse_version("0.2").is_err());
        let alpha = parse_version("0.2.10-alpha").expect("alpha");
        let stable = parse_version("0.2.10").expect("stable");
        assert!(stable > alpha);
    }

    #[test]
    fn release_selection_requires_exact_verified_assets_and_prefers_highest_allowed_version() {
        let records = parse_releases(RELEASES).expect("release payload");
        let current = parse_version("0.2.9-alpha").expect("current");
        let selected = select_best_release(&records, &current).expect("candidate");
        assert_eq!(selected.version, "0.2.10-alpha");
        assert!(selected.summary.contains("New features"));
    }

    #[test]
    fn host_update_state_ignores_stale_results() {
        let mut state = HostUpdateState::new("0.2.9-alpha", "initial");
        let first = state.begin_check("first");
        let second = state.begin_check("second");
        let stale = state.finish_check(first, "0.2.9-alpha", "stale", Ok(None));
        assert_eq!(stale.state, "Checking");
        let current = state.finish_check(second, "0.2.9-alpha", "done", Ok(None));
        assert_eq!(current.state, "UpToDate");
        assert_eq!(current.last_checked, "done");
        assert!(state.release().is_none());
    }

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
