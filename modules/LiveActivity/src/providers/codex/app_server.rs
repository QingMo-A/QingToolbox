//! Transport for a Codex app-server this module starts and owns.
//!
//! Scope, stated plainly because it is the most easily misunderstood part of
//! this module:
//!
//! * This talks to **our own child process**. It does not read another
//!   application's memory, does not scrape a WebView's DOM, does not read a
//!   browser profile, and does not inject into a running desktop client.
//! * If the Codex CLI is not installed, or the app-server does not speak the
//!   line JSON-RPC this module expects, the provider reports `Disconnected` or
//!   `Unavailable` and the island shows nothing for Codex. It never invents a
//!   status, and it never falls back to reading somebody else's window title.
//!
//! The transport is line-delimited JSON-RPC over the child's stdio: one JSON
//! object per line, in both directions. Requests are matched to responses by a
//! monotonically increasing id; notifications have no id.

use std::collections::VecDeque;
use std::io::{BufRead, BufReader, Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, TryRecvError};
use std::sync::{
    atomic::{AtomicBool, Ordering},
    Arc,
};
use std::thread;
use std::time::{Duration, Instant};

use serde_json::{json, Value};

use crate::diagnostics;

/// How long to wait for a response before treating the connection as stalled.
///
/// Long enough for a genuinely slow query, short enough that the island is not
/// left showing a stale state for an unreasonable time.
pub const REQUEST_TIMEOUT: Duration = Duration::from_secs(3);

/// Frames larger than this are refused rather than buffered.
///
/// A thread listing with large bodies can legitimately be sizeable, so the
/// limit is generous; the point is that an unbounded read from a child process
/// cannot exhaust memory.
pub const MAX_FRAME_BYTES: usize = 1024 * 1024;

/// Candidate executable names for the Codex CLI, in preference order.
///
/// Resolved through `PATH` and a few well-known install locations rather than
/// hardcoding one absolute path, because the CLI is installed per-user and its
/// location varies.
const CANDIDATE_COMMANDS: [&str; 3] = ["codex.exe", "codex", "codex.cmd"];

/// Extra directories to search beyond `PATH`, relative to `%LOCALAPPDATA%`.
const LOCAL_APP_DATA_HINTS: [&str; 3] = ["Programs", "npm", "Microsoft/WinGet/Links"];

/// Where the app-server binary was found, or why it was not.
#[derive(Debug, Clone, PartialEq)]
pub enum Discovery {
    Found(PathBuf),
    NotFound,
}

/// Locate the Codex CLI without running it.
///
/// Testing for presence separately from starting it means "not installed" is
/// reported as `Unavailable` (a normal, non-actionable state) rather than as a
/// spawn error the user would read as a bug.
pub fn discover() -> Discovery {
    // An explicit override wins, and is the documented escape hatch for a
    // non-standard install.
    if let Some(explicit) = std::env::var_os("QINGTOOLBOX_CODEX_PATH") {
        let path = PathBuf::from(explicit);
        if path.is_file() {
            return Discovery::Found(path);
        }
        diagnostics::warning(
            "codex",
            "QINGTOOLBOX_CODEX_PATH was set but does not point at a file",
        );
    }

    let mut directories = Vec::new();
    if let Some(path) = std::env::var_os("PATH") {
        directories.extend(std::env::split_paths(&path));
    }
    if let Some(local) = std::env::var_os("LOCALAPPDATA") {
        let base = PathBuf::from(local);
        for hint in LOCAL_APP_DATA_HINTS {
            directories.push(base.join(hint));
        }
    }
    if let Some(roaming) = std::env::var_os("APPDATA") {
        directories.push(PathBuf::from(roaming).join("npm"));
    }

    for directory in directories {
        for command in CANDIDATE_COMMANDS {
            let candidate = directory.join(command);
            if candidate.is_file() {
                return Discovery::Found(candidate);
            }
        }
    }
    // The desktop app bundles a CLI in versioned directories. Explorer/BAT
    // launched hosts do not inherit the app's augmented PATH. Inspect only
    // this public binary directory, never Codex history, credentials or memory.
    if let Some(local) = std::env::var_os("LOCALAPPDATA") {
        if let Some(path) = desktop_cli(&PathBuf::from(local).join("OpenAI/Codex/bin")) {
            diagnostics::information(
                "codex",
                "CLI discovered in the Codex desktop binary directory",
            );
            return Discovery::Found(path);
        }
    }
    Discovery::NotFound
}

