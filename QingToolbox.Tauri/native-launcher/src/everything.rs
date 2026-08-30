//! The Qing Launcher Everything bridge.
//!
//! Everything remains a third-party indexer.  This module only owns the
//! lifetime of the private client instance and translates its bounded ES
//! output into launcher views.  In particular, a web page never supplies a
//! path to an operating-system operation: every open/copy request must refer
//! to an id in `result_map`, populated by a preceding search in this process.

use std::{
    collections::HashMap,
    env, fs, io,
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use serde::Serialize;
use sha2::{Digest, Sha256};

pub const MAX_QUERY_BYTES: usize = 4096;
pub const MAX_RESULTS: usize = 20;
const MAX_RESULT_TEXT_CHARS: usize = 2048;
const MAX_EXPORT_BYTES: u64 = 8 * 1024 * 1024;

const RUNTIME_VERSION: &str = "1.4.1.1032";
const SERVICE_INSTANCE: &str = "QingToolboxLauncher";
const SERVICE_PIPE: &str = r"\\.\PIPE\QingToolboxLauncherEverythingService";
const SERVICE_SECURITY_DESCRIPTOR: &str = "D:(A;OICI;GRGW;;;AU)";
const READY_TIMEOUT: Duration = Duration::from_secs(20);
const QUERY_TIMEOUT: Duration = Duration::from_secs(18);
const PROCESS_POLL: Duration = Duration::from_millis(40);

const ASSET_HASHES: &[(&str, &str)] = &[
    (
        "Everything.exe",
        "F191F756996A14A11E5445FA7103D302EFD510CF2FBF920E6C0C8ED51D512E36",
    ),
    (
        "es.exe",
        "3BE7185707E8023CD9295DBCB7A3FA4092A3D8F52B7FA92A0B84243AB40D12F3",
    ),
    (
        "Everything64.dll",
        "81B5BE18126ACD2C2B913F8F4A821E476B18393CDD3DEBD03387C50AFD8DB88F",
    ),
    (
        "LICENSE.txt",
        "C13D19ADCBFD5D07E9512DE9DF99956A3423399ED1FADC5FD33186697AD8DF2F",
    ),
    (
        "NOTICE.md",
        "35BEFBE14AB7B24657E07B6EC3B481CF17526AD8BC3248211FFAAED39AC7C41B",
    ),
];

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EverythingSearchMode {
    All,
    File,
    Directory,
}

impl EverythingSearchMode {
    pub fn as_wire(self) -> &'static str {
        match self {
            Self::All => "everything-all",
            Self::File => "everything-file",
            Self::Directory => "everything-directory",
        }
    }

    fn query_prefix(self) -> &'static str {
        match self {
            Self::All => "",
            Self::File => "file:",
            Self::Directory => "folder:",
        }
    }
}

