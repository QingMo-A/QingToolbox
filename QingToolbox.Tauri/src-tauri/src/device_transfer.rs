//! Host-owned device file transfer. The engine runs inside QingToolbox, without
//! a module manifest, module process, or module discovery dependency.
//! The existing Android-compatible v1 wire protocol is retained. Its legacy
//! identity matching is not a replacement for the planned QDS authenticated session.

use std::{
    collections::{BTreeMap, VecDeque},
    env,
    fs::{self, File, OpenOptions},
    io::{self, Read, Write},
    net::{IpAddr, Shutdown, SocketAddr, TcpListener, TcpStream},
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicBool, AtomicU64, Ordering},
        Arc, Condvar, Mutex, MutexGuard,
    },
    thread,
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use crate::device_diagnostics::{self as diag, Event as E, Level as L, Reason as R};
use mdns_sd::{ResolvedService, ServiceDaemon, ServiceEvent, ServiceInfo};
use serde::{Deserialize, Serialize};
use serde_json::{json, Map, Value};
use sha2::{Digest, Sha256};

const NETWORK_PROTOCOL_VERSION: u8 = 1;
const NETWORK_MAX_FRAME_BYTES: usize = 4096;
const MAX_FIELD_LENGTH: usize = 128;
const MAX_DISPLAY_NAME_LENGTH: usize = 64;
const MAX_FILE_NAME_LENGTH: usize = 255;
const MAX_FILE_BYTES: u64 = 8 * 1024 * 1024 * 1024;
const MAX_ADDRESSES: usize = 8;
const SERVICE_TYPE: &str = "_qingtransfer._tcp.local.";
const HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(5);
const PROBE_TIMEOUT: Duration = Duration::from_secs(1);
const DECISION_TIMEOUT: Duration = Duration::from_secs(120);
const NETWORK_BUFFER_BYTES: usize = 128 * 1024;

static NONCE_SEQUENCE: AtomicU64 = AtomicU64::new(1);

#[derive(Debug, Clone)]
pub(crate) struct TransferError {
    pub code: &'static str,
    pub message: String,
}

impl TransferError {
    fn new(code: &'static str, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }
}

#[derive(Debug, Clone)]
struct Peer {
    service_name: String,
    display_name: String,
    platform: String,
    protocol_version: String,
    capabilities: Vec<String>,
    addresses: Vec<IpAddr>,
    port: u16,
    device_id: Option<String>,
    online: bool,
    last_seen: Option<String>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
struct ReceiveSettings {
    default_directory: Option<String>,
    use_default_directory: bool,
    auto_accept: bool,
}

#[derive(Debug, Clone)]
struct TransferProgress {
    name: String,
    completed: u64,
    total: u64,
    receiving: bool,
    phase: TransferPhase,
}

#[derive(Debug, Clone, Copy, Serialize)]
enum TransferPhase {
    WaitingAcceptance,
    Transferring,
}

#[derive(Debug, Clone)]
pub(crate) struct ReceiveOutcome {
    pub id: String,
    pub peer_name: String,
    pub file_name: String,
    pub size: u64,
    pub succeeded: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
enum SessionState {
    #[default]
    Idle,
    Connecting,
    WaitingApproval,
    Connected,
}

impl SessionState {
    const fn as_str(self) -> &'static str {
        match self {
            Self::Idle => "Idle",
            Self::Connecting => "Connecting",
            Self::WaitingApproval => "WaitingApproval",
            Self::Connected => "Connected",
        }
    }
}

#[derive(Debug, Clone)]
struct IncomingConnection {
    platform: String,
    name: String,
}

#[derive(Debug, Clone)]
struct IncomingFile {
    name: String,
    size: u64,
}

#[derive(Debug, Clone)]
enum FileDecision {
    Accept(PathBuf),
    Reject,
}

/// A small condition-variable mailbox used by the session reader and the
/// WebView-facing invoke loop.  Each transfer has at most one pending value.
#[derive(Debug)]
struct DecisionWait<T> {
    inner: Arc<(Mutex<Option<T>>, Condvar)>,
}

impl<T> Clone for DecisionWait<T> {
    fn clone(&self) -> Self {
        Self {
            inner: Arc::clone(&self.inner),
        }
    }
}

impl<T> DecisionWait<T> {
    fn new() -> Self {
        Self {
            inner: Arc::new((Mutex::new(None), Condvar::new())),
        }
    }

    fn clear(&self) {
        let (lock, _) = &*self.inner;
        lock_recover(lock).take();
    }

    fn set(&self, value: T) {
        let (lock, signal) = &*self.inner;
        *lock_recover(lock) = Some(value);
        signal.notify_all();
    }
}

fn wait_decision<T: Clone>(
    mailbox: &DecisionWait<T>,
    cancelled: &AtomicBool,
    timeout: Duration,
) -> Option<T> {
    let (lock, signal) = &*mailbox.inner;
    let mut guard = lock_recover(lock);
    let deadline = std::time::Instant::now() + timeout;
    loop {
        if let Some(value) = guard.as_ref() {
            return Some(value.clone());
        }
        if cancelled.load(Ordering::Acquire) {
            return None;
        }
        let now = std::time::Instant::now();
        if now >= deadline {
            return None;
        }
        let wait_for = deadline
            .saturating_duration_since(now)
            .min(Duration::from_millis(250));
        guard = match signal.wait_timeout(guard, wait_for) {
            Ok((guard, _)) => guard,
            Err(poisoned) => poisoned.into_inner().0,
        };
    }
}

#[derive(Debug)]
struct Session {
    stream: Arc<Mutex<TcpStream>>,
    peer: Peer,
    cancelled: Arc<AtomicBool>,
    connection_decision: DecisionWait<bool>,
    file_decision: DecisionWait<FileDecision>,
    offer_response: DecisionWait<bool>,
    result_response: DecisionWait<bool>,
}

impl Session {
    fn new(stream: TcpStream, peer: Peer) -> Arc<Self> {
        Arc::new(Self {
            stream: Arc::new(Mutex::new(stream)),
            peer,
            cancelled: Arc::new(AtomicBool::new(false)),
            connection_decision: DecisionWait::new(),
            file_decision: DecisionWait::new(),
            offer_response: DecisionWait::new(),
            result_response: DecisionWait::new(),
        })
    }

    fn close(&self) {
        self.cancelled.store(true, Ordering::Release);
        if let Ok(stream) = self.stream.lock() {
            let _ = stream.shutdown(Shutdown::Both);
        }
        self.connection_decision.set(false);
        self.file_decision.set(FileDecision::Reject);
        self.offer_response.set(false);
        self.result_response.set(false);
    }
}

#[derive(Debug, Default)]
struct SharedState {
    discovery_running: bool,
    peers: BTreeMap<String, Peer>,
    session_state: SessionState,
    active_peer: Option<Peer>,
    session: Option<Arc<Session>>,
    connect_cancel: Option<Arc<AtomicBool>>,
    incoming_connection: Option<IncomingConnection>,
    incoming_file: Option<IncomingFile>,
    receive: ReceiveSettings,
    transfer: Option<TransferProgress>,
    last_completed: Option<String>,
    last_error: Option<String>,
    receive_outcomes: VecDeque<ReceiveOutcome>,
}

struct DiscoveryRuntime {
    daemon: ServiceDaemon,
    service_fullname: String,
    stop: Arc<AtomicBool>,
    workers: Vec<thread::JoinHandle<()>>,
}

pub(crate) struct TransferEngine {
    lifecycle: Mutex<()>,
    state: Arc<Mutex<SharedState>>,
    process_stop: Arc<AtomicBool>,
    active: AtomicBool,
    data_directory: PathBuf,
    discovery: Mutex<Option<DiscoveryRuntime>>,
    friendly_name: String,
    device_id: Option<String>,
}

impl TransferEngine {
    /// A process-wide, single-consumer result queue; UI navigation cannot replay a receipt.
    pub(crate) fn take_receive_outcomes(&self) -> Vec<ReceiveOutcome> {
        lock_recover(&self.state)
            .receive_outcomes
            .drain(..)
            .collect()
    }

    fn finish_receive(
        &self,
        session: &Session,
        offer: &FileOfferWire,
        notify: bool,
        error: Option<String>,
    ) {
        let succeeded = error.is_none();
        self.clear_transfer(error);
        if notify {
            let mut state = lock_recover(&self.state);
            if state.receive_outcomes.len() >= 32 {
                state.receive_outcomes.pop_front();
            }
            state.receive_outcomes.push_back(ReceiveOutcome {
                id: unique_id(),
                peer_name: session.peer.display_name.clone(),
                file_name: offer.name.clone(),
                size: offer.size,
                succeeded,
            });
        }
    }

    pub(crate) fn new(
        data_directory: PathBuf,
        legacy_directory: Option<&Path>,
        friendly_name: &str,
        device_id: Option<String>,
    ) -> Self {
        migrate_receive_settings(&data_directory, legacy_directory);
        let receive = load_receive_settings(&data_directory);
        let friendly_name = sanitize_display_name(friendly_name);
        let device_id = device_id
            .filter(|value| value.len() == 32 && value.bytes().all(|byte| byte.is_ascii_hexdigit()))
            .map(|value| value.to_ascii_lowercase());
        Self {
            lifecycle: Mutex::new(()),
            state: Arc::new(Mutex::new(SharedState {
                receive,
                ..SharedState::default()
            })),
            process_stop: Arc::new(AtomicBool::new(false)),
            active: AtomicBool::new(false),
            data_directory,
            discovery: Mutex::new(None),
            friendly_name,
            device_id,
        }
    }