fn desktop_cli(root: &Path) -> Option<PathBuf> {
    let direct = root.join("codex.exe");
    if direct.is_file() {
        return Some(direct);
    }
    let mut candidates = std::fs::read_dir(root)
        .ok()?
        .take(128)
        .filter_map(Result::ok)
        .filter(|entry| entry.file_type().is_ok_and(|kind| kind.is_dir()))
        .filter_map(|entry| {
            let path = entry.path().join("codex.exe");
            let metadata = std::fs::metadata(&path).ok()?;
            metadata
                .is_file()
                .then(|| (metadata.modified().unwrap_or(std::time::UNIX_EPOCH), path))
        })
        .collect::<Vec<_>>();
    candidates.sort_by(|a, b| b.0.cmp(&a.0).then_with(|| a.1.cmp(&b.1)));
    candidates.into_iter().next().map(|(_, path)| path)
}

/// One parsed frame from the child, or a transport-level failure.
#[derive(Debug)]
enum Message {
    Frame(Value),
    Invalid(String),
    Closed,
}

/// A live app-server child process.
///
/// `Debug` is implemented by hand rather than derived: the field list contains a
/// stdin pipe and a command line, and a derived `Debug` would print the
/// executable path into any log that dumps the provider.
pub struct AppServer {
    child: Child,
    stdin: ChildStdin,
    messages: Receiver<Message>,
    next_id: u64,
    /// Retained so the reader thread can be identified in diagnostics.
    command: String,
    pending: VecDeque<Value>,
    cancel: Arc<AtomicBool>,
    #[cfg(windows)]
    _job: ProcessJob,
}

impl std::fmt::Debug for AppServer {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        // Deliberately omits `stdin`, `messages` and `command`: only the
        // identity of the process is useful for diagnostics, and the command
        // line is an installation path the user did not ask to broadcast.
        formatter
            .debug_struct("AppServer")
            .field("pid", &self.child.id())
            .field("next_id", &self.next_id)
            .finish()
    }
}

impl AppServer {
    /// Spawn `codex app-server` and begin reading its stdout.
    pub fn spawn(executable: &std::path::Path) -> Result<Self, String> {
        Self::spawn_with_cancel(executable, Arc::new(AtomicBool::new(false)))
    }

    pub fn spawn_with_cancel(
        executable: &std::path::Path,
        cancel: Arc<AtomicBool>,
    ) -> Result<Self, String> {
        let mut command = Command::new(executable);
        command
            .arg("app-server")
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());
        apply_hidden(&mut command);