/// Parse the same prefix grammar used by the Vue surface.  Keeping a backend
/// parser as well means a future UI cannot accidentally turn `/e:f` into a
/// normal launcher query or bypass the mode boundary.
pub fn parse_search_mode(value: &str) -> (EverythingSearchMode, String) {
    // The prefix is deliberately ASCII-only.  Consume exactly one separator
    // after it so Everything query text (including a second leading space)
    // remains otherwise untouched.  This mirrors the Vue parser and accepts
    // all ASCII whitespace users commonly enter (space, tab, etc.).
    let bytes = value.as_bytes();
    if bytes.len() >= 2 && bytes[0] == b'/' && bytes[1].eq_ignore_ascii_case(&b'e') {
        let (mode, prefix_len) = match bytes.get(2).copied() {
            None => (EverythingSearchMode::All, 2),
            Some(byte) if byte.is_ascii_whitespace() => (EverythingSearchMode::All, 2),
            Some(b':') => match bytes.get(3).map(u8::to_ascii_lowercase) {
                Some(b'f') => (EverythingSearchMode::File, 4),
                Some(b'd') => (EverythingSearchMode::Directory, 4),
                _ => return (EverythingSearchMode::All, value.to_string()),
            },
            _ => return (EverythingSearchMode::All, value.to_string()),
        };
        if bytes.len() == prefix_len {
            return (mode, String::new());
        }
        if bytes
            .get(prefix_len)
            .is_some_and(|byte| byte.is_ascii_whitespace())
        {
            return (mode, value[prefix_len + 1..].to_string());
        }
    }
    (EverythingSearchMode::All, value.to_string())
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct EverythingResultView {
    pub id: String,
    pub name: String,
    pub parent_path: String,
    pub is_directory: bool,
    pub result_type: &'static str,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct EverythingSearchResponse {
    pub request_id: String,
    pub mode: &'static str,
    pub query: String,
    pub status: &'static str,
    pub results: Vec<EverythingResultView>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EverythingErrorKind {
    Unavailable,
    Indexing,
    Ipc,
    Failed,
}

#[derive(Debug)]
pub struct EverythingError {
    pub kind: EverythingErrorKind,
    pub message: String,
}

impl EverythingError {
    fn unavailable(message: impl Into<String>) -> Self {
        Self {
            kind: EverythingErrorKind::Unavailable,
            message: message.into(),
        }
    }

    fn indexing(message: impl Into<String>) -> Self {
        Self {
            kind: EverythingErrorKind::Indexing,
            message: message.into(),
        }
    }

    fn ipc(message: impl Into<String>) -> Self {
        Self {
            kind: EverythingErrorKind::Ipc,
            message: message.into(),
        }
    }

    fn failed(message: impl Into<String>) -> Self {
        Self {
            kind: EverythingErrorKind::Failed,
            message: message.into(),
        }
    }

    pub fn status(&self) -> &'static str {
        match self.kind {
            EverythingErrorKind::Unavailable => "unavailable",
            EverythingErrorKind::Indexing => "indexing",
            EverythingErrorKind::Ipc => "error",
            EverythingErrorKind::Failed => "error",
        }
    }
}

#[derive(Debug)]
struct StoredResult {
    path: PathBuf,
    is_directory: bool,
}

/// Owns only the Everything client instance created by this module.  The
/// dedicated service, when enabled, is intentionally left installed/running;
/// it is not a user-owned Everything instance and makes later startups fast.
pub struct EverythingRuntime {
    runtime_directory: PathBuf,
    data_directory: PathBuf,
    client_directory: PathBuf,
    instance_name: String,
    owned_instance: bool,
    owned_process: Option<Child>,
    active_everything: Option<PathBuf>,
    active_es: Option<PathBuf>,
    assets_checked: bool,
    result_map: HashMap<String, StoredResult>,
    next_result_id: u64,
}

impl EverythingRuntime {
    pub fn new(module_directory: PathBuf, data_directory: PathBuf) -> Self {
        let module_directory = locate_module_directory(module_directory);
        let data_directory = data_directory.join("everything-runtime");
        let instance_name = format!(
            "QL{}",
            short_hash(&path_identity(&data_directory).to_ascii_uppercase())
        );
        Self {
            runtime_directory: module_directory.join("third-party").join("Everything"),
            client_directory: data_directory.join("client"),
            data_directory,
            instance_name,
            owned_instance: false,
            owned_process: None,
            active_everything: None,
            active_es: None,
            assets_checked: false,
            result_map: HashMap::new(),
            next_result_id: 0,
        }
    }

    pub fn search(
        &mut self,
        mode: EverythingSearchMode,
        query: String,
        request_id: String,
    ) -> Result<EverythingSearchResponse, EverythingError> {
        validate_query(&query)?;
        if query.is_empty() {
            self.result_map.clear();
            return Ok(EverythingSearchResponse {
                request_id,
                mode: mode.as_wire(),
                query,
                status: "ready",
                results: Vec::new(),
                error: None,
            });
        }

        // Result ids are scoped to the latest search. Invalidate them before
        // touching the runtime so a failed/new query cannot leave an old id
        // usable through a stale UI event.
        self.result_map.clear();
        self.ensure_ready()?;
        let export_token = self.next_token();
        let export_path = self
            .data_directory
            .join(format!("query-{export_token}.txt"));
        let constrained = format!("{}{}", mode.query_prefix(), query);
        let es = self
            .active_es
            .clone()
            .ok_or_else(|| EverythingError::ipc("Everything IPC 客户端不可用。"))?;
        let result = self.run_export(&es, &export_path, &constrained);
        let bytes = match result {
            Ok(_) => {
                let size = fs::metadata(&export_path)
                    .map_err(|error| {
                        EverythingError::ipc(format!("Everything 未生成搜索结果：{error}"))
                    })?
                    .len();
                if size > MAX_EXPORT_BYTES {
                    Err(EverythingError::ipc("Everything 搜索结果超过大小限制。"))
                } else {
                    fs::read(&export_path).map_err(|error| {
                        EverythingError::ipc(format!("Everything 未生成搜索结果：{error}"))
                    })
                }
            }
            Err(error) => Err(error),
        };
        let _ = fs::remove_file(&export_path);
        let bytes = bytes?;
        let paths = parse_export(&bytes);
        self.result_map.clear();
        let mut views = Vec::with_capacity(paths.len());
        for path in paths.into_iter().take(MAX_RESULTS) {
            let is_directory = path.is_dir();
            let id = self.next_result_token();
            let name = path
                .file_name()
                .and_then(|value| value.to_str())
                .filter(|value| !value.is_empty())
                .unwrap_or_else(|| path.to_str().unwrap_or("结果"))
                .chars()
                .take(MAX_RESULT_TEXT_CHARS)
                .collect::<String>();
            let parent_path = path
                .parent()
                .and_then(|value| value.to_str())
                .unwrap_or_default()
                .chars()
                .take(MAX_RESULT_TEXT_CHARS)
                .collect::<String>();
            self.result_map
                .insert(id.clone(), StoredResult { path, is_directory });
            views.push(EverythingResultView {
                id,
                name,
                parent_path,
                is_directory,
                result_type: if is_directory { "directory" } else { "file" },
            });
        }
        Ok(EverythingSearchResponse {
            request_id,
            mode: mode.as_wire(),
            query,
            status: "ready",
            results: views,
            error: None,
        })
    }

    pub fn open_result(&mut self, result_id: &str) -> Result<(), EverythingError> {
        let result = self.lookup_result(result_id)?;
        if result.is_directory {
            return spawn_explorer(&result.path);
        }
        let extension = result
            .path
            .extension()
            .and_then(|value| value.to_str())
            .unwrap_or_default();
        if extension.eq_ignore_ascii_case("exe") {
            spawn_executable(&result.path)
        } else {
            shell_open_file(&result.path)
        }
    }

    pub fn open_result_folder(&mut self, result_id: &str) -> Result<(), EverythingError> {
        let result = self.lookup_result(result_id)?;
        let folder = if result.is_directory {
            result.path.clone()
        } else {
            result
                .path
                .parent()
                .map(Path::to_path_buf)
                .ok_or_else(|| EverythingError::failed("结果没有可打开的父目录。"))?
        };
        spawn_explorer(&folder)
    }

    pub fn copy_result_path(&mut self, result_id: &str) -> Result<(), EverythingError> {
        let result = self.lookup_result(result_id)?;
        copy_text_to_clipboard(&result.path.to_string_lossy())
            .map_err(|error| EverythingError::failed(format!("无法复制路径：{error}")))
    }

    fn lookup_result(&self, result_id: &str) -> Result<&StoredResult, EverythingError> {
        if !valid_result_id(result_id) {
            return Err(EverythingError::failed("Everything 结果 id 无效。"));
        }
        let result = self
            .result_map
            .get(result_id)
            .ok_or_else(|| EverythingError::failed("Everything 结果已过期，请重新搜索。"))?;
        if !result.path.is_absolute() || !result.path.exists() {
            return Err(EverythingError::failed("Everything 结果已不存在。"));
        }
        Ok(result)
    }

    fn ensure_ready(&mut self) -> Result<(), EverythingError> {
        self.validate_assets()?;
        fs::create_dir_all(&self.data_directory).map_err(|error| {
            EverythingError::unavailable(format!("无法创建 Everything 数据目录：{error}"))
        })?;

        let bundled_es = self.runtime_directory.join("es.exe");
        if self.active_es.is_none() {
            self.active_es = Some(bundled_es.clone());
        }
        if self.probe_instance(&bundled_es).is_ok() {
            // A prior Qing Launcher process may have left our private client
            // alive. Reuse it without claiming ownership of another user's
            // Everything instance.
            self.active_everything = Some(self.runtime_directory.join("Everything.exe"));
            return Ok(());
        }

        let use_service = env::var("QING_LAUNCHER_EVERYTHING_SERVICE")
            .map(|value| value != "0")
            .unwrap_or(true);
        let (everything, es) = self.prepare_client()?;
        if use_service {
            self.ensure_service()?;
        }
        let config = self.write_config(use_service)?;
        let mut command = hidden_process(&everything);
        command.args(["-instance", &self.instance_name, "-startup", "-config"]);
        command.arg(&config);
        let child = command.spawn().map_err(|error| {
            EverythingError::unavailable(format!("内置 Everything 无法启动：{error}"))
        })?;
        self.owned_process = Some(child);
        self.owned_instance = true;
        self.active_everything = Some(everything.clone());
        self.active_es = Some(es.clone());

        if use_service {
            // 1.4 creates the named client before accepting the service pipe
            // option. This second command is intentionally short-lived.
            thread::sleep(Duration::from_millis(250));
            let mut connect = hidden_process(&everything);
            connect.args([
                "-instance",
                &self.instance_name,
                "-service-pipe-name",
                SERVICE_PIPE,
            ]);
            let output = run_with_timeout(connect, Duration::from_secs(8)).map_err(|error| {
                EverythingError::ipc(format!("Everything 服务管道连接失败：{error}"))
            })?;
            if !output.status.success() {
                return Err(EverythingError::ipc("Everything 服务管道连接失败。"));
            }
        }

        let deadline = Instant::now() + READY_TIMEOUT;
        while Instant::now() < deadline {
            if self.probe_instance(&es).is_ok() {
                return Ok(());
            }
            thread::sleep(Duration::from_millis(200));
        }
        Err(EverythingError::indexing(
            "Everything 正在建立索引，请稍后重试。",
        ))
    }

    fn validate_assets(&mut self) -> Result<(), EverythingError> {
        if self.assets_checked {
            return Ok(());
        }
        if !cfg!(windows) {
            return Err(EverythingError::unavailable(
                "内置 Everything 仅支持 Windows。",
            ));
        }
        for (name, expected) in ASSET_HASHES {
            let path = self.runtime_directory.join(name);
            let actual = sha256_file(&path).map_err(|error| {
                EverythingError::unavailable(format!("内置 Everything 运行时不可用：{error}"))
            })?;
            if !actual.eq_ignore_ascii_case(expected) {
                return Err(EverythingError::unavailable(format!(
                    "内置 Everything 组件校验失败：{name}。"
                )));
            }
        }
        self.assets_checked = true;
        Ok(())
    }

    fn prepare_client(&self) -> Result<(PathBuf, PathBuf), EverythingError> {
        fs::create_dir_all(&self.client_directory).map_err(|error| {
            EverythingError::unavailable(format!("无法准备 Everything 客户端目录：{error}"))
        })?;
        for (name, expected_hash) in ASSET_HASHES {
            let source = self.runtime_directory.join(name);
            let destination = self.client_directory.join(name);
            if !destination.is_file()
                || sha256_file(&destination).ok().as_deref() != Some(*expected_hash)
            {
                fs::copy(&source, &destination).map_err(|error| {
                    EverythingError::unavailable(format!("无法复制 Everything 组件：{error}"))
                })?;
            }
        }
        Ok((
            self.client_directory.join("Everything.exe"),
            self.client_directory.join("es.exe"),
        ))
    }

    fn write_config(&self, use_service: bool) -> Result<PathBuf, EverythingError> {
        let config = self
            .client_directory
            .join(format!("Everything-{}.ini", self.instance_name));
        let service = if use_service { 1 } else { 0 };
        let pipe = if use_service { SERVICE_PIPE } else { "" };
        // Development/integration runs can provide a semicolon-separated,
        // bounded root list without changing the production service (which
        // indexes fixed volumes through Everything itself).
        let roots = if use_service {
            Vec::new()
        } else {
            env::var("QING_LAUNCHER_EVERYTHING_INDEX_ROOTS")
                .ok()
                .map(|value| {
                    value
                        .split(';')
                        .map(str::trim)
                        .filter(|value| !value.is_empty())
                        .map(PathBuf::from)
                        .filter(|path| path.is_dir())
                        .take(32)
                        .collect::<Vec<_>>()
                })
                .unwrap_or_default()
        };
        let fixed_volumes = if use_service || roots.is_empty() {
            1
        } else {
            0
        };
        let folder_values = roots
            .iter()
            .map(|path| path.to_string_lossy().to_string())
            .collect::<Vec<_>>();
        let folder_flags = std::iter::repeat("1")
            .take(folder_values.len())
            .collect::<Vec<_>>()
            .join(",");
        let folder_buffers = std::iter::repeat("65536")
            .take(folder_values.len())
            .collect::<Vec<_>>()
            .join(",");
        let folder_zeroes = std::iter::repeat("0")
            .take(folder_values.len())
            .collect::<Vec<_>>()
            .join(",");
        let folders = if folder_values.is_empty() {
            String::new()
        } else {
            format!(
                "folders={}\r\nfolder_monitor_changes={folder_flags}\r\nfolder_buffer_size_list={folder_buffers}\r\nfolder_rescan_if_full_list={folder_flags}\r\nfolder_update_types={folder_zeroes}\r\nfolder_update_days={folder_zeroes}\r\nfolder_update_ats={folder_zeroes}\r\nfolder_update_intervals={folder_zeroes}\r\nfolder_update_interval_types={folder_zeroes}\r\n",
                folder_values.join(",")
            )
        };
        let data = format!(
            "[Everything]\r\napp_data=0\r\nrun_as_admin=0\r\nservice={service}\r\nindex_as_admin=0\r\nshow_tray_icon=0\r\nrun_in_background=1\r\nshow_window_on_startup=0\r\ncheck_for_updates=0\r\ncheck_for_beta_updates=0\r\nservice_pipe_name={pipe}\r\nauto_include_fixed_volumes={fixed_volumes}\r\nauto_include_removable_volumes=0\r\ndb_location={}\r\n{folders}",
            self.data_directory.display(),
        );
        fs::write(&config, data.as_bytes()).map_err(|error| {
            EverythingError::unavailable(format!("无法写入 Everything 配置：{error}"))
        })?;
        Ok(config)
    }

    fn ensure_service(&self) -> Result<(), EverythingError> {
        let service_directory = self.data_directory.join("service");
        fs::create_dir_all(&service_directory).map_err(|error| {
            EverythingError::unavailable(format!("无法准备 Everything 服务目录：{error}"))
        })?;
        let source = self.runtime_directory.join("Everything.exe");
        let service_executable = service_directory.join("Everything.exe");
        if !service_executable.is_file()
            || sha256_file(&service_executable).ok().as_deref() != Some(ASSET_HASHES[0].1)
        {
            fs::copy(&source, &service_executable).map_err(|error| {
                EverythingError::unavailable(format!("无法复制 Everything 服务组件：{error}"))
            })?;
        }
        let marker = self
            .data_directory
            .join(format!("service-installed-{RUNTIME_VERSION}"));
        let expected = format!(
            "{RUNTIME_VERSION}\n{}\n{SERVICE_PIPE}\n{SERVICE_SECURITY_DESCRIPTOR}",
            service_executable.display()
        );
        if fs::read_to_string(&marker).ok().as_deref() == Some(expected.as_str()) {
            return Ok(());
        }
        #[cfg(windows)]
        {
            install_service_elevated(&service_executable)?;
            fs::write(&marker, expected.as_bytes()).map_err(|error| {
                EverythingError::unavailable(format!("无法保存 Everything 服务状态：{error}"))
            })?;
            Ok(())
        }
        #[cfg(not(windows))]
        {
            let _ = expected;
            Err(EverythingError::unavailable(
                "内置 Everything 服务仅支持 Windows。",
            ))
        }
    }

    fn probe_instance(&self, es: &Path) -> Result<(), EverythingError> {
        let mut command = hidden_process(es);
        command.args(["-instance", &self.instance_name, "-get-everything-version"]);
        let output = run_with_timeout(command, Duration::from_secs(3))
            .map_err(|error| EverythingError::ipc(format!("Everything IPC 不可用：{error}")))?;
        if output.status.success() {
            Ok(())
        } else {
            Err(EverythingError::ipc("Everything IPC 实例尚未就绪。"))
        }
    }

    fn run_export(
        &self,
        es: &Path,
        export_path: &Path,
        query: &str,
    ) -> Result<(), EverythingError> {
        let mut command = hidden_process(es);
        command.args([
            "-instance",
            &self.instance_name,
            "-n",
            "20",
            "-timeout",
            "15000",
            "-utf8-bom",
            "-no-header",
            "-full-path-and-name",
            "-export-txt",
        ]);
        command.arg(export_path).arg(query);
        let output = run_with_timeout(command, QUERY_TIMEOUT)
            .map_err(|error| EverythingError::ipc(format!("Everything 搜索失败：{error}")))?;
        if output.status.success() {
            return Ok(());
        }
        // ES uses exit code 8 while the database is still loading.
        if output.status.code() == Some(8) {
            return Err(EverythingError::indexing(
                "Everything 正在建立索引，请稍后重试。",
            ));
        }
        let detail = String::from_utf8_lossy(&output.stderr).trim().to_string();
        Err(EverythingError::ipc(if detail.is_empty() {
            "Everything IPC 查询不可用。".to_string()
        } else {
            format!("Everything IPC 查询失败：{detail}")
        }))
    }

    fn next_token(&mut self) -> String {
        self.next_result_id = self.next_result_id.wrapping_add(1);
        format!(
            "qer-{}-{:x}-{}",
            short_hash(&self.instance_name),
            monotonic_token(),
            self.next_result_id
        )
    }

    fn next_result_token(&mut self) -> String {
        self.next_token()
    }

    pub fn shutdown(&mut self) {
        // Only address the named instance while the exact child handle we
        // created is still alive.  If it already exited, another process could
        // have reused the deterministic instance name; sending `-exit` then
        // would violate the ownership boundary.
        let child_is_alive = self
            .owned_process
            .as_mut()
            .is_some_and(|child| matches!(child.try_wait(), Ok(None)));
        if self.owned_instance && child_is_alive {
            if let Some(everything) = self.active_everything.clone() {
                let mut command = hidden_process(&everything);
                command.args(["-instance", &self.instance_name, "-exit"]);
                let _ = run_with_timeout(command, Duration::from_secs(3));
            }
        }
        if let Some(child) = self.owned_process.as_mut() {
            terminate_child(child);
        }
        self.owned_process = None;
        self.owned_instance = false;
    }
}

impl Drop for EverythingRuntime {
    fn drop(&mut self) {
        self.shutdown();
    }
}

fn validate_query(query: &str) -> Result<(), EverythingError> {
    if query.len() > MAX_QUERY_BYTES {
        return Err(EverythingError::failed("搜索内容过长。"));
    }
    if query
        .chars()
        .any(|character| character == '\0' || character == '\r' || character == '\n')
    {
        return Err(EverythingError::failed("搜索内容包含无效字符。"));
    }
    Ok(())
}

fn parse_export(bytes: &[u8]) -> Vec<PathBuf> {
    let text = decode_export_text(bytes);
    let mut seen = std::collections::HashSet::new();
    text.lines()
        .map(|line| line.trim_start_matches('\u{feff}').trim())
        .filter(|line| !line.is_empty())
        .filter_map(|line| {
            let path = PathBuf::from(line);
            if !path.is_absolute() || !seen.insert(path.to_string_lossy().to_ascii_lowercase()) {
                return None;
            }
            Some(path)
        })
        .take(MAX_RESULTS)
        .collect()
}

/// Everything's text exporter normally emits UTF-8 when `-utf8-bom` is
/// supplied, but older clients and user-specific settings can still produce
/// UTF-16 or the active Windows code page. Decode those forms before turning
/// paths into `PathBuf`s; otherwise a perfectly valid Chinese path becomes a
/// string of replacement characters and cannot be opened later.
fn decode_export_text(bytes: &[u8]) -> String {
    if bytes.starts_with(&[0xFF, 0xFE]) {
        return decode_utf16(&bytes[2..], true);
    }
    if bytes.starts_with(&[0xFE, 0xFF]) {
        return decode_utf16(&bytes[2..], false);
    }
    let utf8 = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]).unwrap_or(bytes);
    if let Ok(text) = std::str::from_utf8(utf8) {
        return text.to_string();
    }

    // A BOM-less UTF-16 export is easy to recognize because one byte in most
    // code units is zero for ordinary path text. Keep the heuristic strict so
    // arbitrary malformed UTF-8 is not accidentally reinterpreted.
    if utf8.len() >= 4 && utf8.len() % 2 == 0 {
        let pairs = utf8.chunks_exact(2);
        let zero_low = pairs.clone().filter(|pair| pair[1] == 0).count();
        let zero_high = pairs.filter(|pair| pair[0] == 0).count();
        let threshold = utf8.len() / 4;
        if zero_low >= threshold {
            return decode_utf16(utf8, true);
        }
        if zero_high >= threshold {
            return decode_utf16(utf8, false);
        }
    }

    #[cfg(windows)]
    if let Some(text) = decode_active_code_page(utf8) {
        return text;
    }
    String::from_utf8_lossy(utf8).into_owned()
}