    fn start_discovery(self: &Arc<Self>) {
        if lock_recover(&self.discovery).is_some() {
            lock_recover(&self.state).discovery_running = true;
            return;
        }

        let listener = match TcpListener::bind(("0.0.0.0", 0)) {
            Ok(listener) => listener,
            Err(error) => {
                self.set_error(format!("无法打开局域网传输端口：{error}"));
                return;
            }
        };
        let _ = listener.set_nonblocking(true);
        let port = listener
            .local_addr()
            .map(|address| address.port())
            .unwrap_or(0);
        if port == 0 {
            self.set_error("无法读取局域网传输端口。".to_string());
            return;
        }

        let daemon = match ServiceDaemon::new() {
            Ok(daemon) => daemon,
            Err(error) => {
                self.set_error(format!("局域网发现不可用：{error}"));
                return;
            }
        };
        let instance_name = sanitize_dns_label(&format!(
            "{}-{}",
            self.friendly_name,
            self.device_id.as_deref().unwrap_or("unavailable")
        ));
        let host_name = format!(
            "{}.local.",
            sanitize_dns_label(
                &env::var("COMPUTERNAME").unwrap_or_else(|_| self.friendly_name.clone()),
            )
        );
        let mut properties = vec![
            ("v", "1"),
            ("pf", "windows"),
            ("name", self.friendly_name.as_str()),
            ("cap", "file"),
        ];
        if let Some(device_id) = &self.device_id {
            properties.push(("id", device_id.as_str()));
        }
        let service = match ServiceInfo::new(
            SERVICE_TYPE,
            &instance_name,
            &host_name,
            "",
            port,
            &properties[..],
        ) {
            Ok(service) => service.enable_addr_auto(),
            Err(error) => {
                let _ = daemon.shutdown();
                self.set_error(format!("局域网服务注册失败：{error}"));
                return;
            }
        };
        let service_fullname = service.get_fullname().to_string();
        if let Err(error) = daemon.register(service) {
            let _ = daemon.shutdown();
            self.set_error(format!("局域网服务注册失败：{error}"));
            return;
        }
        let receiver = match daemon.browse(SERVICE_TYPE) {
            Ok(receiver) => receiver,
            Err(error) => {
                let _ = daemon.unregister(&service_fullname);
                let _ = daemon.shutdown();
                self.set_error(format!("局域网设备搜索不可用：{error}"));
                return;
            }
        };
        let discovery_stop = Arc::new(AtomicBool::new(false));
        let mut runtime = DiscoveryRuntime {
            daemon: daemon.clone(),
            service_fullname: service_fullname.clone(),
            stop: Arc::clone(&discovery_stop),
            workers: Vec::new(),
        };
        {
            let mut state = lock_recover(&self.state);
            state.discovery_running = true;
            state.last_error = None;
        }

        let accept_app = Arc::clone(self);
        let accept_stop = Arc::clone(&discovery_stop);
        runtime.workers.push(thread::spawn(move || {
            accept_loop(accept_app, listener, accept_stop)
        }));

        let browse_app = Arc::clone(self);
        runtime.workers.push(thread::spawn(move || {
            browse_loop(browse_app, receiver, discovery_stop, service_fullname)
        }));
        *lock_recover(&self.discovery) = Some(runtime);
    }

    fn stop_discovery(&self) {
        let runtime = lock_recover(&self.discovery).take();
        if let Some(runtime) = runtime {
            runtime.stop.store(true, Ordering::Release);
            let _ = runtime.daemon.unregister(&runtime.service_fullname);
            let _ = runtime.daemon.shutdown();
            for worker in runtime.workers {
                let _ = worker.join();
            }
        }
        lock_recover(&self.state).discovery_running = false;
    }

    pub(crate) fn set_enabled(self: &Arc<Self>, enabled: bool) {
        let _gate = lock_recover(&self.lifecycle);
        if enabled && !self.process_stop.load(Ordering::Acquire) {
            self.start_discovery();
            let running = lock_recover(&self.state).discovery_running;
            self.active.store(running, Ordering::Release);
        } else {
            self.active.store(false, Ordering::Release);
            self.stop_discovery();
            // Drain accept first; otherwise a late incoming socket can survive disable.
            self.disconnect();
            lock_recover(&self.state).peers.clear();
        }
    }

    pub(crate) fn shutdown(&self) {
        let _gate = lock_recover(&self.lifecycle);
        self.process_stop.store(true, Ordering::Release);
        self.active.store(false, Ordering::Release);
        self.stop_discovery();
        self.disconnect();
    }

    pub(crate) fn connect_target(
        self: &Arc<Self>,
        target: &crate::devices::TransferTarget,
    ) -> Result<Value, TransferError> {
        let service_name = {
            let state = lock_recover(&self.state);
            let matches = state
                .peers
                .values()
                .filter(|peer| {
                    peer.online
                        && peer.platform == target.platform
                        && match &peer.device_id {
                            Some(id) => id == &target.device_id,
                            None => peer
                                .addresses
                                .iter()
                                .any(|ip| target.addresses.contains(&ip.to_string())),
                        }
                })
                .collect::<Vec<_>>();
            if matches.len() != 1 {
                return Err(TransferError::new(
                    "peer_unavailable",
                    "设备传输端点暂不可用，请稍后重试。",
                ));
            }
            matches[0].service_name.clone()
        };
        self.invoke("connect", &json!({ "serviceName": service_name }))
    }

    fn set_error(&self, message: String) {
        diag::record(
            L::Warning,
            E::TransferFailed,
            diag::failure_reason(&message),
            None,
            None,
            true,
        );
        lock_recover(&self.state).last_error = Some(message);
    }

    fn clear_error(&self) {
        lock_recover(&self.state).last_error = None;
    }

    pub(crate) fn snapshot(&self) -> Value {
        let state = lock_recover(&self.state);
        let peers = state.peers.values().map(peer_json).collect::<Vec<_>>();
        let active_peer = state.active_peer.as_ref().map(peer_json);
        json!({
            "discovery": { "running": state.discovery_running, "peers": peers },
            "session": { "state": state.session_state.as_str(), "peer": active_peer },
            "incomingConnection": state.incoming_connection.as_ref().map(|value| json!({
                "platform": value.platform,
                "name": value.name,
            })),
            "incomingFile": state.incoming_file.as_ref().map(|value| json!({
                "name": value.name,
                "size": value.size,
            })),
            "receive": {
                "defaultDirectory": state.receive.default_directory,
                "useDefaultDirectory": state.receive.use_default_directory,
                "autoAccept": state.receive.auto_accept,
            },
            "transfer": state.transfer.as_ref().map(|value| json!({
                "name": value.name,
                "completed": value.completed,
                "total": value.total,
                "receiving": value.receiving,
                "phase": value.phase,
            })),
            "lastCompleted": state.last_completed,
            "lastError": state.last_error,
        })
    }

    pub(crate) fn invoke(
        self: &Arc<Self>,
        method: &str,
        payload: &Value,
    ) -> Result<Value, TransferError> {
        if !self.active.load(Ordering::Acquire)
            && matches!(
                method,
                "refresh"
                    | "connect"
                    | "sendFile"
                    | "acceptIncomingConnection"
                    | "acceptIncomingFile"
                    | "acceptIncomingFileDefault"
            )
        {
            return Err(TransferError::new(
                "transfer_inactive",
                "请先开启设备发现。",
            ));
        }
        match method {
            "getState" => Ok(self.snapshot()),
            "refresh" => {
                lock_recover(&self.state).peers.clear();
                self.clear_error();
                self.set_enabled(true);
                Ok(self.snapshot())
            }
            "connect" => {
                let service_name = required_string(payload, "serviceName")?;
                self.begin_connect(&service_name)?;
                Ok(self.snapshot())
            }
            "disconnect" => {
                self.disconnect();
                Ok(self.snapshot())
            }
            "sendFile" => {
                let path = required_string(payload, "path")?;
                self.begin_send_file(&path)?;
                Ok(self.snapshot())
            }
            "inspectFile" => {
                let path = validate_existing_file(&required_string(payload, "path")?)?;
                let size = fs::metadata(&path)
                    .map_err(|error| io_error("file_unavailable", "文件不可用", error))?
                    .len();
                Ok(
                    json!({ "name": path.file_name().and_then(|name| name.to_str()).unwrap_or("file"), "size": size }),
                )
            }
            "acceptIncomingConnection" => {
                let session = lock_recover(&self.state).session.clone();
                if let Some(session) = session {
                    if lock_recover(&self.state).session_state == SessionState::WaitingApproval {
                        session.connection_decision.set(true);
                    }
                }
                Ok(self.snapshot())
            }
            "rejectIncomingConnection" => {
                let session = lock_recover(&self.state).session.clone();
                if let Some(session) = session {
                    session.connection_decision.set(false);
                }
                Ok(self.snapshot())
            }
            "acceptIncomingFile" => {
                let destination = required_string(payload, "destinationPath")?;
                let destination = validate_destination_path(&destination)?;
                let session = lock_recover(&self.state).session.clone();
                let Some(session) = session else {
                    return Err(TransferError::new(
                        "no_incoming_file",
                        "当前没有等待接收的文件。",
                    ));
                };
                if lock_recover(&self.state).incoming_file.is_none() {
                    return Err(TransferError::new(
                        "no_incoming_file",
                        "当前没有等待接收的文件。",
                    ));
                }
                session.file_decision.set(FileDecision::Accept(destination));
                Ok(self.snapshot())
            }
            "acceptIncomingFileDefault" => {
                let (session, destination) = {
                    let state = lock_recover(&self.state);
                    let session = state.session.clone().ok_or_else(|| {
                        TransferError::new("no_incoming_file", "当前没有等待接收的文件。")
                    })?;
                    let offer = state.incoming_file.as_ref().ok_or_else(|| {
                        TransferError::new("no_incoming_file", "当前没有等待接收的文件。")
                    })?;
                    let destination = default_destination(&state.receive, &offer.name, false)
                        .ok_or_else(|| {
                            TransferError::new(
                                "default_directory_unavailable",
                                "默认接收文件夹不可用。",
                            )
                        })?;
                    (session, destination)
                };
                session.file_decision.set(FileDecision::Accept(destination));
                Ok(self.snapshot())
            }
            "rejectIncomingFile" => {
                if let Some(session) = lock_recover(&self.state).session.clone() {
                    session.file_decision.set(FileDecision::Reject);
                }
                Ok(self.snapshot())
            }
            "setReceivePreferences" => {
                self.update_receive_preferences(payload)?;
                Ok(self.snapshot())
            }
            "clearReceiveDirectory" => {
                let settings = ReceiveSettings::default();
                save_receive_settings(&self.data_directory, &settings)?;
                lock_recover(&self.state).receive = settings;
                Ok(self.snapshot())
            }
            "dismissError" => {
                self.clear_error();
                Ok(self.snapshot())
            }
            _ => Err(TransferError::new("unknown_method", "未知的设备传输操作。")),
        }
    }