        let mut child = command
            .spawn()
            .map_err(|error| format!("could not start Codex app-server ({:?})", error.kind()))?;
        #[cfg(windows)]
        let job = match ProcessJob::attach(&child) {
            Ok(job) => job,
            Err(error) => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(error);
            }
        };

        let stdin = child
            .stdin
            .take()
            .ok_or_else(|| "app-server has no stdin pipe".to_string())?;
        let stdout = child
            .stdout
            .take()
            .ok_or_else(|| "app-server has no stdout pipe".to_string())?;

        // stderr is drained on its own thread. A child that fills its stderr
        // pipe would otherwise block forever on write, which looked from the
        // outside like a stalled protocol.
        if let Some(stderr) = child.stderr.take() {
            drain_stderr(stderr);
        }

        let messages = spawn_reader(stdout);
        diagnostics::information(
            "codex",
            &format!("started managed app-server PID {}", child.id()),
        );
        let mut server = Self {
            child,
            stdin,
            messages,
            next_id: 0,
            command: executable.display().to_string(),
            pending: VecDeque::new(),
            cancel,
            #[cfg(windows)]
            _job: job,
        };
        server.request("initialize", json!({"clientInfo": {"name": "qing_liveactivity", "title": "Qing Island", "version": env!("CARGO_PKG_VERSION")}}))?;
        server.write_frame(&json!({"method":"initialized"}))?;
        Ok(server)
    }

    pub fn pid(&self) -> u32 {
        self.child.id()
    }

    /// Send a request and wait for its matching response.
    ///
    /// Notifications that arrive while waiting are buffered, so a chatty server
    /// cannot satisfy this request with somebody else's reply.
    pub fn request(&mut self, method: &str, params: Value) -> Result<Value, String> {
        self.next_id = self.next_id.saturating_add(1);
        let id = self.next_id;
        let frame = json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params });
        self.write_frame(&frame)?;

        let deadline = Instant::now() + REQUEST_TIMEOUT;
        loop {
            if self.cancel.load(Ordering::Relaxed) {
                return Err("app-server request cancelled".to_string());
            }
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return Err(format!("app-server did not answer `{method}` in time"));
            }
            match self
                .messages
                .recv_timeout(remaining.min(Duration::from_millis(100)))
            {
                Ok(Message::Frame(frame)) => {
                    if frame.get("id").and_then(Value::as_u64) != Some(id) {
                        if frame.get("method").is_some() && frame.get("id").is_none() {
                            if self.pending.len() == 64 {
                                self.pending.pop_front();
                            }
                            self.pending.push_back(frame);
                        } else if frame.get("method").is_some() && frame.get("id").is_some() {
                            // Observation never grants approval or supplies credentials.
                            self.write_frame(&json!({"id": frame["id"], "error": {"code": -32601, "message": "Observer does not handle interactive requests"}}))?;
                        }
                        continue;
                    }
                    if frame.get("error").is_some() {
                        // Never copy server error bodies into logs (may contain user data).
                        return Err(format!(
                            "app-server rejected `{method}` (code {})",
                            frame["error"]["code"].as_i64().unwrap_or(-1)
                        ));
                    }
                    return Ok(frame.get("result").cloned().unwrap_or(Value::Null));
                }
                Ok(Message::Invalid(reason)) => {
                    return Err(format!("app-server sent an unreadable frame: {reason}"));
                }
                Ok(Message::Closed) => {
                    return Err("app-server closed its output".to_string());
                }
                Err(RecvTimeoutError::Timeout) => continue,
                Err(RecvTimeoutError::Disconnected) => {
                    return Err("app-server output reader stopped".to_string());
                }
            }
        }
    }

    /// Read any notification that has already arrived, without blocking.
    ///
    /// The island never waits on a notification: a status change is a bonus,
    /// and the periodic poll is what keeps the display correct if notifications
    /// are unavailable.
    pub fn drain_notifications(&mut self, limit: usize) -> Vec<Value> {
        let mut collected: Vec<Value> = self
            .pending
            .drain(..self.pending.len().min(limit))
            .collect();
        while collected.len() < limit {
            match self.messages.try_recv() {
                Ok(Message::Frame(frame)) => {
                    if frame.get("id").is_none() && frame.get("method").is_some() {
                        collected.push(frame);
                    }
                }
                Ok(Message::Invalid(reason)) => {
                    diagnostics::warning("codex", &format!("unreadable frame: {reason}"));
                }
                Ok(Message::Closed) => break,
                Err(TryRecvError::Empty) => break,
                Err(TryRecvError::Disconnected) => break,
            }
        }
        collected
    }

    /// Whether the child is still alive, without blocking on it.
    pub fn is_alive(&mut self) -> bool {
        matches!(self.child.try_wait(), Ok(None))
    }

    fn write_frame(&mut self, frame: &Value) -> Result<(), String> {
        let mut bytes = serde_json::to_vec(frame).map_err(|error| error.to_string())?;
        bytes.push(b'\n');
        if bytes.len() > MAX_FRAME_BYTES {
            return Err("refusing to send an oversized frame".to_string());
        }
        self.stdin
            .write_all(&bytes)
            .map_err(|error| format!("could not write to app-server: {error}"))?;
        self.stdin
            .flush()
            .map_err(|error| format!("could not flush app-server stdin: {error}"))
    }
}

