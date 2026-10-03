//! Official catalog, verified downloads and backend-owned installation planning.
use crate::{
    host_update::{fetch_repository, parse_version, DownloadFailure},
    importer::preview_qmod,
    module_api,
};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::BTreeMap,
    fs,
    io::{Seek, SeekFrom, Write},
    path::{Path, PathBuf},
};

const BASE: &str = "https://raw.githubusercontent.com/QingMo-A/QingToolbox/modules/modules/";
const PROFILE: &str = "tauri-process-v1";
const MAX_JSON: usize = 256 * 1024;
const MAX_PACKAGE: u64 = 256 * 1024 * 1024;
type Text = BTreeMap<String, String>;
type Result<T> = std::result::Result<T, &'static str>;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Catalog {
    schema_version: u32,
    source_id: String,
    module_profile: String,
    modules: BTreeMap<String, Entry>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Entry {
    name: Text,
    description: Text,
    icon: String,
    module_manifest: String,
    update_manifest: String,
    visibility: String,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Updates {
    schema_version: u32,
    module_id: String,
    publisher: String,
    releases: Vec<Release>,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Release {
    version: String,
    channel: String,
    module_profile: String,
    api_version: u32,
    platform: String,
    architecture: String,
    minimum_host_version: String,
    maximum_host_version_exclusive: Option<String>,
    published_at: String,
    package: Package,
    release_notes: Text,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Package {
    file_name: String,
    url: String,
    size: u64,
    sha256: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CatalogItem {
    pub id: String,
    pub name: Text,
    pub description: Text,
    pub version: Option<String>,
    pub api_version: Option<u32>,
    pub size: u64,
    pub can_download: bool,
    pub unavailable_reason: String,
}
#[derive(Clone)]
pub struct Candidate {
    pub item: CatalogItem,
    release: Option<Release>,
}
impl Candidate {
    pub fn release_notes(&self, locale: &str) -> Option<String> {
        self.release
            .as_ref()
            .and_then(|r| {
                r.release_notes
                    .get(locale)
                    .or_else(|| r.release_notes.get("en-US"))
            })
            .cloned()
    }
    pub fn sha256(&self) -> Result<&str> {
        self.release
            .as_ref()
            .filter(|_| self.item.can_download)
            .map(|release| release.package.sha256.as_str())
            .ok_or("SelectionUnavailable")
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Installation {
    Import,
    Update,
    AlreadyInstalled,
}

/// Recheck the saved package before touching any installed module. Never
/// downgrade, overwrite an unknown identity, or bypass API compatibility.
pub fn installation(candidate: &Candidate, source: &Path, root: &Path) -> Result<Installation> {
    let release = candidate
        .release
        .as_ref()
        .filter(|_| candidate.item.can_download)
        .ok_or("SelectionUnavailable")?;
    verify_package(source, &candidate.item.id, release)?;
    if release.api_version != module_api::API_VERSION {
        return Err("IncompatibleApi");
    }
    match fs::symlink_metadata(root) {
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return Ok(Installation::Import)
        }
        Ok(meta) if meta.is_dir() && !crate::importer::is_reparse_point(&meta) => {}
        _ => return Err("InstallFailed"),
    }
    let root = root.canonicalize().map_err(|_| "InstallFailed")?;
    let directory = root.join(&candidate.item.id);
    match fs::symlink_metadata(&directory) {
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return Ok(Installation::Import)
        }
        Ok(meta) if meta.is_dir() && !crate::importer::is_reparse_point(&meta) => {}
        _ => return Err("InstallFailed"),
    }
    let directory = directory.canonicalize().map_err(|_| "InstallFailed")?;
    if directory.parent() != Some(root.as_path()) {
        return Err("InstallFailed");
    }
    let manifest_path = directory.join("module.json");
    let metadata = fs::symlink_metadata(&manifest_path).map_err(|_| "InstallFailed")?;
    if !metadata.is_file()
        || crate::importer::is_reparse_point(&metadata)
        || metadata.len() > MAX_JSON as u64
    {
        return Err("InstallFailed");
    }
    let manifest: serde_json::Value =
        serde_json::from_slice(&fs::read(manifest_path).map_err(|_| "InstallFailed")?)
            .map_err(|_| "InstallFailed")?;
    if manifest.get("id").and_then(|v| v.as_str()) != Some(candidate.item.id.as_str()) {
        return Err("InstallFailed");
    }
    let installed = manifest
        .get("version")
        .and_then(|v| v.as_str())
        .and_then(|v| parse_version(v).ok())
        .ok_or("InstallFailed")?;
    let incoming = parse_version(&release.version).map_err(|_| "InvalidCatalog")?;
    Ok(if installed >= incoming {
        Installation::AlreadyInstalled
    } else {
        Installation::Update
    })
}
#[derive(Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DownloadSnapshot {
    pub job_id: u64,
    pub module_id: String,
    pub name: String,
    pub status: String,
    pub bytes_received: u64,
    pub expected_bytes: u64,
    pub saved_path: String,
    pub error: String,
}
#[derive(Default)]
pub struct RepositoryState {
    candidates: BTreeMap<String, Candidate>,
    loading: bool,
    pub snapshot: DownloadSnapshot,
}
impl RepositoryState {
    /// Only Rust-validated update metadata may enter this path, never Vue data.
    pub fn begin_candidate_download(
        &mut self,
        candidate: Candidate,
        locale: &str,
    ) -> Result<(u64, Candidate)> {
        if self.loading || self.active() {
            return Err("Busy");
        }
        let id = candidate.item.id.clone();
        candidate.sha256()?;
        self.candidates.insert(id.clone(), candidate);
        self.begin_download(&id, locale)
    }
    pub fn begin_catalog(&mut self) -> Result<()> {
        if self.loading || self.active() {
            return Err("Busy");
        }
        self.loading = true;
        self.candidates.clear();
        Ok(())
    }
    pub fn finish_catalog(&mut self, result: Result<Vec<Candidate>>) -> Result<Vec<CatalogItem>> {
        self.loading = false;
        let candidates = result?;
        let items = candidates.iter().map(|c| c.item.clone()).collect();
        self.candidates = candidates
            .into_iter()
            .map(|c| (c.item.id.clone(), c))
            .collect();
        Ok(items)
    }
    pub fn active(&self) -> bool {
        matches!(
            self.snapshot.status.as_str(),
            "Downloading" | "Verifying" | "Installing"
        )
    }
    pub fn begin_download(&mut self, id: &str, locale: &str) -> Result<(u64, Candidate)> {
        if self.loading || self.active() {
            return Err("Busy");
        }
        let candidate = self
            .candidates
            .get(id)
            .filter(|c| c.item.can_download)
            .cloned()
            .ok_or("SelectionUnavailable")?;
        let job_id = self.snapshot.job_id + 1;
        self.snapshot = DownloadSnapshot {
            job_id,
            module_id: id.to_string(),
            name: candidate
                .item
                .name
                .get(locale)
                .or_else(|| candidate.item.name.get("en-US"))
                .cloned()
                .unwrap_or_else(|| id.to_string()),
            status: "Downloading".to_string(),
            expected_bytes: candidate.item.size,
            ..Default::default()
        };
        Ok((job_id, candidate))
    }
    pub fn progress(&mut self, job: u64, received: u64, verifying: bool) {
        if self.snapshot.job_id != job
            || !matches!(self.snapshot.status.as_str(), "Downloading" | "Verifying")
        {
            return;
        }
        self.snapshot.bytes_received = received.min(self.snapshot.expected_bytes);
        if verifying {
            self.snapshot.status = "Verifying".to_string();
        }
    }
    pub fn begin_installation(&mut self, job: u64, path: &Path) -> bool {
        if self.snapshot.job_id != job || self.snapshot.status != "Verifying" {
            return false;
        }
        self.snapshot.status = "Installing".to_string();
        self.snapshot.saved_path = path.to_string_lossy().into_owned();
        true
    }
    pub fn finish(&mut self, job: u64, outcome: Result<PathBuf>) -> bool {
        if self.snapshot.job_id != job || !self.active() {
            return false;
        }
        match outcome {
            Ok(path) => {
                if self.snapshot.status != "Installing" {
                    return false;
                }
                self.snapshot.status = "Completed".to_string();
                self.snapshot.saved_path = path.to_string_lossy().into_owned();
                true
            }
            Err(code) => {
                self.snapshot.status = if self.snapshot.status == "Installing" {
                    "InstallFailed"
                } else {
                    "Failed"
                }
                .to_string();
                self.snapshot.error = code.to_string();
                false
            }
        }
    }
}

fn text_valid(text: &Text, limit: usize) -> bool {
    ["en-US", "zh-CN"].iter().all(|key| {
        text.get(*key)
            .is_some_and(|s| !s.trim().is_empty() && s.len() <= limit && !s.contains('\0'))
    })
}
fn relative(path: &str) -> bool {
    path.len() <= 256
        && path.split('/').count() >= 2
        && path.split('/').all(|p| {
            !p.is_empty()
                && p != "."
                && p != ".."
                && p.bytes()
                    .all(|b| b.is_ascii_alphanumeric() || b"._-".contains(&b))
        })
}
fn identity(id: &str) -> bool {
    id.strip_prefix("qing.").is_some_and(|slug| {
        !slug.is_empty()
            && slug.len() <= 64
            && slug
                .bytes()
                .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-')
    })
}
fn parse_catalog(bytes: &[u8]) -> Result<Catalog> {
    if bytes.len() > MAX_JSON {
        return Err("InvalidCatalog");
    }
    let catalog: Catalog = serde_json::from_slice(bytes).map_err(|_| "InvalidCatalog")?;
    if catalog.schema_version != 2
        || catalog.source_id != "qingtoolbox-official-tauri"
        || catalog.module_profile != PROFILE
        || catalog.modules.len() > 128
    {
        return Err("InvalidCatalog");
    }
    for (id, entry) in &catalog.modules {
        let directory = entry
            .module_manifest
            .strip_suffix("/module.json")
            .ok_or("InvalidCatalog")?;
        if !identity(id)
            || !text_valid(&entry.name, 256)
            || !text_valid(&entry.description, 4096)
            || !relative(&entry.icon)
            || !relative(&entry.module_manifest)
            || !relative(&entry.update_manifest)
            || entry.icon != format!("{directory}/icon.svg")
            || entry.update_manifest != format!("{directory}/update.json")
            || !matches!(entry.visibility.as_str(), "public" | "development")
        {
            return Err("InvalidCatalog");
        }
    }
    Ok(catalog)
}
fn parse_updates(bytes: &[u8], id: &str, directory: &str) -> Result<Vec<Release>> {
    if bytes.len() > MAX_JSON || !identity(id) {
        return Err("InvalidCatalog");
    }
    let updates: Updates = serde_json::from_slice(bytes).map_err(|_| "InvalidCatalog")?;
    if updates.schema_version != 2
        || updates.module_id != id
        || updates.publisher != "QingMo-A"
        || updates.releases.len() > 64
    {
        return Err("InvalidCatalog");
    }
    let mut previous = None;
    for r in &updates.releases {
        let version = parse_version(&r.version).map_err(|_| "InvalidCatalog")?;
        let minimum = parse_version(&r.minimum_host_version).map_err(|_| "InvalidCatalog")?;
        if previous.as_ref().is_some_and(|p| p <= &version) {
            return Err("InvalidCatalog");
        }
        previous = Some(version);
        if let Some(max) = &r.maximum_host_version_exclusive {
            if parse_version(max).map_err(|_| "InvalidCatalog")? <= minimum {
                return Err("InvalidCatalog");
            }
        }
        let filename = format!("{id}-{}-tauri.qmod", r.version);
        let legacy_url = format!(
            "https://github.com/QingMo-A/QingToolbox/releases/download/modules-{}-v{}/{filename}",
            &id[5..],
            r.version
        );
        let pinned_url = r
            .package
            .url
            .strip_prefix("https://raw.githubusercontent.com/QingMo-A/QingToolbox/")
            .and_then(|path| path.split_once('/'))
            .is_some_and(|(commit, path)| {
                commit.len() == 40
                    && commit
                        .bytes()
                        .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
                    && path == format!("modules/{directory}/packages/{filename}")
            });
        if r.module_profile != PROFILE
            || r.api_version == 0
            || r.platform != "windows"
            || !matches!(r.architecture.as_str(), "x64" | "arm64")
            || !matches!(r.channel.as_str(), "stable" | "preview")
            || !text_valid(&r.release_notes, 16384)
            || r.published_at.len() != 20
            || !r.published_at.ends_with('Z')
            || !r.published_at.contains('T')
            || r.package.file_name != filename
            || (r.package.url != legacy_url && !pinned_url)
            || r.package.size == 0
            || r.package.size > MAX_PACKAGE
            || r.package.sha256.len() != 64
            || !r
                .package
                .sha256
                .bytes()
                .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
        {
            return Err("InvalidCatalog");
        }
    }
    Ok(updates.releases)
}
fn incompatibility(release: &Release, host: &str, architecture: &str) -> Result<&'static str> {
    if release.architecture != architecture {
        return Ok("IncompatiblePlatform");
    }
    if release.api_version != module_api::API_VERSION {
        return Ok("IncompatibleApi");
    }
    let current = parse_version(host).map_err(|_| "InvalidCatalog")?;
    if current < parse_version(&release.minimum_host_version).map_err(|_| "InvalidCatalog")?
        || release
            .maximum_host_version_exclusive
            .as_ref()
            .map(|v| parse_version(v).map(|v| current >= v))
            .transpose()
            .map_err(|_| "InvalidCatalog")?
            .unwrap_or(false)
    {
        return Ok("IncompatibleHost");
    }
    Ok("")
}
fn fetch_json(url: &str) -> Result<Vec<u8>> {
    let mut body = Vec::new();
    let raw = fetch_repository(url, MAX_JSON as u64, false, &mut |chunk, _| {
        body.extend_from_slice(chunk);
        Ok(())
    });
    // Some networks reset raw.githubusercontent.com while GitHub's API remains
    // reachable. Read the exact same branch/file via Contents API; no mirrors.
    if matches!(raw, Err(DownloadFailure::SourceUnavailable)) {
        let relative = url.strip_prefix(BASE).ok_or("InvalidCatalog")?;
        body.clear();
        let api = format!("https://api.github.com/repos/QingMo-A/QingToolbox/contents/modules/{relative}?ref=modules");
        fetch_repository(&api, MAX_JSON as u64, false, &mut |chunk, _| {
            body.extend_from_slice(chunk);
            Ok(())
        })
        .map_err(network_error)?;
    } else {
        raw.map_err(network_error)?;
    }
    Ok(body)
}
fn network_error(error: DownloadFailure) -> &'static str {
    match error {
        DownloadFailure::SizeMismatch => "SizeMismatch",
        DownloadFailure::StorageUnavailable => "StorageUnavailable",
        DownloadFailure::HashMismatch => "HashMismatch",
        DownloadFailure::UntrustedRedirect | DownloadFailure::SourceInvalid => "InvalidCatalog",
        _ => "NetworkUnavailable",
    }
}
pub fn fetch_catalog() -> Result<Vec<Candidate>> {
    load_catalog(
        &mut fetch_json,
        env!("CARGO_PKG_VERSION"),
        if cfg!(target_arch = "aarch64") {
            "arm64"
        } else {
            "x64"
        },
    )
}
fn load_catalog(
    fetch: &mut impl FnMut(&str) -> Result<Vec<u8>>,
    host: &str,
    architecture: &str,
) -> Result<Vec<Candidate>> {
    let catalog = parse_catalog(&fetch(&format!("{BASE}index.json"))?)?;
    let mut candidates = Vec::new();
    for (id, entry) in catalog.modules {
        if entry.visibility != "public" {
            continue;
        }
        let directory = entry
            .module_manifest
            .strip_suffix("/module.json")
            .ok_or("InvalidCatalog")?;
        let releases = fetch(&format!("{BASE}{}", entry.update_manifest))
            .and_then(|bytes| parse_updates(&bytes, &id, directory));
        // The root index remains authoritative and fail-closed. A bad child
        // manifest, however, disables only that module; other verified modules
        // must remain browsable and downloadable.
        candidates.push(match releases {
            Ok(releases) => make_candidate(id, entry, releases, host, architecture)?,
            Err(error) => Candidate {
                item: CatalogItem {
                    id,
                    name: entry.name,
                    description: entry.description,
                    version: None,
                    api_version: None,
                    size: 0,
                    can_download: false,
                    unavailable_reason: error.to_string(),
                },
                release: None,
            },
        });
    }
    Ok(candidates)
}

fn make_candidate(
    id: String,
    entry: Entry,
    releases: Vec<Release>,
    host: &str,
    architecture: &str,
) -> Result<Candidate> {
    let selected = releases
        .iter()
        .find(|r| incompatibility(r, host, architecture) == Ok(""))
        .or_else(|| releases.first())
        .cloned();
    let reason = selected
        .as_ref()
        .map(|r| incompatibility(r, host, architecture))
        .transpose()?
        .unwrap_or("NoRelease");
    Ok(Candidate {
        item: CatalogItem {
            id,
            name: entry.name,
            description: entry.description,
            version: selected.as_ref().map(|r| r.version.clone()),
            api_version: selected.as_ref().map(|r| r.api_version),
            size: selected.as_ref().map(|r| r.package.size).unwrap_or(0),
            can_download: reason.is_empty(),
            unavailable_reason: reason.to_string(),
        },
        release: selected,
    })
}

/// Fetch only installed/enabled identities, using the authoritative index's
/// directory mapping. A missing identity is not a network error, and one bad
/// module manifest must not hide the other modules' valid results.
pub fn fetch_installed_updates(
    ids: &[String],
) -> Result<BTreeMap<String, Result<Option<Candidate>>>> {
    load_installed_updates(
        &mut fetch_json,
        ids,
        env!("CARGO_PKG_VERSION"),
        if cfg!(target_arch = "aarch64") {
            "arm64"
        } else {
            "x64"
        },
    )
}
fn load_installed_updates(
    fetch: &mut impl FnMut(&str) -> Result<Vec<u8>>,
    ids: &[String],
    host: &str,
    architecture: &str,
) -> Result<BTreeMap<String, Result<Option<Candidate>>>> {
    if ids.is_empty() {
        return Ok(BTreeMap::new());
    }
    let mut catalog = parse_catalog(&fetch(&format!("{BASE}index.json"))?)?;
    Ok(ids
        .iter()
        .map(|id| {
            let result = match catalog.modules.remove(id) {
                None => Ok(None),
                Some(entry) => (|| {
                    let directory = entry
                        .module_manifest
                        .strip_suffix("/module.json")
                        .ok_or("InvalidCatalog")?;
                    let releases = parse_updates(
                        &fetch(&format!("{BASE}{}", entry.update_manifest))?,
                        id,
                        directory,
                    )?;
                    make_candidate(id.clone(), entry, releases, host, architecture).map(Some)
                })(),
            };
            (id.clone(), result)
        })
        .collect())
}

struct Temporary(PathBuf);
impl Drop for Temporary {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.0);
    }
}
pub fn download(
    candidate: &Candidate,
    downloads: &Path,
    progress: &mut impl FnMut(u64, bool),
) -> Result<PathBuf> {
    let release = candidate
        .release
        .as_ref()
        .filter(|_| candidate.item.can_download)
        .ok_or("SelectionUnavailable")?;
    let folder = downloads.join("QingToolbox Modules");
    if let Ok(meta) = fs::symlink_metadata(&folder) {
        if !meta.is_dir() || meta.file_type().is_symlink() {
            return Err("StorageUnavailable");
        }
        #[cfg(windows)]
        {
            use std::os::windows::fs::MetadataExt;
            if meta.file_attributes() & 0x400 != 0 {
                return Err("StorageUnavailable");
            }
        }
    }
    fs::create_dir_all(&folder).map_err(|_| "StorageUnavailable")?;
    let mut random = [0u8; 16];
    getrandom::fill(&mut random).map_err(|_| "StorageUnavailable")?;
    let part = Temporary(folder.join(format!(".qing-{:x}.qmod", u128::from_le_bytes(random))));
    let mut output = fs::OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(&part.0)
        .map_err(|_| "StorageUnavailable")?;
    let mut hash = Sha256::new();
    let mut fetched = fetch_repository(
        &release.package.url,
        release.package.size,
        true,
        &mut |chunk, received| {
            output
                .write_all(chunk)
                .map_err(|_| DownloadFailure::StorageUnavailable)?;
            hash.update(chunk);
            progress(received, false);
            Ok(())
        },
    );
    if matches!(fetched, Err(DownloadFailure::SourceUnavailable)) {
        if let Some(api) = package_contents_url(&release.package.url) {
            // Retry the same commit/file, not a mutable branch or a mirror.
            // A failed stream may already have written bytes: reset both the
            // file and digest before retrying, never append the second stream.
            output.set_len(0).map_err(|_| "StorageUnavailable")?;
            output
                .seek(SeekFrom::Start(0))
                .map_err(|_| "StorageUnavailable")?;
            hash = Sha256::new();
            progress(0, false);
            fetched = fetch_repository(&api, release.package.size, true, &mut |chunk, received| {
                output
                    .write_all(chunk)
                    .map_err(|_| DownloadFailure::StorageUnavailable)?;
                hash.update(chunk);
                progress(received, false);
                Ok(())
            });
        }
    }
    fetched.map_err(network_error)?;
    output.sync_all().map_err(|_| "StorageUnavailable")?;
    drop(output);
    progress(release.package.size, true);
    if format!("{:x}", hash.finalize()) != release.package.sha256 {
        return Err("HashMismatch");
    }
    verify_package(&part.0, &candidate.item.id, release)?;
    persist_download(&part.0, &folder, &release.package.file_name)
}
fn verify_package(path: &Path, id: &str, release: &Release) -> Result<()> {
    let preview = preview_qmod(&path.to_string_lossy()).map_err(|_| "InvalidPackage")?;
    if preview.id != id
        || preview.version != release.version
        || preview.api_version != release.api_version
        || preview.sha256 != release.package.sha256
    {
        return Err("InvalidPackage");
    }
    Ok(())
}
fn package_contents_url(url: &str) -> Option<String> {
    let (commit, path) = url
        .strip_prefix("https://raw.githubusercontent.com/QingMo-A/QingToolbox/")?
        .split_once('/')?;
    if commit.len() != 40
        || !commit
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
    {
        return None;
    }
    Some(format!(
        "https://api.github.com/repos/QingMo-A/QingToolbox/contents/{path}?ref={commit}"
    ))
}
fn persist_download(source: &Path, folder: &Path, filename: &str) -> Result<PathBuf> {
    // Never overwrite a user's existing download. Only verified bytes are exposed.
    for suffix in 0..1000 {
        let name = if suffix == 0 {
            filename.to_string()
        } else {
            format!("{} ({suffix}).qmod", filename.trim_end_matches(".qmod"))
        };
        let destination = folder.join(name);
        let mut output = match fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&destination)
        {
            Ok(output) => output,
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(_) => return Err("StorageUnavailable"),
        };
        let copied = fs::File::open(source).and_then(|mut input| {
            let bytes = std::io::copy(&mut input, &mut output)?;
            output.sync_all()?;
            Ok(bytes)
        });
        drop(output);
        if copied.is_err() {
            let _ = fs::remove_file(&destination);
            return Err("StorageUnavailable");
        }
        return Ok(destination);
    }
    Err("StorageUnavailable")
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::{json, Value};
    struct Sandbox(PathBuf);
    impl Sandbox {
        fn new() -> Self {
            let mut random = [0u8; 16];
            getrandom::fill(&mut random).unwrap();
            let path = std::env::temp_dir().join(format!(
                "qing-repository-install-{:x}",
                u128::from_le_bytes(random)
            ));
            fs::create_dir(&path).unwrap();
            Self(path)
        }
    }
    impl Drop for Sandbox {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
    fn package(root: &Path, api: u32) -> (Candidate, PathBuf) {
        use zip::{write::SimpleFileOptions, ZipWriter};
        let source = root.join("test.qmod");
        let mut zip = ZipWriter::new(fs::File::create(&source).unwrap());
        let manifest = json!({ "id": "qing.test", "name": "Test", "version": "1.0.0", "apiVersion": api, "entry": "bin/test.exe", "runtimeType": "Process", "runtimeIsolation": "OutOfProcess", "uiKind": "None", "loadMode": "Manual", "operations": [] });
        for (name, bytes) in [
            ("module.json", serde_json::to_vec(&manifest).unwrap()),
            ("bin/test.exe", b"never execute this fixture".to_vec()),
        ] {
            zip.start_file(name, SimpleFileOptions::default()).unwrap();
            zip.write_all(&bytes).unwrap();
        }
        zip.finish().unwrap();
        let mut releases = updates();
        let preview = preview_qmod(&source.to_string_lossy()).unwrap();
        releases["releases"][0]["apiVersion"] = json!(api);
        releases["releases"][0]["package"]["sha256"] = json!(preview.sha256);
        releases["releases"][0]["package"]["size"] = json!(fs::metadata(&source).unwrap().len());
        let candidates = load_catalog(
            &mut |url| {
                Ok(serde_json::to_vec(&if url.ends_with("index.json") {
                    catalog()
                } else {
                    releases.clone()
                })
                .unwrap())
            },
            "0.3.2-alpha",
            "x64",
        )
        .unwrap();
        (candidates.into_iter().next().unwrap(), source)
    }
    fn catalog() -> Value {
        json!({"schemaVersion":2,"sourceId":"qingtoolbox-official-tauri","moduleProfile":PROFILE,"modules":{"qing.test":{"name":{"en-US":"Test","zh-CN":"测试"},"description":{"en-US":"Test","zh-CN":"测试"},"icon":"Test/icon.svg","moduleManifest":"Test/module.json","updateManifest":"Test/update.json","visibility":"public"}}})
    }
    fn updates() -> Value {
        json!({"schemaVersion":2,"moduleId":"qing.test","publisher":"QingMo-A","releases":[{"version":"1.0.0","channel":"stable","moduleProfile":PROFILE,"apiVersion":1,"platform":"windows","architecture":"x64","minimumHostVersion":"0.3.0-alpha","maximumHostVersionExclusive":null,"publishedAt":"2026-09-25T14:14:12Z","package":{"fileName":"qing.test-1.0.0-tauri.qmod","url":"https://github.com/QingMo-A/QingToolbox/releases/download/modules-test-v1.0.0/qing.test-1.0.0-tauri.qmod","size":100,"sha256":"a".repeat(64)},"releaseNotes":{"en-US":"Test","zh-CN":"测试"}}]})
    }
    #[test]
    fn installed_checks_fetch_only_selected_directories_and_isolate_manifest_errors() {
        let mut index = catalog();
        index["modules"]["qing.bad"] = index["modules"]["qing.test"].clone();
        for (field, value) in [
            ("icon", "Bad/icon.svg"),
            ("moduleManifest", "Bad/module.json"),
            ("updateManifest", "Bad/update.json"),
        ] {
            index["modules"]["qing.bad"][field] = json!(value);
        }
        let mut requested = Vec::new();
        let result = load_installed_updates(
            &mut |url| {
                requested.push(url.to_string());
                if url.ends_with("index.json") {
                    Ok(serde_json::to_vec(&index).unwrap())
                } else if url.ends_with("Test/update.json") {
                    Ok(serde_json::to_vec(&updates()).unwrap())
                } else {
                    Err("NetworkUnavailable")
                }
            },
            &["qing.test".into(), "qing.missing".into(), "qing.bad".into()],
            "0.3.2-alpha",
            "x64",
        )
        .unwrap();
        assert!(
            result["qing.test"]
                .as_ref()
                .unwrap()
                .as_ref()
                .unwrap()
                .item
                .can_download
        );
        assert!(result["qing.missing"].as_ref().unwrap().is_none());
        assert!(matches!(result["qing.bad"], Err("NetworkUnavailable")));
        assert_eq!(
            requested,
            vec![
                format!("{BASE}index.json"),
                format!("{BASE}Test/update.json"),
                format!("{BASE}Bad/update.json")
            ]
        );
        requested.clear();
        load_installed_updates(
            &mut |url| {
                requested.push(url.to_string());
                Ok(serde_json::to_vec(&index).unwrap())
            },
            &[],
            "0.3.2-alpha",
            "x64",
        )
        .unwrap();
        assert!(requested.is_empty());
        assert!(matches!(
            load_installed_updates(
                &mut |_| Err("NetworkUnavailable"),
                &["qing.missing".into()],
                "0.3.2-alpha",
                "x64"
            ),
            Err("NetworkUnavailable")
        ));
    }

    #[test]
    #[ignore = "Explicit read-only official repository smoke, does not download or install"]
    fn live_installed_update_lookup_distinguishes_unknown_identity() {
        let results =
            fetch_installed_updates(&["qing.launcher".into(), "qing.not-recorded".into()])
                .expect("official index");
        let launcher = results["qing.launcher"]
            .as_ref()
            .expect("launcher metadata")
            .as_ref()
            .expect("official launcher");
        assert_eq!(launcher.item.id, "qing.launcher");
        assert!(launcher.item.version.is_some());
        assert!(results["qing.not-recorded"].as_ref().unwrap().is_none());
    }

    #[test]
    fn module_detail_projection_enforces_version_and_backend_download_authority() {
        use crate::{module_updates::UpdateState, modules::ModuleSummary, paths::ModuleSource};
        let mut state = UpdateState::default();
        let candidate = load_catalog(
            &mut |url| {
                Ok(serde_json::to_vec(&if url.ends_with("index.json") {
                    catalog()
                } else {
                    updates()
                })
                .unwrap())
            },
            "0.3.2-alpha",
            "x64",
        )
        .unwrap()
        .remove(0);
        let mut module = ModuleSummary {
            id: "qing.test".into(),
            name: "Test".into(),
            description: None,
            version: "0.9.0".into(),
            api_version: Some(1),
            author: None,
            ui_kind: None,
            runtime_type: None,
            runtime_isolation: None,
            source: ModuleSource::User,
            icon_data_url: None,
            valid: true,
            issues: vec![],
        };
        let ticket = state.begin(&[module.id.clone()]).remove(0);
        state.finish(&ticket, Ok(Some(candidate.clone())));
        let download = DownloadSnapshot::default();
        let view = state.view(&module, false, "zh-CN", &download);
        assert_eq!(view.update_status, "UpdateAvailable");
        assert_eq!(view.release_notes.as_deref(), Some("测试"));
        assert!(!view.is_update_check_enabled);
        assert!(view.can_check_for_update && view.can_download_update);
        assert!(!view.can_install_verified_update);
        assert!(state.download_candidate(&module).is_ok());
        for (version, expected) in [
            ("1.0.0", "UpToDate"),
            ("2.0.0", "LocalVersionNewer"),
            ("invalid", "InvalidLocalVersion"),
        ] {
            module.version = version.into();
            assert_eq!(
                state.view(&module, true, "en-US", &download).update_status,
                expected
            );
            assert!(state.download_candidate(&module).is_err());
        }
        module.version = "0.9.0".into();
        module.source = ModuleSource::Bundled;
        assert!(
            !state
                .view(&module, true, "en-US", &download)
                .can_download_update
        );
        module.source = ModuleSource::User;
        let completed = DownloadSnapshot {
            module_id: module.id.clone(),
            status: "Completed".into(),
            ..Default::default()
        };
        assert_eq!(
            state
                .view(&module, true, "en-US", &completed)
                .download_status,
            "NotDownloaded"
        );
        for (reason, expected) in [
            ("IncompatibleApi", "ModuleApiIncompatible"),
            ("IncompatibleHost", "HostVersionIncompatible"),
            ("IncompatiblePlatform", "PlatformIncompatible"),
        ] {
            let mut blocked = candidate.clone();
            blocked.item.can_download = false;
            blocked.item.unavailable_reason = reason.into();
            let ticket = state.begin(&[module.id.clone()]).remove(0);
            state.finish(&ticket, Ok(Some(blocked)));
            let view = state.view(&module, true, "en-US", &download);
            assert_eq!(view.update_status, expected);
            assert!(!view.can_download_update);
        }
        let ticket = state.begin(&[module.id.clone()]).remove(0);
        state.finish(&ticket, Err("NetworkUnavailable"));
        assert_eq!(
            state
                .view(&module, true, "en-US", &completed)
                .download_status,
            "NotDownloaded"
        );
    }
    #[test]
    fn validates_official_identity_paths_profile_and_bounds() {
        assert!(parse_catalog(&serde_json::to_vec(&catalog()).unwrap()).is_ok());
        for (field, value) in [
            ("sourceId", json!("old")),
            ("schemaVersion", json!(1)),
            ("moduleProfile", json!("wpf")),
        ] {
            let mut c = catalog();
            c[field] = value;
            assert!(parse_catalog(&serde_json::to_vec(&c).unwrap()).is_err());
        }
        let mut c = catalog();
        c["modules"]["qing.test"]["updateManifest"] = json!("../evil.json");
        assert!(parse_catalog(&serde_json::to_vec(&c).unwrap()).is_err());
        assert!(parse_catalog(&vec![b' '; MAX_JSON + 1]).is_err());
    }
    #[test]
    fn rejects_mutable_urls_hashes_and_mismatched_packages() {
        for (field, value) in [
            ("url", json!("https://evil.test/test.qmod")),
            ("fileName", json!("../evil.qmod")),
            ("sha256", json!("abc")),
            ("size", json!(MAX_PACKAGE + 1)),
        ] {
            let mut u = updates();
            u["releases"][0]["package"][field] = value;
            assert!(parse_updates(&serde_json::to_vec(&u).unwrap(), "qing.test", "Test").is_err());
        }
        assert!(parse_updates(
            &serde_json::to_vec(&updates()).unwrap(),
            "qing.other",
            "Test"
        )
        .is_err());
        let mut u = updates();
        let r = u["releases"][0].clone();
        u["releases"].as_array_mut().unwrap().push(r);
        assert!(parse_updates(&serde_json::to_vec(&u).unwrap(), "qing.test", "Test").is_err());
    }
    #[test]
    fn accepts_commit_pinned_packages_only_in_the_catalog_owned_directory() {
        let commit = "a".repeat(40);
        let url = format!("https://raw.githubusercontent.com/QingMo-A/QingToolbox/{commit}/modules/Test/packages/qing.test-1.0.0-tauri.qmod");
        let mut u = updates();
        u["releases"][0]["package"]["url"] = json!(url);
        assert!(parse_updates(&serde_json::to_vec(&u).unwrap(), "qing.test", "Test").is_ok());
        assert!(parse_updates(&serde_json::to_vec(&u).unwrap(), "qing.test", "Other").is_err());
        assert_eq!(package_contents_url(&url), Some(format!("https://api.github.com/repos/QingMo-A/QingToolbox/contents/modules/Test/packages/qing.test-1.0.0-tauri.qmod?ref={commit}")));
        for bad in [
            url.replace(&commit, "modules"),
            url.replace(&commit, "abcd"),
            url.replace("/packages/", "/ui/"),
            url.replace("QingMo-A", "Someone"),
            format!("{url}?x=1"),
            url.replace("qing.test", "qing.other"),
        ] {
            u["releases"][0]["package"]["url"] = json!(bad);
            assert!(parse_updates(&serde_json::to_vec(&u).unwrap(), "qing.test", "Test").is_err());
        }
    }
    #[test]
    fn compatibility_and_download_authority_are_backend_owned() {
        let r = parse_updates(
            &serde_json::to_vec(&updates()).unwrap(),
            "qing.test",
            "Test",
        )
        .unwrap()
        .remove(0);
        assert_eq!(incompatibility(&r, "0.3.2-alpha", "x64"), Ok(""));
        assert_eq!(incompatibility(&r, "0.2.0", "x64"), Ok("IncompatibleHost"));
        assert_eq!(
            incompatibility(&r, "0.3.2-alpha", "arm64"),
            Ok("IncompatiblePlatform")
        );
        let mut state = RepositoryState::default();
        assert!(state.begin_download("qing.test", "en-US").is_err());
        let c = Candidate {
            item: CatalogItem {
                id: "qing.test".into(),
                name: Text::from([("en-US".into(), "Test".into())]),
                description: Text::new(),
                version: Some("1.0.0".into()),
                api_version: Some(1),
                size: 100,
                can_download: true,
                unavailable_reason: String::new(),
            },
            release: Some(r),
        };
        state.begin_catalog().unwrap();
        state.finish_catalog(Ok(vec![c])).unwrap();
        let (job, _) = state.begin_download("qing.test", "en-US").unwrap();
        assert!(state.begin_download("qing.test", "en-US").is_err());
        assert!(state.begin_catalog().is_err());
        state.progress(job + 1, 99, false);
        assert_eq!(state.snapshot.bytes_received, 0);
        state.finish(job, Err("HashMismatch"));
        assert_eq!(state.snapshot.status, "Failed");
        state.begin_catalog().unwrap();
        state.finish_catalog(Err("NetworkUnavailable")).unwrap_err();
        assert!(state.begin_download("qing.test", "en-US").is_err());
    }
    #[test]
    fn hides_diagnostics_and_reports_partial_metadata_errors_per_module() {
        let mut c = catalog();
        c["modules"]["qing.test"]["visibility"] = json!("development");
        let result = load_catalog(
            &mut |_| Ok(serde_json::to_vec(&c).unwrap()),
            "0.3.2-alpha",
            "x64",
        )
        .unwrap();
        assert!(result.is_empty());
        let unavailable = load_catalog(
            &mut |url| {
                if url.ends_with("index.json") {
                    Ok(serde_json::to_vec(&catalog()).unwrap())
                } else {
                    Err("NetworkUnavailable")
                }
            },
            "0.3.2-alpha",
            "x64",
        )
        .unwrap();
        assert_eq!(unavailable.len(), 1);
        assert!(!unavailable[0].item.can_download);
        assert_eq!(unavailable[0].item.unavailable_reason, "NetworkUnavailable");
        assert!(unavailable[0].release.is_none());
    }
    #[test]
    fn invalid_child_manifest_does_not_hide_valid_modules_or_allow_download() {
        let mut index = catalog();
        index["modules"]["qing.bad"] = index["modules"]["qing.test"].clone();
        for (field, value) in [
            ("icon", "Bad/icon.svg"),
            ("moduleManifest", "Bad/module.json"),
            ("updateManifest", "Bad/update.json"),
        ] {
            index["modules"]["qing.bad"][field] = json!(value);
        }
        let candidates = load_catalog(
            &mut |url| {
                Ok(serde_json::to_vec(&if url.ends_with("index.json") {
                    index.clone()
                } else {
                    // Correct for qing.test, wrong identity for qing.bad.
                    updates()
                })
                .unwrap())
            },
            "0.3.3-alpha",
            "x64",
        )
        .unwrap();
        assert_eq!(candidates.len(), 2);
        let valid = candidates
            .iter()
            .find(|c| c.item.id == "qing.test")
            .unwrap();
        let bad = candidates.iter().find(|c| c.item.id == "qing.bad").unwrap();
        assert!(valid.item.can_download);
        assert_eq!(bad.item.unavailable_reason, "InvalidCatalog");
        assert!(!bad.item.can_download);
        assert!(bad.sha256().is_err());
        let mut state = RepositoryState::default();
        state.finish_catalog(Ok(candidates)).unwrap();
        assert!(state.begin_download("qing.bad", "zh-CN").is_err());
        assert!(state.begin_download("qing.test", "zh-CN").is_ok());
        assert!(load_catalog(&mut |_| Err("NetworkUnavailable"), "0.3.3-alpha", "x64").is_err());
    }
    #[test]
    fn preserves_existing_files_and_publishes_verified_copy() {
        let root = std::env::temp_dir().join(format!("qing-repository-{}", std::process::id()));
        fs::create_dir_all(&root).unwrap();
        let source = root.join("part.qmod");
        fs::write(&source, b"verified").unwrap();
        fs::write(root.join("test.qmod"), b"keep").unwrap();
        let path = persist_download(&source, &root, "test.qmod").unwrap();
        assert_eq!(path.file_name().unwrap(), "test (1).qmod");
        assert_eq!(fs::read(root.join("test.qmod")).unwrap(), b"keep");
        assert_eq!(fs::read(path).unwrap(), b"verified");
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn completion_requires_installation_and_retains_failed_install_download() {
        let sandbox = Sandbox::new();
        let (candidate, source) = package(&sandbox.0, 1);
        let mut state = RepositoryState::default();
        state.finish_catalog(Ok(vec![candidate])).unwrap();
        let (job, _) = state.begin_download("qing.test", "en-US").unwrap();
        assert!(!state.finish(job, Ok(source.clone())));
        assert_eq!(state.snapshot.status, "Downloading");
        assert!(!state.begin_installation(job, &source));
        state.progress(job, state.snapshot.expected_bytes, true);
        assert!(!state.begin_installation(job + 1, &source));
        assert!(state.begin_installation(job, &source));
        assert!(state.active());
        assert!(state.begin_catalog().is_err());
        assert!(state.begin_download("qing.test", "en-US").is_err());
        state.progress(job, 1, false);
        assert_eq!(state.snapshot.status, "Installing");
        assert!(!state.finish(job, Err("InstallFailed")));
        assert_eq!(state.snapshot.status, "InstallFailed");
        assert_eq!(state.snapshot.saved_path, source.to_string_lossy());
        assert!(source.is_file());
        assert!(!state.active());
        assert!(!state.finish(job, Ok(source.clone())));
        let (job, _) = state.begin_download("qing.test", "en-US").unwrap();
        state.progress(job, state.snapshot.expected_bytes, true);
        assert!(state.begin_installation(job, &source));
        assert!(state.finish(job, Ok(source)));
        assert_eq!(state.snapshot.status, "Completed");
    }
    #[test]
    fn verified_repository_package_imports_then_updates_without_execution_or_downgrade() {
        let sandbox = Sandbox::new();
        let (candidate, source) = package(&sandbox.0, 1);
        let root = sandbox.0.join("modules");
        assert_eq!(
            installation(&candidate, &source, &root),
            Ok(Installation::Import)
        );
        let imported = crate::importer::import_qmod_into_with_options(
            &source.to_string_lossy(),
            &root,
            Some(candidate.sha256().unwrap()),
            false,
        )
        .unwrap();
        assert!(!imported.replaced);
        let manifest = root.join("qing.test/module.json");
        assert!(manifest.is_file());
        assert_eq!(
            installation(&candidate, &source, &root),
            Ok(Installation::AlreadyInstalled)
        );
        let mut installed: Value = serde_json::from_slice(&fs::read(&manifest).unwrap()).unwrap();
        installed["version"] = json!("0.9.0");
        fs::write(&manifest, serde_json::to_vec(&installed).unwrap()).unwrap();
        fs::write(root.join("qing.test/old-only.txt"), b"old").unwrap();
        assert_eq!(
            installation(&candidate, &source, &root),
            Ok(Installation::Update)
        );
        let updated = crate::importer::update_qmod_into_with_options(
            &source.to_string_lossy(),
            "qing.test",
            &root,
            Some(candidate.sha256().unwrap()),
            false,
        )
        .unwrap();
        assert!(updated.replaced);
        assert_eq!(updated.previous_version.as_deref(), Some("0.9.0"));
        assert!(!root.join("qing.test/old-only.txt").exists());
        assert_eq!(
            installation(&candidate, &source, &root),
            Ok(Installation::AlreadyInstalled)
        );
        installed["version"] = json!("2.0.0");
        fs::write(&manifest, serde_json::to_vec(&installed).unwrap()).unwrap();
        assert_eq!(
            installation(&candidate, &source, &root),
            Ok(Installation::AlreadyInstalled)
        );
        assert_eq!(
            fs::read(root.join("qing.test/bin/test.exe")).unwrap(),
            b"never execute this fixture"
        );
    }
    #[test]
    fn auto_install_fails_closed_on_changed_package_api_or_installed_identity() {
        let sandbox = Sandbox::new();
        let root = sandbox.0.join("modules");
        let (candidate, source) = package(&sandbox.0, 1);
        assert_eq!(
            installation(&candidate, &source, &root),
            Ok(Installation::Import)
        );
        // The importer also pins the catalog digest, closing the gap after planning.
        fs::write(&source, b"changed").unwrap();
        assert_eq!(
            installation(&candidate, &source, &root),
            Err("InvalidPackage")
        );
        assert!(crate::importer::import_qmod_into_with_options(
            &source.to_string_lossy(),
            &root,
            Some(candidate.sha256().unwrap()),
            false
        )
        .is_err());
        assert!(!root.exists());
        let (mut incompatible, source) = package(&sandbox.0, 2);
        incompatible.item.can_download = true; // even a bypassed catalog UI cannot permit incompatible auto-install
        assert_eq!(
            installation(&incompatible, &source, &root),
            Err("IncompatibleApi")
        );
        assert!(!root.exists());
        let (candidate, source) = package(&sandbox.0, 1);
        fs::create_dir_all(root.join("qing.test")).unwrap();
        for manifest in [
            json!({"id":"qing.other","version":"0.9.0"}),
            json!({"id":"qing.test","version":"unknown"}),
        ] {
            fs::write(
                root.join("qing.test/module.json"),
                serde_json::to_vec(&manifest).unwrap(),
            )
            .unwrap();
            assert_eq!(
                installation(&candidate, &source, &root),
                Err("InstallFailed")
            );
        }
    }
    #[test]
    #[ignore = "Explicit read-only network smoke; downloads to a temporary test directory, never installs"]
    fn live_official_catalog_and_verified_download() {
        let candidates = fetch_catalog().expect("official catalog");
        assert!(!candidates.is_empty());
        assert!(candidates.iter().all(|c| c.item.id != "qing.canary"));
        let requested = std::env::var("QING_MODULE_DOWNLOAD_SMOKE_ID").ok();
        let selected = candidates
            .iter()
            .filter(|c| {
                c.item.can_download && requested.as_ref().map_or(true, |id| *id == c.item.id)
            })
            .min_by_key(|c| c.item.size)
            .expect("compatible published package");
        let root = std::env::temp_dir().join(format!("qing-official-smoke-{}", std::process::id()));
        fs::create_dir_all(&root).unwrap();
        let mut received = 0;
        let file =
            download(selected, &root, &mut |bytes, _| received = bytes).expect("verified download");
        assert_eq!(received, selected.item.size);
        assert!(preview_qmod(&file.to_string_lossy()).unwrap().compatible);
        println!(
            "Verified {} v{} ({} bytes)",
            selected.item.id,
            selected.item.version.as_deref().unwrap(),
            received
        );
        let canonical = root.canonicalize().unwrap();
        assert!(canonical.starts_with(std::env::temp_dir().canonicalize().unwrap()));
        fs::remove_dir_all(canonical).unwrap();
    }
}