    fn update_receive_preferences(&self, payload: &Value) -> Result<(), TransferError> {
        let mut settings = lock_recover(&self.state).receive.clone();
        if let Some(value) = payload.get("useDefaultDirectory") {
            settings.use_default_directory = value.as_bool().ok_or_else(|| {
                TransferError::new("invalid_payload", "useDefaultDirectory 必须是布尔值。")
            })?;
        }
        if let Some(value) = payload.get("autoAccept") {
            settings.auto_accept = value.as_bool().ok_or_else(|| {
                TransferError::new("invalid_payload", "autoAccept 必须是布尔值。")
            })?;
        }
        if let Some(value) = payload.get("defaultDirectory") {
            if value.is_null() {
                settings.default_directory = None;
            } else {
                let path = value.as_str().ok_or_else(|| {
                    TransferError::new("invalid_payload", "defaultDirectory 必须是字符串。")
                })?;
                settings.default_directory =
                    Some(validate_directory_path(path)?.to_string_lossy().to_string());
            }
        }
        save_receive_settings(&self.data_directory, &settings)?;
        lock_recover(&self.state).receive = settings;
        Ok(())
    }

    fn begin_connect(self: &Arc<Self>, service_name: &str) -> Result<(), TransferError> {
        let (peer, cancel) = {
            let mut state = lock_recover(&self.state);
            if state.session_state != SessionState::Idle {
                return Err(TransferError::new(
                    "session_active",
                    "已有一个文件传输会话正在运行。",
                ));
            }
            let peer = state
                .peers
                .values()
                .find(|peer| canonical_name(&peer.service_name) == canonical_name(service_name))
                .cloned()
                .ok_or_else(|| TransferError::new("peer_unavailable", "所选设备已不在线。"))?;
            if peer.port == 0 || peer.addresses.is_empty() {
                return Err(TransferError::new(
                    "peer_unavailable",
                    "所选设备没有可用端点。",
                ));
            }
            let cancel = Arc::new(AtomicBool::new(false));
            state.session_state = SessionState::Connecting;
            state.active_peer = Some(peer.clone());
            state.connect_cancel = Some(Arc::clone(&cancel));
            state.last_error = None;
            (peer, cancel)
        };
        diag::record(
            L::Information,
            E::TransferConnect,
            R::None,
            peer.device_id.as_deref(),
            None,
            false,
        );
        let app = Arc::clone(self);
        thread::spawn(move || connect_worker(app, peer, cancel));
        Ok(())
    }

    fn begin_send_file(self: &Arc<Self>, raw_path: &str) -> Result<(), TransferError> {
        let path = validate_existing_file(raw_path)?;
        let session = {
            let mut state = lock_recover(&self.state);
            if state.session_state != SessionState::Connected {
                return Err(TransferError::new("not_connected", "请先连接到设备。"));
            }
            if state.transfer.is_some() {
                return Err(TransferError::new("transfer_active", "已有文件正在传输。"));
            }
            let session = state
                .session
                .clone()
                .ok_or_else(|| TransferError::new("not_connected", "请先连接到设备。"))?;
            session.offer_response.clear();
            session.result_response.clear();
            let metadata = fs::metadata(&path)
                .map_err(|error| io_error("file_unavailable", "无法读取要发送的文件。", error))?;
            state.transfer = Some(TransferProgress {
                name: path
                    .file_name()
                    .and_then(|value| value.to_str())
                    .unwrap_or("file")
                    .to_string(),
                completed: 0,
                total: metadata.len(),
                receiving: false,
                phase: TransferPhase::WaitingAcceptance,
            });
            state.last_completed = None;
            state.last_error = None;
            session
        };
        let app = Arc::clone(self);
        thread::spawn(move || send_file_worker(app, session, path));
        Ok(())
    }

    fn disconnect(&self) {
        let (session, connect_cancel) = {
            let mut state = lock_recover(&self.state);
            let session = state.session.take();
            let connect_cancel = state.connect_cancel.take();
            state.session_state = SessionState::Idle;
            state.active_peer = None;
            state.incoming_connection = None;
            state.incoming_file = None;
            state.transfer = None;
            (session, connect_cancel)
        };
        if let Some(cancel) = connect_cancel {
            cancel.store(true, Ordering::Release);
        }
        if let Some(session) = session {
            session.close();
        }
    }

    fn install_incoming(self: &Arc<Self>, stream: TcpStream, hello: HelloWire) {
        let remote = stream.peer_addr().ok();
        let peer = Peer {
            service_name: format!("incoming-{}", unique_id()),
            display_name: hello.name.clone(),
            platform: hello.platform.clone(),
            protocol_version: NETWORK_PROTOCOL_VERSION.to_string(),
            capabilities: vec!["file".to_string()],
            addresses: remote.map(|address| vec![address.ip()]).unwrap_or_default(),
            port: remote.map(|address| address.port()).unwrap_or_default(),
            device_id: hello.device_id.clone(),
            online: true,
            last_seen: Some(unix_time_millis().to_string()),
        };
        let mut state = lock_recover(&self.state);
        if state.session_state != SessionState::Idle {
            drop(state);
            let mut stream = stream;
            let _ = write_wire(&mut stream, &WireMessage::Reject);
            let _ = stream.shutdown(Shutdown::Both);
            return;
        }
        let session = Session::new(stream, peer.clone());
        session.connection_decision.clear();
        state.session = Some(Arc::clone(&session));
        state.connect_cancel = Some(Arc::clone(&session.cancelled));
        state.session_state = SessionState::WaitingApproval;
        state.active_peer = Some(peer);
        state.incoming_connection = Some(IncomingConnection {
            platform: hello.platform,
            name: hello.name,
        });
        state.last_error = None;
        drop(state);
        let app = Arc::clone(self);
        thread::spawn(move || incoming_approval_worker(app, session));
    }

    fn set_connected(self: &Arc<Self>, session: Arc<Session>) -> bool {
        let mut state = lock_recover(&self.state);
        if state
            .session
            .as_ref()
            .is_some_and(|current| !Arc::ptr_eq(current, &session))
            || session.cancelled.load(Ordering::Acquire)
        {
            return false;
        }
        state.session = Some(Arc::clone(&session));
        state.connect_cancel = Some(Arc::clone(&session.cancelled));
        state.active_peer = Some(session.peer.clone());
        state.session_state = SessionState::Connected;
        diag::record(
            L::Information,
            E::TransferConnected,
            R::None,
            session.peer.device_id.as_deref(),
            None,
            false,
        );
        state.incoming_connection = None;
        state.last_error = None;
        true
    }

    fn finish_connect_failure(&self, cancel: &Arc<AtomicBool>, message: String) {
        let mut state = lock_recover(&self.state);
        if state
            .connect_cancel
            .as_ref()
            .is_some_and(|current| !Arc::ptr_eq(current, cancel))
        {
            return;
        }
        diag::record(
            L::Warning,
            E::TransferFailed,
            diag::failure_reason(&message),
            state
                .active_peer
                .as_ref()
                .and_then(|peer| peer.device_id.as_deref()),
            None,
            false,
        );
        state.connect_cancel = None;
        state.session = None;
        state.session_state = SessionState::Idle;
        state.active_peer = None;
        state.last_error = Some(message);
    }

    fn clear_session_if(&self, session: &Arc<Session>, error: Option<String>) {
        let mut state = lock_recover(&self.state);
        let same = state
            .session
            .as_ref()
            .is_some_and(|current| Arc::ptr_eq(current, session));
        if !same {
            return;
        }
        state.session = None;
        state.connect_cancel = None;
        state.session_state = SessionState::Idle;
        state.active_peer = None;
        state.incoming_connection = None;
        state.incoming_file = None;
        state.transfer = None;
        if error.is_some() {
            state.last_error = error;
        }
    }

    fn update_progress(&self, name: &str, completed: u64, total: u64, receiving: bool) {
        let mut state = lock_recover(&self.state);
        if state.transfer.is_some() {
            state.transfer = Some(TransferProgress {
                name: name.to_string(),
                completed,
                total,
                receiving,
                phase: TransferPhase::Transferring,
            });
        }
    }

    fn clear_transfer(&self, error: Option<String>) {
        let mut state = lock_recover(&self.state);
        let finished = state.transfer.take();
        if finished.is_some() {
            diag::record(
                if error.is_none() {
                    L::Information
                } else {
                    L::Warning
                },
                if error.is_none() {
                    E::TransferCompleted
                } else {
                    E::TransferFailed
                },
                error
                    .as_deref()
                    .map(diag::failure_reason)
                    .unwrap_or(R::None),
                state
                    .active_peer
                    .as_ref()
                    .and_then(|peer| peer.device_id.as_deref()),
                None,
                false,
            );
        }
        if error.is_none() {
            state.last_completed = finished.map(|value| value.name);
        }
        state.incoming_file = None;
        if let Some(error) = error {
            state.last_error = Some(error);
        }
    }