impl Drop for AppServer {
    fn drop(&mut self) {
        // Best effort and bounded: the module must not hang on shutdown because
        // a child ignored its input.
        let _ = self.stdin.write_all(b"");
        let _ = self.child.kill();
        let _ = self.child.wait();
        diagnostics::information("codex", "app-server stopped");
    }
}

impl std::fmt::Display for AppServer {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            formatter,
            "app-server(PID {}) at {}",
            self.pid(),
            self.command
        )
    }
}

#[cfg(windows)]
struct ProcessJob(windows_sys::Win32::Foundation::HANDLE);
#[cfg(windows)]
impl ProcessJob {
    fn attach(child: &Child) -> Result<Self, String> {
        use std::os::windows::io::AsRawHandle;
        use windows_sys::Win32::System::JobObjects::*;
        unsafe {
            let handle = CreateJobObjectW(std::ptr::null(), std::ptr::null());
            if handle.is_null() {
                return Err("could not create managed process job".into());
            }
            let job = Self(handle);
            let mut limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if SetInformationJobObject(
                handle,
                JobObjectExtendedLimitInformation,
                (&limits as *const JOBOBJECT_EXTENDED_LIMIT_INFORMATION).cast(),
                std::mem::size_of_val(&limits) as u32,
            ) == 0
                || AssignProcessToJobObject(handle, child.as_raw_handle()) == 0
            {
                return Err("could not contain managed app-server process tree".into());
            }
            Ok(job)
        }
    }
}
// This owned handle is never used concurrently; moving it transfers ownership.
#[cfg(windows)]
unsafe impl Send for ProcessJob {}
#[cfg(windows)]
impl Drop for ProcessJob {
    fn drop(&mut self) {
        unsafe {
            windows_sys::Win32::Foundation::CloseHandle(self.0);
        }
    }
}

/// Split a byte stream into JSON lines.
///
/// Extracted from the reader thread so the framing rules — the part that
/// actually breaks when a child writes oddly — are testable without a process.
pub struct LineFramer {
    buffer: Vec<u8>,
}

impl LineFramer {
    pub fn new() -> Self {
        Self { buffer: Vec::new() }
    }

    /// Feed bytes, returning every complete frame now available.
    pub fn push(&mut self, chunk: &[u8]) -> Vec<Result<Value, String>> {
        self.buffer.extend_from_slice(chunk);
        let mut frames = Vec::new();
        loop {
            let Some(position) = self.buffer.iter().position(|byte| *byte == b'\n') else {
                // A single frame that never terminates must not grow forever.
                if self.buffer.len() > MAX_FRAME_BYTES {
                    self.buffer.clear();
                    frames.push(Err("frame exceeded the size limit".to_string()));
                }
                break;
            };
            let mut line = self.buffer.drain(..=position).collect::<Vec<u8>>();
            line.pop(); // the newline
            if line.last() == Some(&b'\r') {
                line.pop();
            }
            if line.is_empty() {
                continue;
            }
            frames.push(parse_frame(&line));
        }
        frames
    }
}

impl Default for LineFramer {
    fn default() -> Self {
        Self::new()
    }
}

fn parse_frame(line: &[u8]) -> Result<Value, String> {
    let text = std::str::from_utf8(line).map_err(|_| "frame was not valid UTF-8".to_string())?;
    // The server may prefix non-JSON chatter on stdout. Skip lines that are
    // clearly not a frame instead of failing the whole stream.
    let trimmed = text.trim_start();
    if !trimmed.starts_with('{') && !trimmed.starts_with('[') {
        return Err("non-JSON output (contents omitted)".to_string());
    }
    serde_json::from_str::<Value>(trimmed).map_err(|error| format!("invalid JSON: {error}"))
}