fn decode_utf16(bytes: &[u8], little_endian: bool) -> String {
    let units = bytes
        .chunks_exact(2)
        .map(|pair| {
            if little_endian {
                u16::from_le_bytes([pair[0], pair[1]])
            } else {
                u16::from_be_bytes([pair[0], pair[1]])
            }
        })
        .collect::<Vec<_>>();
    String::from_utf16_lossy(&units)
}

#[cfg(windows)]
fn decode_active_code_page(bytes: &[u8]) -> Option<String> {
    use std::ptr;
    use windows_sys::Win32::Globalization::{MultiByteToWideChar, CP_ACP};

    if bytes.is_empty() {
        return Some(String::new());
    }
    let length = i32::try_from(bytes.len()).ok()?;
    let required =
        unsafe { MultiByteToWideChar(CP_ACP, 0, bytes.as_ptr(), length, ptr::null_mut(), 0) };
    if required <= 0 {
        return None;
    }
    let mut wide = vec![0u16; required as usize];
    let written = unsafe {
        MultiByteToWideChar(
            CP_ACP,
            0,
            bytes.as_ptr(),
            length,
            wide.as_mut_ptr(),
            required,
        )
    };
    (written == required).then(|| String::from_utf16_lossy(&wide))
}

fn valid_result_id(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
}