    fn mark_transferring(&self) {
        if let Some(transfer) = lock_recover(&self.state).transfer.as_mut() {
            transfer.phase = TransferPhase::Transferring;
        }
    }
}

fn lock_recover<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn peer_json(peer: &Peer) -> Value {
    json!({
        "serviceName": peer.service_name,
        "displayName": peer.display_name,
        "platform": peer.platform,
        "protocolVersion": peer.protocol_version,
        "capabilities": peer.capabilities,
        "addresses": peer.addresses.iter().map(ToString::to_string).collect::<Vec<_>>(),
        "port": peer.port,
        "deviceId": peer.device_id,
        "online": peer.online,
        "lastSeen": peer.last_seen,
    })
}

fn required_string(payload: &Value, name: &str) -> Result<String, TransferError> {
    payload
        .get(name)
        .and_then(Value::as_str)
        .filter(|value| !value.trim().is_empty())
        .map(str::to_string)
        .ok_or_else(|| TransferError::new("invalid_payload", format!("缺少 {name}。")))
}

fn io_error(code: &'static str, prefix: &str, error: io::Error) -> TransferError {
    TransferError::new(code, format!("{prefix}：{error}"))
}

fn unix_time_millis() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis().min(u64::MAX as u128) as u64)
        .unwrap_or_default()
}

fn unique_id() -> String {
    let sequence = NONCE_SEQUENCE.fetch_add(1, Ordering::Relaxed);
    format!(
        "{:x}-{:x}-{:x}",
        unix_time_millis(),
        std::process::id(),
        sequence
    )
}

fn create_probe_nonce() -> String {
    let mut hasher = Sha256::new();
    hasher.update(unique_id().as_bytes());
    hasher.update(
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos()
            .to_le_bytes(),
    );
    hex_lower(&hasher.finalize())[..32].to_string()
}