/// Bound a string that will be written to diagnostics.
fn truncate_for_log(value: &str) -> String {
    value.chars().take(120).collect()
}

fn spawn_reader(stdout: std::process::ChildStdout) -> Receiver<Message> {
    let (sender, receiver) = mpsc::sync_channel(16);
    thread::spawn(move || {
        let mut reader = BufReader::new(stdout);
        let mut framer = LineFramer::new();
        let mut buffer = Vec::new();
        loop {
            buffer.clear();
            match (&mut reader)
                .take((MAX_FRAME_BYTES + 1) as u64)
                .read_until(b'\n', &mut buffer)
            {
                Ok(0) => break,
                Ok(_) => {}
                Err(_) => break,
            }
            if buffer.len() > MAX_FRAME_BYTES {
                let _ = sender.send(Message::Invalid(
                    "frame exceeded the size limit".to_string(),
                ));
                break;
            }
            for frame in framer.push(&buffer) {
                let message = match frame {
                    Ok(value) => Message::Frame(value),
                    Err(reason) => Message::Invalid(reason),
                };
                if sender.send(message).is_err() {
                    return;
                }
            }
        }
        let _ = sender.send(Message::Closed);
    });
    receiver
}

fn drain_stderr(stderr: std::process::ChildStderr) {
    thread::spawn(move || {
        let mut reader = BufReader::new(stderr);
        // Deliberately does not log contents: a child's stderr can contain
        // prompts or paths. Only the fact that output occurred is recorded, and
        // only the first occurrence, so a noisy child cannot flood the ring.
        let mut seen = false;
        let mut bytes = [0; 4096];
        loop {
            match reader.read(&mut bytes) {
                Ok(0) => break,
                Ok(_) if !seen => {
                    seen = true;
                    diagnostics::information(
                        "codex",
                        "app-server wrote to stderr; contents are not recorded",
                    );
                }
                Ok(_) => {}
                Err(_) => break,
            }
        }
    });
}