fn locate_module_directory(candidate: PathBuf) -> PathBuf {
    if candidate.join("third-party").join("Everything").is_dir() {
        return candidate;
    }
    if let Ok(override_path) = env::var("QINGTOOLBOX_MODULE_DIRECTORY") {
        let path = PathBuf::from(override_path);
        if path.join("third-party").join("Everything").is_dir() {
            return path;
        }
    }
    if let Ok(executable) = env::current_exe() {
        if let Some(parent) = executable.parent() {
            if parent.join("third-party").join("Everything").is_dir() {
                return parent.to_path_buf();
            }
        }
    }
    candidate
}

fn path_identity(path: &Path) -> String {
    fs::canonicalize(path)
        .unwrap_or_else(|_| path.to_path_buf())
        .to_string_lossy()
        .replace('/', "\\")
}

fn short_hash(value: &str) -> String {
    let digest = Sha256::digest(value.as_bytes());
    digest[..4]
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect()
}

fn sha256_file(path: &Path) -> io::Result<String> {
    let bytes = fs::read(path)?;
    let digest = Sha256::digest(bytes);
    Ok(digest.iter().map(|byte| format!("{byte:02X}")).collect())
}

fn monotonic_token() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_nanos())
        .unwrap_or_default()
}

fn hidden_process(path: &Path) -> Command {
    let mut command = Command::new(path);
    command
        .current_dir(path.parent().unwrap_or_else(|| Path::new(".")))
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    apply_hidden_process_flags(&mut command);
    command
}