fn hex_lower(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

fn canonical_name(value: &str) -> String {
    value.trim().trim_end_matches('.').to_ascii_lowercase()
}

fn sanitize_display_name(value: &str) -> String {
    let mut result = value
        .trim()
        .chars()
        .filter(|character| !character.is_control())
        .collect::<String>();
    if result.is_empty() {
        result = "QingToolbox".to_string();
    }
    result.chars().take(MAX_DISPLAY_NAME_LENGTH).collect()
}

fn sanitize_dns_label(value: &str) -> String {
    let mut result = value
        .chars()
        .filter(|character| character.is_ascii_alphanumeric() || *character == '-')
        .collect::<String>();
    if result.is_empty() {
        result = "qingtoolbox".to_string();
    }
    result.chars().take(63).collect()
}

fn safe_field(value: Option<&str>, max: usize) -> Option<String> {
    let value = value?.trim();
    if value.is_empty() || value.len() > max || value.chars().any(char::is_control) {
        return None;
    }
    Some(value.to_string())
}

fn parse_peer(info: &ResolvedService, own_fullname: &str) -> Option<Peer> {
    let service_name = info.get_fullname().trim().trim_end_matches('.').to_string();
    if canonical_name(&service_name) == canonical_name(own_fullname) {
        return None;
    }
    let version = safe_field(info.get_property_val_str("v"), MAX_FIELD_LENGTH)?;
    if version != "1" {
        return None;
    }
    let platform = safe_field(info.get_property_val_str("pf"), MAX_FIELD_LENGTH)?;
    if platform != "windows" && platform != "android" {
        return None;
    }
    let display_name = safe_field(info.get_property_val_str("name"), MAX_DISPLAY_NAME_LENGTH)?;
    let device_id = info
        .get_property_val_str("id")
        .filter(|value| value.len() == 32 && value.bytes().all(|byte| byte.is_ascii_hexdigit()))
        .map(str::to_ascii_lowercase);
    let capability_text = safe_field(info.get_property_val_str("cap"), MAX_FIELD_LENGTH)?;
    let capabilities = capability_text
        .split(',')
        .map(str::trim)
        .filter(|value| !value.is_empty() && value.len() <= MAX_FIELD_LENGTH)
        .map(str::to_string)
        .collect::<Vec<_>>();
    if !capabilities
        .iter()
        .any(|value| value.eq_ignore_ascii_case("file"))
    {
        return None;
    }
    let addresses = info
        .get_addresses()
        .iter()
        .map(|address| address.to_ip_addr())
        .filter(|address| !address.is_unspecified() && !address.is_multicast())
        .take(MAX_ADDRESSES)
        .collect::<Vec<_>>();
    if addresses.is_empty() || info.get_port() == 0 {
        return None;
    }
    Some(Peer {
        service_name,
        display_name,
        platform,
        protocol_version: version,
        capabilities,
        addresses,
        port: info.get_port(),
        device_id,
        online: true,
        last_seen: Some(unix_time_millis().to_string()),
    })
}

fn browse_loop(
    app: Arc<TransferEngine>,
    receiver: mdns_sd::Receiver<ServiceEvent>,
    stop: Arc<AtomicBool>,
    own_fullname: String,
) {
    while !stop.load(Ordering::Acquire) && !app.process_stop.load(Ordering::Acquire) {
        let event = match receiver.recv_timeout(Duration::from_millis(500)) {
            Ok(event) => event,
            Err(mdns_sd::RecvTimeoutError::Timeout) => continue,
            Err(_) => break,
        };
        match event {
            ServiceEvent::ServiceResolved(info) => {
                let Some(peer) = parse_peer(&info, &own_fullname) else {
                    continue;
                };
                if confirm_peer(&peer) {
                    let mut state = lock_recover(&app.state);
                    state.peers.insert(canonical_name(&peer.service_name), peer);
                } else {
                    lock_recover(&app.state)
                        .peers
                        .remove(&canonical_name(&peer.service_name));
                }
            }
            ServiceEvent::ServiceRemoved(_, fullname) => {
                lock_recover(&app.state)
                    .peers
                    .remove(&canonical_name(&fullname));
            }
            _ => {}
        }
    }
}

fn accept_loop(app: Arc<TransferEngine>, listener: TcpListener, stop: Arc<AtomicBool>) {
    while !stop.load(Ordering::Acquire) && !app.process_stop.load(Ordering::Acquire) {
        match listener.accept() {
            Ok((mut stream, _)) => {
                // The listener is non-blocking so shutdown can be observed without
                // hanging the module. On Windows an accepted socket can retain that
                // mode; the session reader would then interpret WSAEWOULDBLOCK as a
                // disconnect before the peer has time to send its first file offer.
                if stream.set_nonblocking(false).is_err() {
                    let _ = stream.shutdown(Shutdown::Both);
                    continue;
                }
                let _ = stream.set_read_timeout(Some(HANDSHAKE_TIMEOUT));
                let _ = stream.set_write_timeout(Some(HANDSHAKE_TIMEOUT));
                match read_wire(&mut stream) {
                    Ok(WireMessage::Probe(nonce)) if is_probe_nonce(&nonce) => {
                        let _ = write_wire(&mut stream, &WireMessage::ProbeAck(nonce));
                    }
                    Ok(WireMessage::Hello(hello)) => app.install_incoming(stream, hello),
                    _ => {
                        let _ = write_wire(&mut stream, &WireMessage::Reject);
                        let _ = stream.shutdown(Shutdown::Both);
                    }
                }
            }
            Err(error) if error.kind() == io::ErrorKind::WouldBlock => {
                thread::sleep(Duration::from_millis(50))
            }
            Err(_) => break,
        }
    }
}

fn confirm_peer(peer: &Peer) -> bool {
    for address in &peer.addresses {
        let socket = SocketAddr::new(*address, peer.port);
        let Ok(mut stream) = TcpStream::connect_timeout(&socket, PROBE_TIMEOUT) else {
            continue;
        };
        let _ = stream.set_read_timeout(Some(PROBE_TIMEOUT));
        let _ = stream.set_write_timeout(Some(PROBE_TIMEOUT));
        let nonce = create_probe_nonce();
        if write_wire(&mut stream, &WireMessage::Probe(nonce.clone())).is_ok()
            && matches!(read_wire(&mut stream), Ok(WireMessage::ProbeAck(value)) if value == nonce)
        {
            let _ = stream.shutdown(Shutdown::Both);
            return true;
        }
    }
    false
}

fn connect_worker(app: Arc<TransferEngine>, peer: Peer, cancel: Arc<AtomicBool>) {
    let mut last_error = "无法连接到所选设备。".to_string();
    for address in &peer.addresses {
        if cancel.load(Ordering::Acquire) {
            return;
        }
        let socket = SocketAddr::new(*address, peer.port);
        match TcpStream::connect_timeout(&socket, HANDSHAKE_TIMEOUT) {
            Ok(mut stream) => {
                let _ = stream.set_read_timeout(Some(HANDSHAKE_TIMEOUT));
                let _ = stream.set_write_timeout(Some(HANDSHAKE_TIMEOUT));
                let hello = WireMessage::Hello(HelloWire {
                    platform: "windows".to_string(),
                    name: app.friendly_name.clone(),
                    device_id: app.device_id.clone(),
                });
                if write_wire(&mut stream, &hello).is_err() {
                    last_error = "无法发送连接请求。".to_string();
                    continue;
                }
                match read_wire(&mut stream) {
                    Ok(WireMessage::Accept) => {
                        let _ = stream.set_read_timeout(None);
                        let _ = stream.set_write_timeout(None);
                        let session = Session::new(stream, peer.clone());
                        if app.set_connected(Arc::clone(&session)) {
                            spawn_session_reader(app, session);
                        } else {
                            session.close();
                        }
                        return;
                    }
                    Ok(WireMessage::Reject) => last_error = "对方拒绝了连接请求。".to_string(),
                    Ok(_) => last_error = "设备返回了无效的连接响应。".to_string(),
                    Err(error) => last_error = format!("连接握手失败：{error}"),
                }
            }
            Err(error) => last_error = format!("无法连接到设备：{error}"),
        }
    }
    app.finish_connect_failure(&cancel, last_error);
}

fn incoming_approval_worker(app: Arc<TransferEngine>, session: Arc<Session>) {
    let decision = wait_decision(
        &session.connection_decision,
        &session.cancelled,
        DECISION_TIMEOUT,
    );
    match decision {
        Some(true) if !session.cancelled.load(Ordering::Acquire) => {
            let write_result = {
                let mut stream = lock_recover(&session.stream);
                write_wire(&mut *stream, &WireMessage::Accept)
            };
            if write_result.is_ok() {
                let _ = session
                    .stream
                    .lock()
                    .map(|stream| stream.set_read_timeout(None));
                let _ = session
                    .stream
                    .lock()
                    .map(|stream| stream.set_write_timeout(None));
                if app.set_connected(Arc::clone(&session)) {
                    spawn_session_reader(app, session);
                    return;
                }
            }
        }
        _ => {
            let mut stream = lock_recover(&session.stream);
            let _ = write_wire(&mut *stream, &WireMessage::Reject);
        }
    }
    session.close();
    app.clear_session_if(&session, None);
}

fn spawn_session_reader(app: Arc<TransferEngine>, session: Arc<Session>) {
    thread::spawn(move || session_reader(app, session));
}

fn session_reader(app: Arc<TransferEngine>, session: Arc<Session>) {
    let Ok(stream) = session.stream.lock().map(|stream| stream.try_clone()) else {
        app.clear_session_if(&session, Some("无法读取连接流。".to_string()));
        return;
    };
    let Ok(mut reader) = stream else {
        app.clear_session_if(&session, Some("无法读取连接流。".to_string()));
        return;
    };
    let _ = reader.set_read_timeout(None);
    loop {
        if session.cancelled.load(Ordering::Acquire) {
            break;
        }
        match read_wire(&mut reader) {
            Ok(WireMessage::FileAccept) => session.offer_response.set(true),
            Ok(WireMessage::FileReject) => session.offer_response.set(false),
            Ok(WireMessage::FileResult(ok)) => session.result_response.set(ok),
            Ok(WireMessage::FileOffer(offer)) => receive_offer(&app, &session, &mut reader, offer),
            Ok(_) => {
                app.clear_session_if(&session, Some("设备发送了无效的传输消息。".to_string()));
                break;
            }
            Err(_) => {
                if !session.cancelled.load(Ordering::Acquire) {
                    app.clear_session_if(&session, Some("设备连接已断开。".to_string()));
                }
                break;
            }
        }
    }
}

fn receive_offer(
    app: &TransferEngine,
    session: &Arc<Session>,
    reader: &mut TcpStream,
    offer: FileOfferWire,
) {
    diag::record(
        L::Information,
        E::TransferOfferReceived,
        R::None,
        session.peer.device_id.as_deref(),
        None,
        false,
    );
    if !is_safe_file_name(&offer.name) || offer.size > MAX_FILE_BYTES {
        let _ = send_wire(session, &WireMessage::FileReject);
        return;
    }
    let (automatic, notify_result) = {
        let mut state = lock_recover(&app.state);
        if state.transfer.is_some() {
            drop(state);
            let _ = send_wire(session, &WireMessage::FileReject);
            return;
        }
        session.file_decision.clear();
        let automatic = default_destination(&state.receive, &offer.name, true);
        state.incoming_file = if automatic.is_some() {
            None
        } else {
            Some(IncomingFile {
                name: offer.name.clone(),
                size: offer.size,
            })
        };
        state.transfer = Some(TransferProgress {
            name: offer.name.clone(),
            completed: 0,
            total: offer.size,
            receiving: true,
            phase: TransferPhase::WaitingAcceptance,
        });
        state.last_completed = None;
        state.last_error = None;
        (automatic, state.receive.auto_accept)
    };
    let decision = automatic
        .map(FileDecision::Accept)
        .or_else(|| wait_decision(&session.file_decision, &session.cancelled, DECISION_TIMEOUT));
    let Some(FileDecision::Accept(destination)) = decision else {
        diag::record(
            L::Information,
            E::TransferRejected,
            R::None,
            session.peer.device_id.as_deref(),
            None,
            false,
        );
        let _ = send_wire(session, &WireMessage::FileReject);
        app.finish_receive(
            session,
            &offer,
            notify_result,
            Some("已拒绝接收文件或接收确认已超时。".to_string()),
        );
        return;
    };
    diag::record(
        L::Information,
        E::TransferAccepted,
        R::None,
        session.peer.device_id.as_deref(),
        None,
        false,
    );
    {
        let mut state = lock_recover(&app.state);
        state.incoming_file = None;
    }
    if send_wire(session, &WireMessage::FileAccept).is_err() {
        app.finish_receive(
            session,
            &offer,
            notify_result,
            Some("无法确认接收文件。".to_string()),
        );
        return;
    }
    app.mark_transferring();
    let result = receive_bytes(app, session, reader, &offer, &destination);
    let ok = result.is_ok();
    let _ = send_wire(session, &WireMessage::FileResult(ok));
    match result {
        Ok(()) => app.finish_receive(session, &offer, notify_result, None),
        Err(error) => app.finish_receive(session, &offer, notify_result, Some(error.message)),
    }
}

pub(crate) fn format_file_size(bytes: u64) -> String {
    let units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
    let mut size = bytes as f64;
    let mut unit = 0;
    while size >= 1024.0 && unit < units.len() - 1 {
        size /= 1024.0;
        unit += 1;
    }
    size = (size * 100.0).round() / 100.0;
    if size >= 1024.0 && unit < units.len() - 1 {
        size /= 1024.0;
        unit += 1;
    }
    let number = format!("{size:.2}");
    format!(
        "{} {}",
        number.trim_end_matches('0').trim_end_matches('.'),
        units[unit]
    )
}

fn receive_bytes(
    app: &TransferEngine,
    session: &Arc<Session>,
    reader: &mut TcpStream,
    offer: &FileOfferWire,
    destination: &Path,
) -> Result<(), TransferError> {
    let parent = destination
        .parent()
        .ok_or_else(|| TransferError::new("destination_invalid", "接收目录无效。"))?;
    fs::create_dir_all(parent)
        .map_err(|error| io_error("destination_unavailable", "无法创建接收目录", error))?;
    if destination.exists() {
        return Err(TransferError::new("destination_exists", "目标文件已存在。"));
    }
    let temporary = parent.join(format!(".{}.qingtransfer.part", offer.name));
    let _ = fs::remove_file(&temporary);
    let mut output = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temporary)
        .map_err(|error| io_error("destination_unavailable", "无法创建临时文件", error))?;
    let mut hash = Sha256::new();
    let mut remaining = offer.size;
    let mut completed = 0u64;
    let mut buffer = vec![0u8; NETWORK_BUFFER_BYTES];
    let result = (|| {
        while remaining > 0 {
            if session.cancelled.load(Ordering::Acquire) {
                return Err(TransferError::new("cancelled", "传输已取消。"));
            }
            let wanted = remaining.min(buffer.len() as u64) as usize;
            let read = reader
                .read(&mut buffer[..wanted])
                .map_err(|error| io_error("transfer_failed", "读取传输数据失败", error))?;
            if read == 0 {
                return Err(TransferError::new(
                    "transfer_failed",
                    "设备提前关闭了连接。",
                ));
            }
            hash.update(&buffer[..read]);
            output
                .write_all(&buffer[..read])
                .map_err(|error| io_error("destination_unavailable", "写入接收文件失败", error))?;
            remaining -= read as u64;
            completed += read as u64;
            app.update_progress(&offer.name, completed, offer.size, true);
        }
        output
            .sync_all()
            .map_err(|error| io_error("destination_unavailable", "保存接收文件失败", error))?;
        let expected = hex_lower(&hash.finalize());
        let end = read_wire(reader)
            .map_err(|error| io_error("transfer_failed", "读取文件校验信息失败", error))?;
        let WireMessage::FileEnd(actual) = end else {
            return Err(TransferError::new(
                "transfer_failed",
                "设备未发送文件校验信息。",
            ));
        };
        if actual != expected {
            return Err(TransferError::new("transfer_failed", "文件校验失败。"));
        }
        fs::rename(&temporary, destination)
            .map_err(|error| io_error("destination_unavailable", "无法发布接收文件", error))?;
        Ok(())
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result
}

fn send_file_worker(app: Arc<TransferEngine>, session: Arc<Session>, path: PathBuf) {
    let name = path
        .file_name()
        .and_then(|value| value.to_str())
        .unwrap_or("file")
        .to_string();
    let total = fs::metadata(&path)
        .map(|metadata| metadata.len())
        .unwrap_or(0);
    let offer = WireMessage::FileOffer(FileOfferWire {
        name: name.clone(),
        size: total,
    });
    if send_wire(&session, &offer).is_err() {
        app.clear_transfer(Some("无法发送文件请求。".to_string()));
        return;
    }
    diag::record(
        L::Information,
        E::TransferOfferSent,
        R::None,
        session.peer.device_id.as_deref(),
        None,
        false,
    );
    let accepted = wait_decision(
        &session.offer_response,
        &session.cancelled,
        DECISION_TIMEOUT,
    )
    .unwrap_or(false);
    if !accepted {
        diag::record(
            L::Information,
            E::TransferRejected,
            R::None,
            session.peer.device_id.as_deref(),
            None,
            false,
        );
        app.clear_transfer(Some("对方拒绝了文件。".to_string()));
        return;
    }
    diag::record(
        L::Information,
        E::TransferAccepted,
        R::None,
        session.peer.device_id.as_deref(),
        None,
        false,
    );
    app.mark_transferring();
    let result = (|| -> Result<(), TransferError> {
        let mut input = File::open(&path)
            .map_err(|error| io_error("file_unavailable", "无法打开要发送的文件", error))?;
        let mut hash = Sha256::new();
        let mut completed = 0u64;
        let mut buffer = vec![0u8; NETWORK_BUFFER_BYTES];
        loop {
            if session.cancelled.load(Ordering::Acquire) {
                return Err(TransferError::new("cancelled", "传输已取消。"));
            }
            let read = input
                .read(&mut buffer)
                .map_err(|error| io_error("transfer_failed", "读取发送文件失败", error))?;
            if read == 0 {
                break;
            }
            hash.update(&buffer[..read]);
            {
                let mut stream = lock_recover(&session.stream);
                stream
                    .write_all(&buffer[..read])
                    .map_err(|error| io_error("transfer_failed", "发送文件数据失败", error))?;
            }
            completed += read as u64;
            app.update_progress(&name, completed, total, false);
        }
        let digest = hex_lower(&hash.finalize());
        send_wire(&session, &WireMessage::FileEnd(digest))
            .map_err(|error| io_error("transfer_failed", "发送文件校验信息失败", error))?;
        if !wait_decision(
            &session.result_response,
            &session.cancelled,
            DECISION_TIMEOUT,
        )
        .unwrap_or(false)
        {
            return Err(TransferError::new(
                "transfer_failed",
                "接收设备未能保存文件。",
            ));
        }
        Ok(())
    })();
    match result {
        Ok(()) => app.clear_transfer(None),
        Err(error) => app.clear_transfer(Some(error.message)),
    }
}

fn send_wire(session: &Arc<Session>, message: &WireMessage) -> io::Result<()> {
    let mut stream = lock_recover(&session.stream);
    write_wire(&mut *stream, message)
}

#[derive(Debug, Clone)]
struct HelloWire {
    platform: String,
    name: String,
    device_id: Option<String>,
}

#[derive(Debug, Clone)]
struct FileOfferWire {
    name: String,
    size: u64,
}

#[derive(Debug, Clone)]
enum WireMessage {
    Hello(HelloWire),
    Probe(String),
    ProbeAck(String),
    Accept,
    Reject,
    FileOffer(FileOfferWire),
    FileAccept,
    FileReject,
    FileEnd(String),
    FileResult(bool),
}

fn write_wire(stream: &mut impl Write, message: &WireMessage) -> io::Result<()> {
    let value = match message {
        WireMessage::Hello(value) => match &value.device_id {
            Some(device_id) => json!({ "type": "hello", "v": NETWORK_PROTOCOL_VERSION,
                    "pf": value.platform, "name": value.name, "id": device_id }),
            None => json!({ "type": "hello", "v": NETWORK_PROTOCOL_VERSION,
                    "pf": value.platform, "name": value.name }),
        },
        WireMessage::Probe(nonce) => {
            json!({ "type": "probe", "v": NETWORK_PROTOCOL_VERSION, "nonce": nonce })
        }
        WireMessage::ProbeAck(nonce) => {
            json!({ "type": "probe_ack", "v": NETWORK_PROTOCOL_VERSION, "nonce": nonce })
        }
        WireMessage::Accept => json!({ "type": "accept", "v": NETWORK_PROTOCOL_VERSION }),
        WireMessage::Reject => json!({ "type": "reject", "v": NETWORK_PROTOCOL_VERSION }),
        WireMessage::FileOffer(value) => {
            json!({ "type": "file_offer", "v": NETWORK_PROTOCOL_VERSION, "name": value.name, "size": value.size })
        }
        WireMessage::FileAccept => json!({ "type": "file_accept", "v": NETWORK_PROTOCOL_VERSION }),
        WireMessage::FileReject => json!({ "type": "file_reject", "v": NETWORK_PROTOCOL_VERSION }),
        WireMessage::FileEnd(hash) => {
            json!({ "type": "file_end", "v": NETWORK_PROTOCOL_VERSION, "sha256": hash })
        }
        WireMessage::FileResult(ok) => {
            json!({ "type": "file_result", "v": NETWORK_PROTOCOL_VERSION, "ok": ok })
        }
    };
    let payload = serde_json::to_vec(&value)
        .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
    if payload.is_empty() || payload.len() > NETWORK_MAX_FRAME_BYTES {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "network frame too large",
        ));
    }
    let length = (payload.len() as u32).to_be_bytes();
    stream.write_all(&length)?;
    stream.write_all(&payload)?;
    stream.flush()
}

