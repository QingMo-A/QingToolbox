#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

//! Native PowerGuard module.
//!
//! Monitoring, settings and the shutdown decision live in this process.  The
//! WebView only receives a typed snapshot; it cannot provide a command line,
//! arbitrary process path, or an unbounded network target.  The default
//! configuration is intentionally disabled and an immediate shutdown request
//! requires an explicit backend confirmation token.

use std::{
    env, fs,
    io::{self, BufRead, BufReader, BufWriter, Write},
    net::{SocketAddr, TcpStream},
    path::{Path, PathBuf},
    process::Command,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Mutex,
    },
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

const HOST_PROTOCOL_VERSION: u16 = 1;
const HOST_MAX_FRAME_BYTES: usize = 1024 * 1024;
const PROBE_INTERVAL: Duration = Duration::from_secs(5);
const PROBE_TIMEOUT: Duration = Duration::from_millis(1200);
const TEST_COUNTDOWN: Duration = Duration::from_secs(30);
const EXTENSION: Duration = Duration::from_secs(600);

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct HostEnvelope {
    protocol_version: u16,
    message_type: String,
    request_id: String,
    payload: Value,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct HostResponse<'a> {
    protocol_version: u16,
    message_type: &'a str,
    request_id: &'a str,
    payload: Value,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<HostErrorBody>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct HostErrorBody {
    code: &'static str,
    message: String,
}

#[derive(Debug, Clone)]
struct ModuleError {
    code: &'static str,
    message: String,
}

impl ModuleError {
    fn new(code: &'static str, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Settings {
    guard_enabled: bool,
    startup_grace_seconds: u64,
    offline_confirmation_seconds: u64,
    shutdown_countdown_seconds: u64,
    recovery_confirmation_seconds: u64,
    show_recovery_notification: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            // A safety-first migration default. The user must explicitly turn
            // the guard on after reviewing the outage policy.
            guard_enabled: false,
            startup_grace_seconds: 120,
            offline_confirmation_seconds: 60,
            shutdown_countdown_seconds: 600,
            recovery_confirmation_seconds: 30,
            show_recovery_notification: true,
        }
    }
}