fn run_with_timeout(mut command: Command, timeout: Duration) -> io::Result<std::process::Output> {
    let mut child = command.spawn()?;
    let deadline = Instant::now() + timeout;
    loop {
        if child.try_wait()?.is_some() {
            return child.wait_with_output();
        }
        if Instant::now() >= deadline {
            let _ = child.kill();
            let _ = child.wait();
            return Err(io::Error::new(io::ErrorKind::TimedOut, "process timed out"));
        }
        thread::sleep(PROCESS_POLL);
    }
}

fn terminate_child(child: &mut Child) {
    match child.try_wait() {
        Ok(Some(_)) => {}
        Ok(None) | Err(_) => {
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}

fn spawn_explorer(path: &Path) -> Result<(), EverythingError> {
    let mut command = Command::new("explorer.exe");
    command.arg(path);
    apply_hidden_process_flags(&mut command);
    command
        .spawn()
        .map(|_| ())
        .map_err(|error| EverythingError::failed(format!("无法打开资源：{error}")))
}

/// Ask Windows to open a file through the user's normal association.  Calling
/// `explorer.exe` for documents looks similar, but it can show a folder view
/// instead of launching the registered application.  ShellExecute is the
/// native equivalent of a `UseShellExecute=true` process start and keeps the
/// path as a single, non-shell-parsed argument.
#[cfg(windows)]
fn shell_open_file(path: &Path) -> Result<(), EverythingError> {
    use std::{mem, os::windows::ffi::OsStrExt};
    use windows_sys::Win32::{
        Foundation::GetLastError,
        UI::Shell::{ShellExecuteExW, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW},
        UI::WindowsAndMessaging::SW_SHOWNORMAL,
    };

    let file = path
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect::<Vec<_>>();
    let verb = "open"
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect::<Vec<_>>();
    let mut info: SHELLEXECUTEINFOW = unsafe { mem::zeroed() };
    info.cbSize = mem::size_of::<SHELLEXECUTEINFOW>() as u32;
    info.fMask = SEE_MASK_NOCLOSEPROCESS;
    info.lpVerb = verb.as_ptr();
    info.lpFile = file.as_ptr();
    info.nShow = SW_SHOWNORMAL;
    let ok = unsafe { ShellExecuteExW(&mut info) };
    if ok == 0 {
        return Err(EverythingError::failed(format!(
            "无法打开文件（Windows 错误 {}）。",
            unsafe { GetLastError() }
        )));
    }
    if !info.hProcess.is_null() {
        unsafe { windows_sys::Win32::Foundation::CloseHandle(info.hProcess) };
    }
    Ok(())
}

#[cfg(not(windows))]
fn shell_open_file(path: &Path) -> Result<(), EverythingError> {
    spawn_explorer(path)
}

fn spawn_executable(path: &Path) -> Result<(), EverythingError> {
    let mut command = Command::new(path);
    if let Some(parent) = path.parent() {
        command.current_dir(parent);
    }
    apply_hidden_process_flags(&mut command);
    command
        .spawn()
        .map(|_| ())
        .map_err(|error| EverythingError::failed(format!("无法启动程序：{error}")))
}

#[cfg(windows)]
fn apply_hidden_process_flags(command: &mut Command) {
    use std::os::windows::process::CommandExt;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    command.creation_flags(CREATE_NO_WINDOW);
}

#[cfg(not(windows))]
fn apply_hidden_process_flags(_command: &mut Command) {}

#[cfg(windows)]
fn install_service_elevated(executable: &Path) -> Result<(), EverythingError> {
    use std::{mem, os::windows::ffi::OsStrExt};
    use windows_sys::Win32::{
        Foundation::{CloseHandle, GetLastError, HANDLE, WAIT_OBJECT_0},
        System::Threading::{GetExitCodeProcess, WaitForSingleObject},
        UI::{
            Shell::{ShellExecuteExW, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW},
            WindowsAndMessaging::SW_HIDE,
        },
    };

    fn wide(value: &std::ffi::OsStr) -> Vec<u16> {
        value.encode_wide().chain(std::iter::once(0)).collect()
    }
    fn quote(value: &str) -> String {
        if value
            .bytes()
            .all(|byte| !byte.is_ascii_whitespace() && byte != b'"')
        {
            return value.to_string();
        }
        format!("\"{}\"", value.replace('"', "\\\""))
    }

    let file = wide(executable.as_os_str());
    let directory = wide(
        executable
            .parent()
            .unwrap_or_else(|| Path::new("."))
            .as_os_str(),
    );
    let parameters = [
        "-instance",
        SERVICE_INSTANCE,
        "-install-service",
        "-install-service-pipe-name",
        SERVICE_PIPE,
        "-install-service-security-descriptor",
        SERVICE_SECURITY_DESCRIPTOR,
    ]
    .iter()
    .map(|value| quote(value))
    .collect::<Vec<_>>()
    .join(" ");
    let parameters = wide(std::ffi::OsStr::new(&parameters));
    let verb = wide(std::ffi::OsStr::new("runas"));
    let mut info: SHELLEXECUTEINFOW = unsafe { mem::zeroed() };
    info.cbSize = mem::size_of::<SHELLEXECUTEINFOW>() as u32;
    info.fMask = SEE_MASK_NOCLOSEPROCESS;
    info.lpVerb = verb.as_ptr();
    info.lpFile = file.as_ptr();
    info.lpParameters = parameters.as_ptr();
    info.lpDirectory = directory.as_ptr();
    info.nShow = SW_HIDE;
    let ok = unsafe { ShellExecuteExW(&mut info) };
    if ok == 0 {
        let code = unsafe { GetLastError() };
        return Err(if code == 1223 {
            EverythingError::unavailable("Everything 服务需要管理员授权，授权已取消。")
        } else {
            EverythingError::unavailable(format!("Everything 服务授权失败（错误 {code}）。"))
        });
    }
    let process: HANDLE = info.hProcess;
    if process.is_null() {
        return Err(EverythingError::unavailable(
            "Everything 服务授权进程句柄不可用。",
        ));
    }
    let wait = unsafe { WaitForSingleObject(process, 30_000) };
    if wait != WAIT_OBJECT_0 {
        unsafe { CloseHandle(process) };
        return Err(EverythingError::unavailable(
            "Everything 服务安装响应超时。",
        ));
    }
    let mut exit_code = 1_u32;
    let _ = unsafe { GetExitCodeProcess(process, &mut exit_code) };
    unsafe { CloseHandle(process) };
    if exit_code != 0 {
        return Err(EverythingError::unavailable(format!(
            "Everything 服务安装失败（退出码 {exit_code}）。"
        )));
    }
    Ok(())
}

#[cfg(not(windows))]
fn install_service_elevated(_executable: &Path) -> Result<(), EverythingError> {
    Err(EverythingError::unavailable(
        "Everything 服务仅支持 Windows。",
    ))
}

#[cfg(windows)]
fn copy_text_to_clipboard(value: &str) -> io::Result<()> {
    use std::{mem, ptr};
    use windows_sys::Win32::{
        Foundation::{GetLastError, GlobalFree, HANDLE},
        System::{
            DataExchange::{CloseClipboard, EmptyClipboard, OpenClipboard, SetClipboardData},
            Memory::{GlobalAlloc, GlobalLock, GlobalUnlock, GMEM_MOVEABLE},
        },
    };
    const CF_UNICODETEXT: u32 = 13;
    let mut wide = value.encode_utf16().collect::<Vec<_>>();
    wide.push(0);
    let bytes = wide.len() * mem::size_of::<u16>();
    let opened = (0..8).any(|attempt| {
        if unsafe { OpenClipboard(ptr::null_mut()) } != 0 {
            true
        } else {
            if attempt < 7 {
                thread::sleep(Duration::from_millis(20));
            }
            false
        }
    });
    if !opened {
        return Err(io::Error::from_raw_os_error(
            unsafe { GetLastError() } as i32
        ));
    }
    let result = (|| unsafe {
        if EmptyClipboard() == 0 {
            return Err(io::Error::from_raw_os_error(GetLastError() as i32));
        }
        let memory: HANDLE = GlobalAlloc(GMEM_MOVEABLE, bytes);
        if memory.is_null() {
            return Err(io::Error::from_raw_os_error(GetLastError() as i32));
        }
        let target = GlobalLock(memory) as *mut u16;
        if target.is_null() {
            GlobalFree(memory);
            return Err(io::Error::from_raw_os_error(GetLastError() as i32));
        }
        ptr::copy_nonoverlapping(wide.as_ptr(), target, wide.len());
        let _ = GlobalUnlock(memory);
        if SetClipboardData(CF_UNICODETEXT, memory).is_null() {
            GlobalFree(memory);
            return Err(io::Error::from_raw_os_error(GetLastError() as i32));
        }
        Ok(())
    })();
    unsafe { CloseClipboard() };
    result
}

#[cfg(not(windows))]
fn copy_text_to_clipboard(_value: &str) -> io::Result<()> {
    Err(io::Error::new(
        io::ErrorKind::Unsupported,
        "clipboard is only available on Windows",
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_everything_prefixes_without_mixing_normal_mode() {
        assert_eq!(
            parse_search_mode("minecraft"),
            (EverythingSearchMode::All, "minecraft".into())
        );
        assert_eq!(
            parse_search_mode("/e minecraft"),
            (EverythingSearchMode::All, "minecraft".into())
        );
        assert_eq!(
            parse_search_mode("/e:f *.exe"),
            (EverythingSearchMode::File, "*.exe".into())
        );
        assert_eq!(
            parse_search_mode("/E:D minecraft"),
            (EverythingSearchMode::Directory, "minecraft".into())
        );
        assert_eq!(
            parse_search_mode("/e:f\t*.exe"),
            (EverythingSearchMode::File, "*.exe".into())
        );
        assert_eq!(
            parse_search_mode("/E:d  folder"),
            (EverythingSearchMode::Directory, " folder".into())
        );
        assert_eq!(
            parse_search_mode("/evil"),
            (EverythingSearchMode::All, "/evil".into())
        );
    }

    #[test]
    fn export_parser_is_bounded_and_does_not_accept_relative_paths() {
        let parsed = parse_export("\u{feff}C:\\one.txt\r\nrelative.txt\nC:\\one.txt\n".as_bytes());
        assert_eq!(parsed, vec![PathBuf::from("C:\\one.txt")]);
    }

    #[test]
    fn export_decoder_handles_utf16_and_utf8_bom() {
        let utf16 = "C:\\资料\\报告.xlsx\r\n"
            .encode_utf16()
            .flat_map(u16::to_le_bytes)
            .collect::<Vec<_>>();
        let mut utf16_bom = vec![0xFF, 0xFE];
        utf16_bom.extend(utf16);
        assert_eq!(decode_export_text(&utf16_bom), "C:\\资料\\报告.xlsx\r\n");

        let mut utf8_bom = vec![0xEF, 0xBB, 0xBF];
        utf8_bom.extend_from_slice("C:\\资料\\报告.xlsx\r\n".as_bytes());
        assert_eq!(decode_export_text(&utf8_bom), "C:\\资料\\报告.xlsx\r\n");
    }

    #[test]
    fn export_decoder_recognizes_bomless_utf16() {
        let bytes = "C:\\资料\\报告.xlsx\n"
            .encode_utf16()
            .flat_map(u16::to_le_bytes)
            .collect::<Vec<_>>();
        assert_eq!(decode_export_text(&bytes), "C:\\资料\\报告.xlsx\n");
    }

    #[test]
    fn query_validation_rejects_control_characters_and_large_payloads() {
        assert!(validate_query("a\n b").is_err());
        assert!(validate_query(&"x".repeat(MAX_QUERY_BYTES + 1)).is_err());
        assert!(validate_query("file:*.exe").is_ok());
    }

    #[test]
    fn result_ids_are_opaque_tokens() {
        assert!(valid_result_id("qer-AB12-123-1"));
        assert!(!valid_result_id("C:\\secret.txt"));
        assert!(!valid_result_id("../result"));
    }
}