fn read_wire(stream: &mut impl Read) -> io::Result<WireMessage> {
    let mut header = [0u8; 4];
    stream.read_exact(&mut header)?;
    let length = u32::from_be_bytes(header) as usize;
    if length == 0 || length > NETWORK_MAX_FRAME_BYTES {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "network frame too large",
        ));
    }
    let mut payload = vec![0u8; length];
    stream.read_exact(&mut payload)?;
    decode_wire(&payload)
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "invalid network message"))
}

fn decode_wire(payload: &[u8]) -> Option<WireMessage> {
    let value = serde_json::from_slice::<Value>(payload).ok()?;
    let object = value.as_object()?;
    let kind = object.get("type")?.as_str()?;
    if object.get("v")?.as_u64()? != u64::from(NETWORK_PROTOCOL_VERSION) {
        return None;
    }
    match kind {
        "accept" if exact_keys(object, &["type", "v"]) => Some(WireMessage::Accept),
        "reject" if exact_keys(object, &["type", "v"]) => Some(WireMessage::Reject),
        "file_accept" if exact_keys(object, &["type", "v"]) => Some(WireMessage::FileAccept),
        "file_reject" if exact_keys(object, &["type", "v"]) => Some(WireMessage::FileReject),
        "probe" | "probe_ack" if exact_keys(object, &["type", "v", "nonce"]) => {
            let nonce = object.get("nonce")?.as_str()?.to_string();
            if !is_probe_nonce(&nonce) {
                return None;
            }
            if kind == "probe" {
                Some(WireMessage::Probe(nonce))
            } else {
                Some(WireMessage::ProbeAck(nonce))
            }
        }
        "hello"
            if exact_keys(object, &["type", "v", "pf", "name", "id"])
                || exact_keys(object, &["type", "v", "pf", "name"]) =>
        {
            let platform = safe_field(object.get("pf")?.as_str(), MAX_FIELD_LENGTH)?;
            let name = safe_field(object.get("name")?.as_str(), MAX_DISPLAY_NAME_LENGTH)?;
            if platform != "windows" && platform != "android" {
                return None;
            }
            let device_id = object
                .get("id")
                .and_then(Value::as_str)
                .map(str::to_ascii_lowercase);
            if device_id.as_ref().is_some_and(|id| !is_device_id(id)) {
                return None;
            }
            Some(WireMessage::Hello(HelloWire {
                platform,
                name,
                device_id,
            }))
        }
        "file_offer" if exact_keys(object, &["type", "v", "name", "size"]) => {
            let name = object.get("name")?.as_str()?.to_string();
            let size = object.get("size")?.as_u64()?;
            if !is_safe_file_name(&name) || size > MAX_FILE_BYTES {
                return None;
            }
            Some(WireMessage::FileOffer(FileOfferWire { name, size }))
        }
        "file_end" if exact_keys(object, &["type", "v", "sha256"]) => {
            let hash = object.get("sha256")?.as_str()?.to_string();
            if !is_sha256(&hash) {
                return None;
            }
            Some(WireMessage::FileEnd(hash))
        }
        "file_result" if exact_keys(object, &["type", "v", "ok"]) => {
            Some(WireMessage::FileResult(object.get("ok")?.as_bool()?))
        }
        _ => None,
    }
}

fn exact_keys(object: &Map<String, Value>, expected: &[&str]) -> bool {
    object.len() == expected.len() && expected.iter().all(|key| object.contains_key(*key))
}