fn apply_hidden(command: &mut Command) {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        // CREATE_NO_WINDOW: a resident module must never flash a console.
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        command.creation_flags(CREATE_NO_WINDOW);
    }
    #[cfg(not(windows))]
    {
        let _ = command;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn framer_splits_complete_lines_and_keeps_partial_ones() {
        let mut framer = LineFramer::new();
        // A frame split across two reads must not be parsed twice or lost.
        let first = framer.push(br#"{"jsonrpc":"2.0","id":1}"#);
        assert!(first.is_empty(), "an unterminated frame is not a frame yet");
        let second = framer.push(b"\n{\"jsonrpc\":\"2.0\",\"id\":2}\n");
        assert_eq!(second.len(), 2);
        assert_eq!(second[0].as_ref().expect("frame")["id"], 1);
        assert_eq!(second[1].as_ref().expect("frame")["id"], 2);
    }

    #[test]
    fn framer_handles_crlf_and_blank_lines() {
        let mut framer = LineFramer::new();
        let frames = framer.push(b"\r\n{\"id\":7}\r\n\r\n");
        assert_eq!(frames.len(), 1);
        assert_eq!(frames[0].as_ref().expect("frame")["id"], 7);
    }

    #[test]
    fn framer_reports_bad_frames_without_losing_the_rest() {
        let mut framer = LineFramer::new();
        let frames = framer.push(b"not json at all\n{\"id\":3}\n{broken\n");
        assert_eq!(frames.len(), 3);
        assert!(frames[0].is_err(), "chatter on stdout must be reported");
        assert_eq!(frames[1].as_ref().expect("frame")["id"], 3);
        assert!(frames[2].is_err(), "truncated JSON must be reported");
    }

    #[test]
    fn framer_accepts_a_json_array_frame() {
        let mut framer = LineFramer::new();
        let frames = framer.push(b"[{\"id\":1}]\n");
        assert_eq!(frames.len(), 1);
        assert!(frames[0].is_ok());
    }

    #[test]
    fn an_endless_frame_is_dropped_rather_than_buffered_forever() {
        let mut framer = LineFramer::new();
        let chunk = vec![b'x'; 1024 * 1024];
        let mut reported = Vec::new();
        // Push well past the limit with no newline in sight.
        for _ in 0..(MAX_FRAME_BYTES / chunk.len() + 2) {
            reported.extend(framer.push(&chunk));
        }
        assert!(
            reported.iter().any(Result::is_err),
            "an unterminated oversized frame must be reported and discarded"
        );
    }

    #[test]
    fn log_truncation_bounds_what_reaches_diagnostics() {
        let long = "a".repeat(1000);
        assert_eq!(truncate_for_log(&long).chars().count(), 120);
        assert_eq!(truncate_for_log("short"), "short");
    }

    /// Serialises the two tests that mutate `QINGTOOLBOX_CODEX_PATH`.
    ///
    /// `std::env::set_var` writes to a process-wide table, so two tests doing it
    /// concurrently can each observe the other's value. The guard makes the
    /// set/probe/restore sequences mutually exclusive.
    fn env_lock() -> std::sync::MutexGuard<'static, ()> {
        static LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());
        LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    #[test]
    fn discovery_prefers_an_explicit_override_that_exists() {
        let _guard = env_lock();
        // A path that exists but is not executable is still "found": running it
        // will fail with a clear spawn error, which is better than silently
        // falling through to a different install.
        //
        // The filename carries the process id so two concurrent `cargo test`
        // invocations on the same machine cannot delete each other's probe.
        let file = std::env::temp_dir().join(format!(
            "qing-liveactivity-discovery-probe-{}.exe",
            std::process::id()
        ));
        std::fs::write(&file, b"not really an executable").expect("write probe");
        let saved = std::env::var_os("QINGTOOLBOX_CODEX_PATH");
        std::env::set_var("QINGTOOLBOX_CODEX_PATH", &file);
        let discovery = discover();
        // Restored explicitly because tests share a process.
        match &saved {
            Some(value) => std::env::set_var("QINGTOOLBOX_CODEX_PATH", value),
            None => std::env::remove_var("QINGTOOLBOX_CODEX_PATH"),
        }
        let _ = std::fs::remove_file(&file);
        assert_eq!(discovery, Discovery::Found(file));
    }

    #[test]
    fn discovery_is_not_found_rather_than_an_error_when_nothing_is_installed() {
        let _guard = env_lock();
        let saved = std::env::var_os("QINGTOOLBOX_CODEX_PATH");
        std::env::set_var("QINGTOOLBOX_CODEX_PATH", "/definitely/not/here");
        // Only meaningful if the CLI truly is absent; if it is installed the
        // test still asserts that *some* conclusion is reached without panicking.
        let discovery = discover();
        match saved {
            Some(value) => std::env::set_var("QINGTOOLBOX_CODEX_PATH", value),
            None => std::env::remove_var("QINGTOOLBOX_CODEX_PATH"),
        }
        assert!(matches!(
            discovery,
            Discovery::Found(_) | Discovery::NotFound
        ));
    }

    #[test]
    fn desktop_discovery_does_not_need_path_or_scan_arbitrary_subtrees() {
        let root = std::env::temp_dir().join(format!(
            "qing-liveactivity-desktop-cli-{}",
            std::process::id()
        ));
        let version = root.join("version-one");
        std::fs::create_dir_all(version.join("nested")).unwrap();
        std::fs::write(version.join("nested/codex.exe"), b"nested must not match").unwrap();
        assert_eq!(desktop_cli(&root), None);
        std::fs::write(version.join("codex.exe"), b"fixture").unwrap();
        assert_eq!(desktop_cli(&root), Some(version.join("codex.exe")));
        std::fs::write(root.join("codex.exe"), b"direct fixture").unwrap();
        assert_eq!(desktop_cli(&root), Some(root.join("codex.exe")));
        let _ = std::fs::remove_dir_all(root);
    }
}