impl Settings {
    fn validate(&self) -> Result<(), ModuleError> {
        if self.startup_grace_seconds > 600
            || !(15..=300).contains(&self.offline_confirmation_seconds)
            || !(60..=3600).contains(&self.shutdown_countdown_seconds)
            || !(5..=120).contains(&self.recovery_confirmation_seconds)
        {
            return Err(ModuleError::new("settings_invalid", "设置超出允许范围。"));
        }
        Ok(())
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum GuardState {
    Disabled,
    StartupGrace,
    Online,
    SuspectedOffline,
    Countdown,
    Suppressed,
    Recovering,
    ExecutingShutdown,
    ActionFailed,
}

impl GuardState {
    const fn as_str(self) -> &'static str {
        match self {
            Self::Disabled => "disabled",
            Self::StartupGrace => "startupGrace",
            Self::Online => "online",
            Self::SuspectedOffline => "suspectedOffline",
            Self::Countdown => "countdown",
            Self::Suppressed => "suppressed",
            Self::Recovering => "recovering",
            Self::ExecutingShutdown => "executingShutdown",
            Self::ActionFailed => "actionFailed",
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct EndpointResult {
    name: &'static str,
    succeeded: bool,
    elapsed_millis: u128,
    failure_category: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct Event {
    timestamp_epoch_millis: u64,
    kind: String,
    detail: Option<String>,
}

#[derive(Debug)]
struct Inner {
    settings: Settings,
    state: GuardState,
    is_online: bool,
    last_probe_epoch_millis: Option<u64>,
    last_successful_probe_epoch_millis: Option<u64>,
    consecutive_probe_failures: u32,
    last_probe: Vec<EndpointResult>,
    events: Vec<Event>,
    started_at: Instant,
    offline_since: Option<Instant>,
    recovery_since: Option<Instant>,
    countdown_deadline: Option<Instant>,
    shutdown_attempted: bool,
    test_deadline: Option<Instant>,
    last_probe_at: Option<Instant>,
    suppressed: bool,
}

#[derive(Clone, Debug)]
struct PowerApp {
    inner: Arc<Mutex<Inner>>,
    data_dir: Option<PathBuf>,
    stop: Arc<AtomicBool>,
}

impl PowerApp {
    fn new() -> Self {
        let data_dir = env::var_os("QINGTOOLBOX_MODULE_DATA_DIR").map(PathBuf::from);
        let settings = data_dir
            .as_deref()
            .and_then(load_settings)
            .unwrap_or_default();
        let state = if settings.guard_enabled {
            GuardState::StartupGrace
        } else {
            GuardState::Disabled
        };
        Self {
            inner: Arc::new(Mutex::new(Inner {
                settings,
                state,
                is_online: false,
                last_probe_epoch_millis: None,
                last_successful_probe_epoch_millis: None,
                consecutive_probe_failures: 0,
                last_probe: Vec::new(),
                events: Vec::new(),
                started_at: Instant::now(),
                offline_since: None,
                recovery_since: None,
                countdown_deadline: None,
                shutdown_attempted: false,
                test_deadline: None,
                last_probe_at: None,
                suppressed: false,
            })),
            data_dir,
            stop: Arc::new(AtomicBool::new(false)),
        }
    }

    fn start_monitor(&self) -> thread::JoinHandle<()> {
        let app = self.clone();
        thread::spawn(move || {
            while !app.stop.load(Ordering::Acquire) {
                app.tick();
                thread::sleep(Duration::from_millis(250));
            }
        })
    }

    fn tick(&self) {
        let now = Instant::now();
        let should_probe = {
            let mut inner = self.lock();
            if let Some(deadline) = inner.test_deadline {
                if deadline <= now {
                    inner.test_deadline = None;
                    self.record_locked(&mut inner, "TestCompleted", None);
                }
            }
            if inner.state == GuardState::Countdown
                && inner
                    .countdown_deadline
                    .is_some_and(|deadline| deadline <= now)
            {
                inner.countdown_deadline = None;
                inner.state = GuardState::ExecutingShutdown;
                self.record_locked(&mut inner, "ShutdownRequested", Some("automatic"));
                // Do not probe while executing the action.
                false
            } else if !inner.settings.guard_enabled
                || matches!(
                    inner.state,
                    GuardState::Disabled | GuardState::ExecutingShutdown
                )
                || (inner.state == GuardState::StartupGrace
                    && now.duration_since(inner.started_at)
                        < Duration::from_secs(inner.settings.startup_grace_seconds))
            {
                false
            } else {
                inner
                    .last_probe_at
                    .map_or(true, |last| now.duration_since(last) >= PROBE_INTERVAL)
            }
        };

        // If the countdown reached zero, execute outside the state lock.
        let execute = {
            let inner = self.lock();
            inner.state == GuardState::ExecutingShutdown
                && inner.countdown_deadline.is_none()
                && inner.settings.guard_enabled
                && !inner.shutdown_attempted
        };
        if execute {
            self.lock().shutdown_attempted = true;
            if let Err(message) = request_system_shutdown() {
                let mut inner = self.lock();
                inner.state = GuardState::ActionFailed;
                self.record_locked(&mut inner, "ShutdownFailed", Some(&message));
            }
            return;
        }

        if should_probe {
            let result = probe_connectivity();
            self.apply_probe(result);
        }
    }

    fn apply_probe(&self, result: Vec<EndpointResult>) {
        let now = Instant::now();
        let online = result.iter().any(|item| item.succeeded);
        let mut inner = self.lock();
        inner.last_probe_at = Some(now);
        inner.last_probe_epoch_millis = Some(epoch_millis());
        inner.last_probe = result;
        inner.is_online = online;
        if inner.state == GuardState::StartupGrace
            && now.duration_since(inner.started_at)
                < Duration::from_secs(inner.settings.startup_grace_seconds)
        {
            return;
        }
        if online {
            inner.consecutive_probe_failures = 0;
            inner.last_successful_probe_epoch_millis = inner.last_probe_epoch_millis;
            match inner.state {
                GuardState::StartupGrace => {
                    inner.state = GuardState::Online;
                    self.record_locked(&mut inner, "ProbeSucceeded", None);
                }
                GuardState::SuspectedOffline | GuardState::Countdown | GuardState::ActionFailed => {
                    inner.state = GuardState::Recovering;
                    inner.recovery_since = Some(now);
                    inner.countdown_deadline = None;
                    inner.shutdown_attempted = false;
                    self.record_locked(&mut inner, "ConnectivityRecovered", Some("confirming"));
                }
                GuardState::Recovering => {
                    if inner.recovery_since.is_some_and(|started| {
                        now.duration_since(started)
                            >= Duration::from_secs(inner.settings.recovery_confirmation_seconds)
                    }) {
                        inner.state = GuardState::Online;
                        inner.recovery_since = None;
                        inner.offline_since = None;
                        inner.suppressed = false;
                        self.record_locked(&mut inner, "ConnectivityRecovered", None);
                    }
                }
                GuardState::Suppressed => {
                    inner.recovery_since.get_or_insert(now);
                    if inner.recovery_since.is_some_and(|started| {
                        now.duration_since(started)
                            >= Duration::from_secs(inner.settings.recovery_confirmation_seconds)
                    }) {
                        inner.state = GuardState::Online;
                        inner.recovery_since = None;
                        inner.offline_since = None;
                        inner.suppressed = false;
                        self.record_locked(&mut inner, "ConnectivityRecovered", None);
                    }
                }
                GuardState::Online => {}
                GuardState::Disabled | GuardState::ExecutingShutdown => {}
            }
        } else {
            inner.consecutive_probe_failures = inner.consecutive_probe_failures.saturating_add(1);
            inner.recovery_since = None;
            if inner.suppressed || inner.state == GuardState::Suppressed {
                inner.state = GuardState::Suppressed;
                inner.suppressed = true;
                return;
            }
            match inner.state {
                GuardState::Online | GuardState::StartupGrace | GuardState::Recovering => {
                    inner.state = GuardState::SuspectedOffline;
                    inner.offline_since = Some(now);
                    self.record_locked(&mut inner, "OfflineSuspected", None);
                }
                GuardState::SuspectedOffline => {
                    if inner.offline_since.is_some_and(|started| {
                        now.duration_since(started)
                            >= Duration::from_secs(inner.settings.offline_confirmation_seconds)
                    }) {
                        inner.state = GuardState::Countdown;
                        inner.shutdown_attempted = false;
                        inner.countdown_deadline = Some(
                            now + Duration::from_secs(inner.settings.shutdown_countdown_seconds),
                        );
                        self.record_locked(&mut inner, "CountdownStarted", None);
                    }
                }
                GuardState::Countdown | GuardState::ActionFailed => {}
                GuardState::Disabled | GuardState::ExecutingShutdown => {}
                GuardState::Suppressed => {}
            }
        }
    }

    fn invoke(&self, method: &str, payload: &Value) -> Result<Value, ModuleError> {
        match method {
            "getState" => Ok(self.snapshot()),
            "setSettings" => {
                let settings: Settings = serde_json::from_value(payload.clone())
                    .map_err(|_| ModuleError::new("settings_invalid", "设置格式无效。"))?;
                settings.validate()?;
                self.persist_settings(&settings)?;
                {
                    let mut inner = self.lock();
                    inner.settings = settings;
                    inner.suppressed = false;
                    inner.offline_since = None;
                    inner.recovery_since = None;
                    inner.countdown_deadline = None;
                    inner.shutdown_attempted = false;
                    inner.state = if inner.settings.guard_enabled {
                        inner.started_at = Instant::now();
                        GuardState::StartupGrace
                    } else {
                        GuardState::Disabled
                    };
                    self.record_locked(&mut inner, "SettingsChanged", None);
                }
                Ok(self.snapshot())
            }
            "probeNow" => {
                let allowed = {
                    let inner = self.lock();
                    !matches!(inner.state, GuardState::ExecutingShutdown)
                };
                if !allowed {
                    return Err(ModuleError::new(
                        "operation_unavailable",
                        "当前正在执行关机。",
                    ));
                }
                self.apply_probe(probe_connectivity());
                Ok(self.snapshot())
            }
            "testWarning" => {
                let mut inner = self.lock();
                if matches!(
                    inner.state,
                    GuardState::Countdown | GuardState::ExecutingShutdown
                ) {
                    return Err(ModuleError::new(
                        "operation_unavailable",
                        "当前已有真实关机倒计时。",
                    ));
                }
                inner.test_deadline = Some(Instant::now() + TEST_COUNTDOWN);
                self.record_locked(&mut inner, "TestStarted", None);
                Ok(self.snapshot_locked(&inner))
            }
            "cancelCurrent" => {
                let mut inner = self.lock();
                if inner.state != GuardState::Countdown {
                    return Err(ModuleError::new(
                        "operation_unavailable",
                        "当前没有可取消的倒计时。",
                    ));
                }
                inner.countdown_deadline = None;
                inner.shutdown_attempted = false;
                inner.state = GuardState::Suppressed;
                inner.suppressed = true;
                self.record_locked(&mut inner, "CountdownCancelled", None);
                Ok(self.snapshot_locked(&inner))
            }
            "rearmCurrent" => {
                let mut inner = self.lock();
                if inner.state != GuardState::Suppressed {
                    return Err(ModuleError::new(
                        "operation_unavailable",
                        "当前没有被抑制的断网。",
                    ));
                }
                inner.suppressed = false;
                inner.state = GuardState::SuspectedOffline;
                inner.offline_since = Some(Instant::now());
                inner.shutdown_attempted = false;
                self.record_locked(&mut inner, "OutageRearmed", None);
                Ok(self.snapshot_locked(&inner))
            }
            "extendCountdown" => {
                let mut inner = self.lock();
                if inner.state != GuardState::Countdown {
                    return Err(ModuleError::new(
                        "operation_unavailable",
                        "当前没有可延长的倒计时。",
                    ));
                }
                if let Some(deadline) = inner.countdown_deadline.as_mut() {
                    *deadline += EXTENSION;
                }
                self.record_locked(&mut inner, "CountdownExtended", None);
                Ok(self.snapshot_locked(&inner))
            }
            "shutdownNow" => {
                let confirmation = payload
                    .get("confirm")
                    .and_then(Value::as_str)
                    .unwrap_or_default();
                if confirmation != "SHUTDOWN" {
                    return Err(ModuleError::new(
                        "confirmation_required",
                        "立即关机需要明确确认。",
                    ));
                }
                {
                    let mut inner = self.lock();
                    if !matches!(
                        inner.state,
                        GuardState::Countdown | GuardState::ActionFailed
                    ) {
                        return Err(ModuleError::new(
                            "operation_unavailable",
                            "当前状态不允许立即关机。",
                        ));
                    }
                    inner.state = GuardState::ExecutingShutdown;
                    inner.countdown_deadline = None;
                    inner.shutdown_attempted = true;
                    self.record_locked(&mut inner, "ShutdownRequested", Some("manual"));
                }
                if let Err(message) = request_system_shutdown() {
                    let mut inner = self.lock();
                    inner.state = GuardState::ActionFailed;
                    self.record_locked(&mut inner, "ShutdownFailed", Some(&message));
                    return Err(ModuleError::new("operation_failed", message));
                }
                Ok(self.snapshot())
            }
            "readEvents" => {
                let inner = self.lock();
                Ok(json!({ "events": inner.events }))
            }
            "clearEvents" => {
                let mut inner = self.lock();
                inner.events.clear();
                Ok(self.snapshot_locked(&inner))
            }
            _ => Err(ModuleError::new("unknown_method", "未知的断网守护操作。")),
        }
    }

    fn snapshot(&self) -> Value {
        let inner = self.lock();
        self.snapshot_locked(&inner)
    }

    fn snapshot_locked(&self, inner: &Inner) -> Value {
        let countdown_remaining_seconds = inner.countdown_deadline.map(|deadline| {
            deadline
                .saturating_duration_since(Instant::now())
                .as_secs()
                .saturating_add(u64::from(
                    deadline
                        .saturating_duration_since(Instant::now())
                        .subsec_nanos()
                        > 0,
                ))
        });
        let test_remaining_seconds = inner.test_deadline.map(|deadline| {
            deadline
                .saturating_duration_since(Instant::now())
                .as_secs()
                .saturating_add(u64::from(
                    deadline
                        .saturating_duration_since(Instant::now())
                        .subsec_nanos()
                        > 0,
                ))
        });
        json!({
            "settings": inner.settings,
            "guardEnabled": inner.settings.guard_enabled,
            "state": inner.state.as_str(),
            "isOnline": inner.is_online,
            "lastProbeEpochMillis": inner.last_probe_epoch_millis,
            "lastSuccessfulProbeEpochMillis": inner.last_successful_probe_epoch_millis,
            "consecutiveProbeFailures": inner.consecutive_probe_failures,
            "countdownRemainingSeconds": countdown_remaining_seconds,
            "testRemainingSeconds": test_remaining_seconds,
            "isSuppressed": inner.suppressed,
            "lastProbe": inner.last_probe,
            "events": inner.events,
        })
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, Inner> {
        self.inner
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    fn record_locked(&self, inner: &mut Inner, kind: &str, detail: Option<&str>) {
        inner.events.push(Event {
            timestamp_epoch_millis: epoch_millis(),
            kind: kind.to_string(),
            detail: detail.map(str::to_string),
        });
        if inner.events.len() > 20 {
            let excess = inner.events.len() - 20;
            inner.events.drain(0..excess);
        }
    }

    fn persist_settings(&self, settings: &Settings) -> Result<(), ModuleError> {
        let Some(root) = self.data_dir.as_deref() else {
            return Ok(());
        };
        fs::create_dir_all(root)
            .map_err(|error| ModuleError::new("settings_save_failed", error.to_string()))?;
        let target = root.join("settings.json");
        let temp = root.join(format!("settings.tmp-{}", std::process::id()));
        let bytes = serde_json::to_vec_pretty(settings)
            .map_err(|error| ModuleError::new("settings_save_failed", error.to_string()))?;
        fs::write(&temp, bytes)
            .map_err(|error| ModuleError::new("settings_save_failed", error.to_string()))?;
        let backup = root.join("settings.previous.json");
        if target.exists() {
            let _ = fs::remove_file(&backup);
            fs::rename(&target, &backup)
                .map_err(|error| ModuleError::new("settings_save_failed", error.to_string()))?;
        }
        if let Err(error) = fs::rename(&temp, &target) {
            if backup.exists() {
                let _ = fs::rename(&backup, &target);
            }
            return Err(ModuleError::new("settings_save_failed", error.to_string()));
        }
        let _ = fs::remove_file(backup);
        Ok(())
    }
}

fn load_settings(root: &Path) -> Option<Settings> {
    let bytes = fs::read(root.join("settings.json")).ok()?;
    let settings: Settings = serde_json::from_slice(&bytes).ok()?;
    settings.validate().ok()?;
    Some(settings)
}

fn epoch_millis() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
        .min(u64::MAX as u128) as u64
}

fn probe_connectivity() -> Vec<EndpointResult> {
    const TARGETS: [(&str, SocketAddr); 2] = [
        (
            "Cloudflare",
            SocketAddr::new(
                std::net::IpAddr::V4(std::net::Ipv4Addr::new(1, 1, 1, 1)),
                443,
            ),
        ),
        (
            "Google DNS",
            SocketAddr::new(
                std::net::IpAddr::V4(std::net::Ipv4Addr::new(8, 8, 8, 8)),
                53,
            ),
        ),
    ];
    TARGETS
        .into_iter()
        .map(|(name, address)| {
            let started = Instant::now();
            let result = TcpStream::connect_timeout(&address, PROBE_TIMEOUT);
            let elapsed = started.elapsed().as_millis();
            match result {
                Ok(stream) => {
                    let _ = stream.shutdown(std::net::Shutdown::Both);
                    EndpointResult {
                        name,
                        succeeded: true,
                        elapsed_millis: elapsed,
                        failure_category: None,
                    }
                }
                Err(error) => EndpointResult {
                    name,
                    succeeded: false,
                    elapsed_millis: elapsed,
                    failure_category: Some(if error.kind() == io::ErrorKind::TimedOut {
                        "Timeout".to_string()
                    } else {
                        "Unavailable".to_string()
                    }),
                },
            }
        })
        .collect()
}

fn request_system_shutdown() -> Result<(), String> {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        let executable =
            PathBuf::from(env::var_os("WINDIR").unwrap_or_else(|| "C:\\Windows".into()))
                .join("System32")
                .join("shutdown.exe");
        let status = Command::new(executable)
            .args(["/s", "/t", "0"])
            .creation_flags(0x0800_0000)
            .status()
            .map_err(|error| error.to_string())?;
        if status.success() {
            Ok(())
        } else {
            Err(format!("shutdown.exe returned {status}"))
        }
    }
    #[cfg(not(windows))]
    {
        Err("此系统不支持关机操作。".to_string())
    }
}

fn valid_token(value: &str, max: usize) -> bool {
    !value.is_empty()
        && value.len() <= max
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-' | b'_' | b':'))
}

fn valid_envelope(envelope: &HostEnvelope) -> bool {
    envelope.protocol_version == HOST_PROTOCOL_VERSION
        && valid_token(&envelope.message_type, 64)
        && valid_token(&envelope.request_id, 128)
}

fn write_response(
    writer: &mut BufWriter<impl Write>,
    message_type: &str,
    request_id: &str,
    payload: Value,
    error: Option<HostErrorBody>,
) {
    let response = HostResponse {
        protocol_version: HOST_PROTOCOL_VERSION,
        message_type,
        request_id,
        payload,
        error,
    };
    if let Ok(bytes) = serde_json::to_vec(&response) {
        if bytes.len() <= HOST_MAX_FRAME_BYTES {
            let _ = writer.write_all(&bytes);
            let _ = writer.write_all(b"\n");
            let _ = writer.flush();
        }
    }
}

fn main() {
    let app = PowerApp::new();
    let monitor = app.start_monitor();
    let module_id =
        env::var("QINGTOOLBOX_MODULE_ID").unwrap_or_else(|_| "qing.powerguard".to_string());
    let nonce = env::var("QINGTOOLBOX_MODULE_NONCE").unwrap_or_default();
    let stdin = io::stdin();
    let stdout = io::stdout();
    let mut reader = BufReader::new(stdin.lock());
    let mut writer = BufWriter::new(stdout.lock());
    let mut line = Vec::new();
    let mut handshaken = false;
    loop {
        line.clear();
        let read = match reader.read_until(b'\n', &mut line) {
            Ok(read) => read,
            Err(_) => break,
        };
        if read == 0 {
            break;
        }
        if line.len() > HOST_MAX_FRAME_BYTES {
            write_response(
                &mut writer,
                "module.protocol.response",
                "unknown",
                json!({}),
                Some(HostErrorBody {
                    code: "frame_too_large",
                    message: "模块请求帧过大。".to_string(),
                }),
            );
            break;
        }
        let envelope = match serde_json::from_slice::<HostEnvelope>(&line) {
            Ok(envelope) if valid_envelope(&envelope) => envelope,
            _ => {
                write_response(
                    &mut writer,
                    "module.protocol.response",
                    "unknown",
                    json!({}),
                    Some(HostErrorBody {
                        code: "invalid_frame",
                        message: "模块请求帧无效。".to_string(),
                    }),
                );
                continue;
            }
        };
        match envelope.message_type.as_str() {
            "module.hello.request" if !handshaken => {
                let valid = envelope.payload.get("moduleId").and_then(Value::as_str)
                    == Some(module_id.as_str())
                    && envelope.payload.get("nonce").and_then(Value::as_str)
                        == Some(nonce.as_str());
                if !valid {
                    write_response(
                        &mut writer,
                        "module.hello.response",
                        &envelope.request_id,
                        json!({}),
                        Some(HostErrorBody {
                            code: "hello_rejected",
                            message: "模块 hello 校验失败。".to_string(),
                        }),
                    );
                    break;
                }
                handshaken = true;
                write_response(
                    &mut writer,
                    "module.hello.response",
                    &envelope.request_id,
                    json!({ "moduleId": module_id, "nonce": nonce, "name": "PowerGuard", "protocolVersion": HOST_PROTOCOL_VERSION }),
                    None,
                );
            }
            "module.invoke.request" if handshaken => {
                let method = envelope
                    .payload
                    .get("method")
                    .and_then(Value::as_str)
                    .unwrap_or_default();
                let payload = envelope.payload.get("payload").unwrap_or(&Value::Null);
                match app.invoke(method, payload) {
                    Ok(value) => write_response(
                        &mut writer,
                        "module.invoke.response",
                        &envelope.request_id,
                        value,
                        None,
                    ),
                    Err(error) => write_response(
                        &mut writer,
                        "module.invoke.response",
                        &envelope.request_id,
                        json!({}),
                        Some(HostErrorBody {
                            code: error.code,
                            message: error.message,
                        }),
                    ),
                }
            }
            "module.shutdown.request" if handshaken => {
                write_response(
                    &mut writer,
                    "module.shutdown.response",
                    &envelope.request_id,
                    json!({}),
                    None,
                );
                break;
            }
            _ => write_response(
                &mut writer,
                "module.protocol.response",
                &envelope.request_id,
                json!({}),
                Some(HostErrorBody {
                    code: "invalid_message",
                    message: "模块消息顺序或类型无效。".to_string(),
                }),
            ),
        }
    }
    app.stop.store(true, Ordering::Release);
    let _ = monitor.join();
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn settings_ranges_are_bounded() {
        let mut settings = Settings::default();
        assert!(settings.validate().is_ok());
        settings.shutdown_countdown_seconds = 59;
        assert!(settings.validate().is_err());
    }

    #[test]
    fn default_guard_is_disabled() {
        assert!(!Settings::default().guard_enabled);
    }

    #[test]
    fn malformed_confirmation_is_rejected() {
        let app = PowerApp::new();
        let error = app
            .invoke("shutdownNow", &json!({ "confirm": "yes" }))
            .unwrap_err();
        assert_eq!(error.code, "confirmation_required");
    }

    #[test]
    fn host_envelope_rejects_wrong_version() {
        let envelope = HostEnvelope {
            protocol_version: 2,
            message_type: "module.invoke.request".to_string(),
            request_id: "x".to_string(),
            payload: json!({}),
        };
        assert!(!valid_envelope(&envelope));
    }
}