fn is_probe_nonce(value: &str) -> bool {
    value.len() == 32 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn is_device_id(value: &str) -> bool {
    value.len() == 32
        && value
            .bytes()
            .all(|byte| byte.is_ascii_hexdigit() && !byte.is_ascii_uppercase())
}

fn is_sha256(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn is_safe_file_name(value: &str) -> bool {
    !value.is_empty()
        && value.chars().count() <= MAX_FILE_NAME_LENGTH
        && value != "."
        && value != ".."
        && !value.chars().any(char::is_control)
        && !value
            .chars()
            .any(|character| matches!(character, '<' | '>' | ':' | '"' | '|' | '?' | '*'))
        && !value.contains(['/', '\\'])
        && !value.ends_with(['.', ' '])
}

fn validate_existing_file(raw: &str) -> Result<PathBuf, TransferError> {
    validate_path_string(raw, "文件路径无效。")?;
    let path =
        fs::canonicalize(raw).map_err(|error| io_error("file_unavailable", "文件不可用", error))?;
    let metadata =
        fs::metadata(&path).map_err(|error| io_error("file_unavailable", "文件不可用", error))?;
    if !metadata.is_file() {
        return Err(TransferError::new(
            "file_invalid",
            "请选择一个文件，暂不支持发送文件夹。",
        ));
    }
    if metadata.len() > MAX_FILE_BYTES {
        return Err(TransferError::new("file_invalid", "文件大小超出支持范围。"));
    }
    let name = path
        .file_name()
        .and_then(|value| value.to_str())
        .unwrap_or_default();
    if !is_safe_file_name(name) {
        return Err(TransferError::new("file_invalid", "文件名无效。"));
    }
    Ok(path)
}

fn validate_directory_path(raw: &str) -> Result<PathBuf, TransferError> {
    validate_path_string(raw, "目录路径无效。")?;
    let path = fs::canonicalize(raw)
        .map_err(|error| io_error("directory_unavailable", "目录不可用", error))?;
    if !path.is_dir() {
        return Err(TransferError::new("directory_invalid", "必须选择文件夹。"));
    }
    Ok(path)
}

fn validate_destination_path(raw: &str) -> Result<PathBuf, TransferError> {
    validate_path_string(raw, "目标路径无效。")?;
    let path = PathBuf::from(raw);
    let name = path
        .file_name()
        .and_then(|value| value.to_str())
        .unwrap_or_default();
    if !is_safe_file_name(name) {
        return Err(TransferError::new(
            "destination_invalid",
            "目标文件名无效。",
        ));
    }
    let parent = path
        .parent()
        .ok_or_else(|| TransferError::new("destination_invalid", "目标目录无效。"))?;
    if !parent.is_absolute() {
        return Err(TransferError::new(
            "destination_invalid",
            "目标路径必须是绝对路径。",
        ));
    }
    Ok(path)
}

fn validate_path_string(raw: &str, message: &str) -> Result<(), TransferError> {
    if raw.is_empty()
        || raw.chars().count() > 32_767
        || raw.contains('\0')
        || raw.chars().any(char::is_control)
        || !Path::new(raw).is_absolute()
    {
        return Err(TransferError::new("path_invalid", message));
    }
    Ok(())
}

fn default_destination(
    settings: &ReceiveSettings,
    name: &str,
    require_auto_accept: bool,
) -> Option<PathBuf> {
    if !settings.use_default_directory || require_auto_accept && !settings.auto_accept {
        return None;
    }
    let directory = settings.default_directory.as_deref()?.to_string();
    let directory = PathBuf::from(directory);
    if !directory.is_dir() || !is_safe_file_name(name) {
        return None;
    }
    let path = Path::new(name);
    let stem = path
        .file_stem()
        .and_then(|value| value.to_str())
        .unwrap_or("file");
    let extension = path
        .extension()
        .and_then(|value| value.to_str())
        .map(|value| format!(".{value}"))
        .unwrap_or_default();
    for index in 0..1000u32 {
        let suffix = if index == 0 {
            String::new()
        } else {
            format!(" ({index})")
        };
        let candidate = directory.join(format!("{stem}{suffix}{extension}"));
        if !candidate.exists() {
            return Some(candidate);
        }
    }
    None
}

fn settings_path(data_directory: &Path) -> PathBuf {
    data_directory.join("receive-settings.json")
}

fn migrate_receive_settings(data_directory: &Path, legacy_directory: Option<&Path>) {
    if settings_path(data_directory).exists() {
        return;
    }
    let Some(legacy) = legacy_directory else {
        return;
    };
    let path = settings_path(legacy);
    if !fs::metadata(&path).is_ok_and(|metadata| metadata.len() <= 64 * 1024) {
        return;
    }
    let Ok(bytes) = fs::read(path) else { return };
    if serde_json::from_slice::<ReceiveSettings>(&bytes).is_ok() {
        // Preserve the old file and do not overwrite host-owned preferences.
        let _ = save_receive_settings(data_directory, &load_receive_settings(legacy));
    }
}

fn load_receive_settings(data_directory: &Path) -> ReceiveSettings {
    let path = settings_path(data_directory);
    if !fs::metadata(&path).is_ok_and(|metadata| metadata.len() <= 64 * 1024) {
        return ReceiveSettings::default();
    }
    let Ok(bytes) = fs::read(path) else {
        return ReceiveSettings::default();
    };
    let Ok(mut settings) = serde_json::from_slice::<ReceiveSettings>(&bytes) else {
        return ReceiveSettings::default();
    };
    if let Some(directory) = settings.default_directory.as_deref() {
        settings.default_directory = Path::new(directory)
            .is_absolute()
            .then(|| directory.to_string());
    }
    settings
}

fn save_receive_settings(
    data_directory: &Path,
    settings: &ReceiveSettings,
) -> Result<(), TransferError> {
    fs::create_dir_all(data_directory)
        .map_err(|error| io_error("settings_unavailable", "无法创建接收设置目录", error))?;
    let path = settings_path(data_directory);
    let temporary = path.with_extension(format!("json.tmp.{}", unique_id()));
    let bytes = serde_json::to_vec_pretty(settings)
        .map_err(|error| TransferError::new("settings_unavailable", error.to_string()))?;
    fs::write(&temporary, bytes)
        .map_err(|error| io_error("settings_unavailable", "无法保存接收设置", error))?;
    if let Err(error) = replace_file(&temporary, &path) {
        let _ = fs::remove_file(&temporary);
        return Err(error);
    }
    Ok(())
}

fn replace_file(temporary: &Path, destination: &Path) -> Result<(), TransferError> {
    if destination.exists() {
        let backup = destination.with_extension(format!("json.bak-{}", unique_id()));
        fs::rename(destination, &backup)
            .map_err(|error| io_error("settings_unavailable", "无法替换接收设置", error))?;
        if let Err(error) = fs::rename(temporary, destination) {
            let _ = fs::rename(&backup, destination);
            return Err(io_error("settings_unavailable", "无法写入接收设置", error));
        }
        let _ = fs::remove_file(backup);
    } else {
        fs::rename(temporary, destination)
            .map_err(|error| io_error("settings_unavailable", "无法写入接收设置", error))?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    struct TestEngine {
        app: Arc<TransferEngine>,
        directory: PathBuf,
    }

    impl TestEngine {
        fn new(name: &str, identity: &str) -> Self {
            let directory = env::temp_dir().join(format!("qing-host-transfer-{}", unique_id()));
            let app = Arc::new(TransferEngine::new(
                directory.clone(),
                None,
                name,
                Some(identity.to_string()),
            ));
            Self { app, directory }
        }
    }

    impl Drop for TestEngine {
        fn drop(&mut self) {
            self.app.shutdown();
            let _ = fs::remove_dir_all(&self.directory);
        }
    }

    fn until(mut ready: impl FnMut() -> bool) {
        let deadline = std::time::Instant::now() + Duration::from_secs(8);
        while !ready() {
            assert!(
                std::time::Instant::now() < deadline,
                "transfer state timed out"
            );
            thread::sleep(Duration::from_millis(10));
        }
    }

    fn peer(identity: &str, platform: &str, port: u16) -> Peer {
        Peer {
            service_name: format!("test-{identity}"),
            display_name: "Test device".to_string(),
            platform: platform.to_string(),
            protocol_version: "1".to_string(),
            capabilities: vec!["file".to_string()],
            addresses: vec!["127.0.0.1".parse().unwrap()],
            port,
            device_id: Some(identity.to_string()),
            online: true,
            last_seen: None,
        }
    }

    #[test]
    fn host_engine_needs_no_manifest_and_migrates_preferences_once() {
        let old = TestEngine::new("Old", "11111111111111111111111111111111");
        fs::create_dir_all(&old.directory).unwrap();
        let settings = ReceiveSettings {
            default_directory: Some(old.directory.to_string_lossy().to_string()),
            use_default_directory: true,
            auto_accept: true,
        };
        save_receive_settings(&old.directory, &settings).unwrap();
        let new = TestEngine::new("PC", "22222222222222222222222222222222");
        migrate_receive_settings(&new.directory, Some(&old.directory));
        let migrated = load_receive_settings(&new.directory);
        assert!(migrated.use_default_directory && migrated.auto_accept);
        assert!(settings_path(&old.directory).is_file());
        save_receive_settings(&new.directory, &ReceiveSettings::default()).unwrap();
        migrate_receive_settings(&new.directory, Some(&old.directory));
        assert!(!load_receive_settings(&new.directory).auto_accept);
        assert_eq!(new.app.snapshot()["session"]["state"], "Idle");
        assert_eq!(
            new.app
                .invoke("sendFile", &json!({"path": "relative.txt"}))
                .unwrap_err()
                .code,
            "transfer_inactive"
        );
        assert!(!new.directory.join("module.json").exists());
    }

    #[test]
    fn paired_target_is_resolved_by_identity_not_a_client_endpoint() {
        let engine = TestEngine::new("PC", "11111111111111111111111111111111");
        let mut wrong = peer("22222222222222222222222222222222", "android", 12345);
        // Same IP but a different advertised identity must not match.
        wrong.addresses = vec!["127.0.0.1".parse().unwrap()];
        lock_recover(&engine.app.state)
            .peers
            .insert(wrong.service_name.clone(), wrong);
        let target = crate::devices::TransferTarget {
            device_id: "33333333333333333333333333333333".to_string(),
            platform: "android".to_string(),
            addresses: vec!["127.0.0.1".to_string()],
        };
        assert_eq!(
            engine.app.connect_target(&target).unwrap_err().code,
            "peer_unavailable"
        );
    }

    #[test]
    fn host_engine_streams_in_both_directions_with_receiver_confirmation() {
        let pc = TestEngine::new("PC", "11111111111111111111111111111111");
        let phone = TestEngine::new("Phone fixture", "22222222222222222222222222222222");
        pc.app.active.store(true, Ordering::Release);
        phone.app.active.store(true, Ordering::Release);
        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let address = listener.local_addr().unwrap();
        let remote = peer(
            phone.app.device_id.as_deref().unwrap(),
            "android",
            address.port(),
        );
        lock_recover(&pc.app.state)
            .peers
            .insert(remote.service_name.clone(), remote.clone());
        let server = Arc::clone(&phone.app);
        // Use a real socket and the existing Android wire format; no mDNS or module process.
        let accept = thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            stream.set_read_timeout(Some(HANDSHAKE_TIMEOUT)).unwrap();
            let WireMessage::Hello(hello) = read_wire(&mut stream).unwrap() else {
                panic!("missing hello")
            };
            server.install_incoming(stream, hello);
        });
        pc.app
            .connect_target(&crate::devices::TransferTarget {
                device_id: remote.device_id.unwrap(),
                platform: "android".to_string(),
                addresses: vec!["127.0.0.1".to_string()],
            })
            .unwrap();
        until(|| phone.app.snapshot()["session"]["state"] == "WaitingApproval");
        phone
            .app
            .invoke("acceptIncomingConnection", &json!({}))
            .unwrap();
        until(|| {
            pc.app.snapshot()["session"]["state"] == "Connected"
                && phone.app.snapshot()["session"]["state"] == "Connected"
        });
        accept.join().unwrap();
        fs::create_dir_all(&pc.directory).unwrap();
        fs::create_dir_all(&phone.directory).unwrap();
        phone.app.invoke("setReceivePreferences", &json!({
            "defaultDirectory": phone.directory.to_string_lossy(), "useDefaultDirectory": true, "autoAccept": false,
        })).unwrap();
        let outgoing = pc.directory.join("pc-file.bin");
        let bytes = vec![0x5au8; NETWORK_BUFFER_BYTES * 2 + 17];
        fs::write(&outgoing, &bytes).unwrap();
        pc.app
            .invoke("sendFile", &json!({"path": outgoing.to_string_lossy()}))
            .unwrap();
        until(|| phone.app.snapshot()["incomingFile"]["name"] == "pc-file.bin");
        assert_eq!(pc.app.snapshot()["transfer"]["phase"], "WaitingAcceptance");
        assert_eq!(
            phone.app.snapshot()["transfer"]["phase"],
            "WaitingAcceptance"
        );
        assert!(
            !phone.directory.join("pc-file.bin").exists(),
            "must wait for receiver approval"
        );
        phone
            .app
            .invoke("acceptIncomingFileDefault", &json!({}))
            .unwrap();
        until(|| {
            pc.app.snapshot()["lastCompleted"] == "pc-file.bin"
                && phone.app.snapshot()["lastCompleted"] == "pc-file.bin"
        });
        assert_eq!(
            fs::read(phone.directory.join("pc-file.bin")).unwrap(),
            bytes
        );
        pc.app.invoke("setReceivePreferences", &json!({
            "defaultDirectory": pc.directory.to_string_lossy(), "useDefaultDirectory": true, "autoAccept": true,
        })).unwrap();
        let incoming = phone.directory.join("android-file.bin");
        fs::write(&incoming, b"Android to PC").unwrap();
        phone
            .app
            .invoke("sendFile", &json!({"path": incoming.to_string_lossy()}))
            .unwrap();
        until(|| {
            pc.app.snapshot()["lastCompleted"] == "android-file.bin"
                && phone.app.snapshot()["lastCompleted"] == "android-file.bin"
        });
        assert_eq!(
            fs::read(pc.directory.join("android-file.bin")).unwrap(),
            b"Android to PC"
        );
        let results = pc.app.take_receive_outcomes();
        assert_eq!(results.len(), 1);
        assert!(results[0].succeeded);
        assert_eq!(results[0].file_name, "android-file.bin");
        assert!(
            pc.app.take_receive_outcomes().is_empty(),
            "a receipt must not replay after navigation"
        );
        assert!(
            phone.app.take_receive_outcomes().is_empty(),
            "manual receive and outgoing send do not notify"
        );
        pc.app.set_enabled(false);
        assert_eq!(pc.app.snapshot()["session"]["state"], "Idle");
        assert!(!pc.app.snapshot()["discovery"]["running"].as_bool().unwrap());
    }

    #[test]
    fn probe_messages_are_strict_and_nonce_bound() {
        let value = serde_json::to_vec(
            &json!({ "type": "probe", "v": 1, "nonce": "0123456789abcdef0123456789abcdef" }),
        )
        .unwrap();
        assert!(matches!(decode_wire(&value), Some(WireMessage::Probe(_))));
        let value =
            serde_json::to_vec(&json!({ "type": "probe", "v": 1, "nonce": "bad", "extra": true }))
                .unwrap();
        assert!(decode_wire(&value).is_none());
    }

    #[test]
    fn hello_carries_a_valid_discovery_identity() {
        let value = serde_json::to_vec(&json!({
            "type": "hello", "v": 1, "pf": "android", "name": "Phone",
            "id": "0123456789abcdef0123456789abcdef"
        }))
        .unwrap();
        assert!(
            matches!(decode_wire(&value), Some(WireMessage::Hello(HelloWire {
            device_id: Some(id), ..
        })) if id == "0123456789abcdef0123456789abcdef")
        );
        let invalid = serde_json::to_vec(&json!({
            "type": "hello", "v": 1, "pf": "android", "name": "Phone", "id": "bad"
        }))
        .unwrap();
        assert!(decode_wire(&invalid).is_none());
    }

    #[test]
    fn file_offer_rejects_path_traversal_and_oversized_payloads() {
        let value = serde_json::to_vec(
            &json!({ "type": "file_offer", "v": 1, "name": "..\\secret.txt", "size": 1 }),
        )
        .unwrap();
        assert!(decode_wire(&value).is_none());
        let value = serde_json::to_vec(
            &json!({ "type": "file_offer", "v": 1, "name": "ok.txt", "size": MAX_FILE_BYTES + 1 }),
        )
        .unwrap();
        assert!(decode_wire(&value).is_none());
    }

    #[test]
    fn destination_requires_absolute_safe_filename() {
        assert!(validate_destination_path("relative.txt").is_err());
        assert!(validate_destination_path("C:\\out\\..\\bad.txt").is_ok());
        assert!(validate_destination_path("C:\\out\\bad<name>.txt").is_err());
    }

    #[test]
    fn automatic_destination_avoids_existing_names() {
        let temp = env::temp_dir().join(format!("qing-transfer-test-{}", unique_id()));
        fs::create_dir_all(&temp).unwrap();
        fs::write(temp.join("a.txt"), b"x").unwrap();
        let settings = ReceiveSettings {
            default_directory: Some(temp.to_string_lossy().to_string()),
            use_default_directory: true,
            auto_accept: true,
        };
        assert_eq!(
            default_destination(&settings, "a.txt", true)
                .unwrap()
                .file_name()
                .unwrap(),
            "a (1).txt"
        );
        let _ = fs::remove_dir_all(temp);
    }

    #[test]
    fn manual_accept_can_use_default_directory_without_auto_accept() {
        let temp = env::temp_dir().join(format!("qing-transfer-default-test-{}", unique_id()));
        fs::create_dir_all(&temp).unwrap();
        let settings = ReceiveSettings {
            default_directory: Some(temp.to_string_lossy().to_string()),
            use_default_directory: true,
            auto_accept: false,
        };
        assert!(default_destination(&settings, "a.txt", false).is_some());
        assert!(default_destination(&settings, "a.txt", true).is_none());
        let _ = fs::remove_dir_all(temp);
    }

    #[test]
    fn automatic_receive_checksum_failure_is_reported_once_and_discards_partial_file() {
        let receiver = TestEngine::new("PC", "11111111111111111111111111111111");
        fs::create_dir_all(&receiver.directory).unwrap();
        lock_recover(&receiver.app.state).receive = ReceiveSettings {
            default_directory: Some(receiver.directory.to_string_lossy().to_string()),
            use_default_directory: true,
            auto_accept: true,
        };
        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let mut remote = TcpStream::connect(listener.local_addr().unwrap()).unwrap();
        remote
            .set_read_timeout(Some(Duration::from_secs(5)))
            .unwrap();
        let (stream, _) = listener.accept().unwrap();
        let session = Session::new(
            stream,
            peer("22222222222222222222222222222222", "android", 12345),
        );
        let app = Arc::clone(&receiver.app);
        let receive = thread::spawn(move || {
            let mut reader = session.stream.lock().unwrap().try_clone().unwrap();
            receive_offer(
                &app,
                &session,
                &mut reader,
                FileOfferWire {
                    name: "corrupt.bin".to_string(),
                    size: 1,
                },
            );
        });
        assert!(matches!(
            read_wire(&mut remote).unwrap(),
            WireMessage::FileAccept
        ));
        assert!(
            receiver.app.snapshot()["incomingFile"].is_null(),
            "automatic receive never asks for approval"
        );
        remote.write_all(b"x").unwrap();
        write_wire(&mut remote, &WireMessage::FileEnd("0".repeat(64))).unwrap();
        assert!(matches!(
            read_wire(&mut remote).unwrap(),
            WireMessage::FileResult(false)
        ));
        receive.join().unwrap();
        let results = receiver.app.take_receive_outcomes();
        assert_eq!(results.len(), 1);
        assert!(!results[0].succeeded);
        assert_eq!(results[0].file_name, "corrupt.bin");
        assert!(!receiver.directory.join("corrupt.bin").exists());
        assert!(receiver.app.take_receive_outcomes().is_empty());
    }

    #[test]
    fn receive_result_queue_is_bounded_and_keeps_distinct_file_attempts() {
        let receiver = TestEngine::new("PC", "11111111111111111111111111111111");
        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let _remote = TcpStream::connect(listener.local_addr().unwrap()).unwrap();
        let (stream, _) = listener.accept().unwrap();
        let session = Session::new(
            stream,
            peer("22222222222222222222222222222222", "android", 12345),
        );
        let offer = FileOfferWire {
            name: "same-file.bin".to_string(),
            size: 0,
        };
        receiver.app.finish_receive(&session, &offer, false, None);
        assert!(receiver.app.take_receive_outcomes().is_empty());
        for _ in 0..40 {
            receiver.app.finish_receive(&session, &offer, true, None);
        }
        let results = receiver.app.take_receive_outcomes();
        assert_eq!(results.len(), 32);
        assert_eq!(
            results
                .iter()
                .map(|item| &item.id)
                .collect::<std::collections::BTreeSet<_>>()
                .len(),
            32
        );
    }

    #[test]
    fn result_file_sizes_match_the_shell_formatter() {
        for (bytes, expected) in [
            (0, "0 B"),
            (1023, "1023 B"),
            (1024, "1 KB"),
            (1536, "1.5 KB"),
            (1048575, "1 MB"),
            (1234567, "1.18 MB"),
            (8589934592, "8 GB"),
        ] {
            assert_eq!(format_file_size(bytes), expected);
        }
    }
}
