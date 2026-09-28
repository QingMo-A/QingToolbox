//! Mutually confirmed Noise XX pairing. Discovery names/ports are untrusted;
//! only a confirmed remote static public key is persisted as a relationship.
use std::{
    collections::{BTreeMap, BTreeSet, VecDeque},
    fs,
    io::{Read, Write},
    net::{SocketAddr, TcpStream},
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Mutex,
    },
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use snow::{Builder, HandshakeState, TransportState};

const NOISE_PATTERN: &str = "Noise_XX_25519_ChaChaPoly_BLAKE2s";
const PROLOGUE: &[u8] = b"QingToolbox device pairing v1";
const MANAGEMENT_PROLOGUE: &[u8] = b"QingToolbox device management v1";
const HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(5);
const PAIR_TIMEOUT: Duration = Duration::from_secs(90);
const PRESENCE_INTERVAL: Duration = Duration::from_secs(120);
const PRESENCE_RETRY_INTERVAL: Duration = Duration::from_secs(15);
const PRESENCE_TTL: Duration = Duration::from_secs(180);
const MAX_SESSIONS: usize = 4;
const MAX_RECORDS: usize = 128;
const MAX_FRAME: usize = 1024;

#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum DeviceAction {
    Upgrade,
    Disconnect,
    Demote,
    DisconnectNotice,
    Ping,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PendingDeviceAction {
    pub session_id: String,
    pub peer_id: String,
    pub name: String,
    pub action: DeviceAction,
    pub local_approved: bool,
    #[serde(skip)]
    decision: Option<bool>,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DeviceNotice {
    pub id: String,
    pub peer_name: String,
    pub action: DeviceAction,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DeviceBattery {
    pub peer_id: String,
    pub percent: u8,
    pub charging: bool,
    pub received_at_ms: u64,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ForwardedNotification {
    pub id: String,
    pub device_name: String,
    pub app_name: String,
    pub title: String,
    pub body: String,
}

#[derive(Clone)]
pub struct OutboundPeer {
    pub discovery_id: String,
    pub addresses: Vec<SocketAddr>,
}

struct LocalIdentity {
    private: Vec<u8>,
}

impl Drop for LocalIdentity {
    fn drop(&mut self) {
        self.private.fill(0);
    }
}

#[derive(Clone, Copy, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub enum Relationship {
    Connected,
    Intimate,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PairedDevice {
    pub id: String,
    pub discovery_id: String,
    pub name: String,
    pub platform: String,
    pub relationship: Relationship,
}

#[derive(Deserialize, Serialize)]
struct PairRecords {
    version: u32,
    peers: Vec<PairedDevice>,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PendingPair {
    pub session_id: String,
    pub discovery_id: String,
    pub name: String,
    pub platform: String,
    pub code: String,
    pub incoming: bool,
    pub local_approved: bool,
    #[serde(skip)]
    decision: Option<bool>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PairingSnapshot {
    pub error: Option<String>,
    pub pending: Vec<PendingPair>,
    pub paired: Vec<PairedDevice>,
    pub actions: Vec<PendingDeviceAction>,
    pub notices: Vec<DeviceNotice>,
    pub batteries: Vec<DeviceBattery>,
    pub revocations: Vec<PairedDevice>,
    pub online: Vec<String>,
}

impl PairingSnapshot {
    pub fn unavailable(error: String) -> Self {
        Self {
            error: Some(error),
            pending: vec![],
            paired: vec![],
            actions: vec![],
            notices: vec![],
            batteries: vec![],
            revocations: vec![],
            online: vec![],
        }
    }
}

#[derive(Default)]
struct PairState {
    active: usize,
    outbound: BTreeSet<String>,
    error: Option<String>,
    pending: BTreeMap<String, PendingPair>,
    records: BTreeMap<String, PairedDevice>,
    tombstones: BTreeMap<String, PairedDevice>,
    actions: BTreeMap<String, PendingDeviceAction>,
    notices: Vec<DeviceNotice>,
    batteries: BTreeMap<String, DeviceBattery>,
    notification_queue: VecDeque<ForwardedNotification>,
    last_retry: BTreeMap<String, Instant>,
    last_ping: BTreeMap<String, Instant>,
    last_authenticated: BTreeMap<String, Instant>,
}

pub struct PairingCore {
    identity: LocalIdentity,
    discovery_id: String,
    name: String,
    records_file: Option<PathBuf>,
    tombstones_file: Option<PathBuf>,
    state: Mutex<PairState>,
}

impl PairingCore {
    pub fn new(profile: &Path, discovery_id: &str, name: &str) -> Result<Arc<Self>, String> {
        let directory = profile.join("Devices");
        fs::create_dir_all(&directory).map_err(|error| format!("无法创建设备数据目录：{error}"))?;
        let records_file = directory.join("paired.json");
        let tombstones_file = directory.join("revocations.json");
        if (records_file.exists() || tombstones_file.exists()) && !directory.join("pairing-key.dpapi").exists() {
            return Err("配对密钥已丢失；旧配对不能沿用。".to_string());
        }
        let identity = load_or_create_identity(&directory)?;
        let mut records = read_records(&records_file)?;
        let tombstones = read_records(&tombstones_file)?;
        records.retain(|id, _| !tombstones.contains_key(id));
        Ok(Arc::new(Self {
            identity,
            discovery_id: discovery_id.to_owned(),
            name: safe_name(name),
            records_file: Some(records_file),
            tombstones_file: Some(tombstones_file),
            state: Mutex::new(PairState {
                records,
                tombstones,
                ..PairState::default()
            }),
        }))
    }

    #[cfg(test)]
    pub(crate) fn ephemeral(discovery_id: &str, name: &str) -> Arc<Self> {
        Arc::new(Self {
            identity: generate_identity().unwrap(),
            discovery_id: discovery_id.to_owned(),
            name: name.to_owned(),
            records_file: None,
            tombstones_file: None,
            state: Mutex::new(PairState::default()),
        })
    }

    pub fn snapshot(&self) -> PairingSnapshot {
        let state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        PairingSnapshot {
            error: state.error.clone(),
            pending: state.pending.values().cloned().collect(),
            paired: state.records.values().cloned().collect(),
            actions: state.actions.values().cloned().collect(),
            notices: state.notices.clone(),
            batteries: state.batteries.values().cloned().collect(),
            revocations: state.tombstones.values().cloned().collect(),
            online: state.records.keys()
                .filter(|id| state.last_authenticated.get(*id)
                    .is_some_and(|time| time.elapsed() < PRESENCE_TTL))
                .cloned().collect(),
        }
    }

    pub fn take_notifications(&self) -> Vec<ForwardedNotification> {
        self.state.lock().unwrap_or_else(|error| error.into_inner())
            .notification_queue.drain(..).collect()
    }

    pub fn decide(&self, session_id: &str, approve: bool) -> Result<(), String> {
        let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        let pending = state
            .pending
            .get_mut(session_id)
            .ok_or("配对请求已过期。")?;
        if pending.decision.is_some() {
            return Err("配对请求已经确认。".to_string());
        }
        pending.decision = Some(approve);
        pending.local_approved = approve;
        Ok(())
    }

    pub fn decide_action(&self, session_id: &str, approve: bool) -> Result<(), String> {
        let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        let pending = state.actions.get_mut(session_id).ok_or("设备操作请求已过期。")?;
        if pending.decision.is_some() {
            return Err("设备操作已经确认。".to_string());
        }
        pending.decision = Some(approve);
        pending.local_approved = approve;
        Ok(())
    }

    pub fn request_action(
        self: &Arc<Self>,
        peer_id: &str,
        peer: OutboundPeer,
        action: DeviceAction,
        stop: Arc<AtomicBool>,
    ) -> Result<(), String> {
        let state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        let record = state.records.get(peer_id).ok_or("设备未配对。")?;
        if record.discovery_id != peer.discovery_id {
            return Err("发现身份与已配对设备不一致。".to_string());
        }
        if action == DeviceAction::Upgrade && record.relationship != Relationship::Connected {
            return Err("设备已经是亲密设备。".to_string());
        }
        drop(state);
        self.acquire_slot(Some(&peer.discovery_id))?;
        let core = Arc::clone(self);
        let expected_key = peer_id.to_owned();
        thread::spawn(move || {
            let discovery_id = peer.discovery_id.clone();
            let result = core.connect_and_manage(peer, &expected_key, action, stop);
            core.finish_session(result, Some(&discovery_id));
        });
        Ok(())
    }

    pub fn accept_action(self: &Arc<Self>, stream: TcpStream, stop: Arc<AtomicBool>) {
        if self.acquire_slot(None).is_err() {
            return;
        }
        let core = Arc::clone(self);
        thread::spawn(move || {
            let result = core.run_management(stream, false, None, None, stop);
            core.finish_session(result, None);
        });
    }

    #[cfg(test)]
    pub fn set_relationship(
        &self,
        peer_id: &str,
        relationship: Relationship,
    ) -> Result<(), String> {
        let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        let record = state.records.get_mut(peer_id).ok_or("设备未配对。")?;
        let previous = record.relationship;
        record.relationship = relationship;
        if let Err(error) = self.persist(&state.records) {
            state.records.get_mut(peer_id).unwrap().relationship = previous;
            return Err(error);
        }
        Ok(())
    }

    #[cfg(test)]
    pub fn revoke(&self, peer_id: &str) -> Result<(), String> {
        let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        let removed = state.records.remove(peer_id).ok_or("设备未配对。")?;
        if let Err(error) = self.persist(&state.records) {
            state.records.insert(peer_id.to_owned(), removed);
            return Err(error);
        }
        Ok(())
    }

    pub fn revoke_offline(&self, peer_id: &str) -> Result<(), String> {
        let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        let removed = state.records.get(peer_id).cloned().ok_or("设备未配对。")?;
        state.tombstones.insert(peer_id.to_owned(), removed.clone());
        if let Err(error) = self.persist_tombstones(&state.tombstones) {
            state.tombstones.remove(peer_id);
            return Err(error);
        }
        state.records.remove(peer_id);
        if let Err(error) = self.persist(&state.records) {
            state.records.insert(peer_id.to_owned(), removed);
            state.tombstones.remove(peer_id);
            let _ = self.persist_tombstones(&state.tombstones);
            return Err(error);
        }
        state.batteries.remove(peer_id);
        state.last_authenticated.remove(peer_id);
        state.last_ping.remove(peer_id);
        state.notices.push(DeviceNotice {
            id: random_id()?, peer_name: removed.name, action: DeviceAction::Disconnect,
        });
        if state.notices.len() > 16 { state.notices.remove(0); }
        Ok(())
    }

    pub fn retry_tombstone(self: &Arc<Self>, peer: OutboundPeer, stop: Arc<AtomicBool>) {
        let key = {
            let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
            let key = state.tombstones.values().find(|record| record.discovery_id == peer.discovery_id)
                .map(|record| record.id.clone());
            if let Some(key) = &key {
                if state.last_retry.get(key).is_some_and(|time| time.elapsed() < Duration::from_secs(30)) {
                    return;
                }
                state.last_retry.insert(key.clone(), Instant::now());
            }
            key
        };
        let Some(key) = key else { return };
        if self.acquire_slot(Some(&peer.discovery_id)).is_err() { return }
        let core = Arc::clone(self);
        thread::spawn(move || {
            let discovery_id = peer.discovery_id.clone();
            let result = core.connect_and_manage(peer, &key, DeviceAction::DisconnectNotice, stop);
            core.finish_session(result, Some(&discovery_id));
        });
    }

    pub fn probe_online(self: &Arc<Self>, peer: OutboundPeer, stop: Arc<AtomicBool>) {
        let key = {
            let state = self.state.lock().unwrap_or_else(|error| error.into_inner());
            let Some(record) = state.records.values().find(|record| record.discovery_id == peer.discovery_id) else {
                return;
            };
            if state.last_ping.get(&record.id)
                .is_some_and(|time| time.elapsed() < PRESENCE_INTERVAL) { return; }
            record.id.clone()
        };
        if self.acquire_slot(None).is_err() { return; }
        self.state.lock().unwrap_or_else(|error| error.into_inner())
            .last_ping.insert(key.clone(), Instant::now());
        let core = Arc::clone(self);
        thread::spawn(move || {
            let result = core.connect_and_manage(peer, &key, DeviceAction::Ping, Arc::clone(&stop));
            if result.is_err() && !stop.load(Ordering::Acquire) {
                let mut state = core.state.lock().unwrap_or_else(|error| error.into_inner());
                if state.records.contains_key(&key) {
                    state.last_ping.insert(key, Instant::now() - (PRESENCE_INTERVAL - PRESENCE_RETRY_INTERVAL));
                }
            }
            // A missed heartbeat is normal offline state, not a user action error.
            core.finish_session(Ok(()), None);
        });
    }

    pub fn cancel_pending(&self) {
        let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        state.pending.clear();
        state.actions.clear();
        state.last_ping.clear();
        state.last_authenticated.clear();
    }

    pub fn begin_outbound(
        self: &Arc<Self>,
        peer: OutboundPeer,
        stop: Arc<AtomicBool>,
    ) -> Result<(), String> {
        self.acquire_slot(Some(&peer.discovery_id))?;
        let core = Arc::clone(self);
        thread::spawn(move || {
            let discovery_id = peer.discovery_id.clone();
            let result = core.connect_and_pair(peer, stop);
            core.finish_session(result, Some(&discovery_id));
        });
        Ok(())
    }

    pub fn accept(self: &Arc<Self>, stream: TcpStream, stop: Arc<AtomicBool>) {
        if self.acquire_slot(None).is_err() {
            return;
        }
        let core = Arc::clone(self);
        thread::spawn(move || {
            let result = core.run_pair(stream, false, None, stop);
            core.finish_session(result, None);
        });
    }

    fn acquire_slot(&self, outbound_id: Option<&str>) -> Result<(), String> {
        let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        if state.active >= MAX_SESSIONS {
            return Err("同时进行的配对请求过多。".to_string());
        }
        if let Some(id) = outbound_id {
            if !state.outbound.insert(id.to_string()) {
                return Err("该设备已有配对请求。".to_string());
            }
        }
        state.active += 1;
        state.error = None;
        Ok(())
    }

    fn finish_session(&self, result: Result<(), String>, outbound_id: Option<&str>) {
        let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        state.active = state.active.saturating_sub(1);
        if let Some(id) = outbound_id {
            state.outbound.remove(id);
        }
        if let Err(error) = result {
            if error != "已拒绝配对。" && error != "设备发现已关闭。" {
                state.error = Some(error);
            }
        }
    }

    fn connect_and_pair(
        self: &Arc<Self>,
        peer: OutboundPeer,
        stop: Arc<AtomicBool>,
    ) -> Result<(), String> {
        for address in peer.addresses {
            if stop.load(Ordering::Acquire) {
                return Err("设备发现已关闭。".to_string());
            }
            if let Ok(mut stream) = TcpStream::connect_timeout(&address, HANDSHAKE_TIMEOUT) {
                stream
                    .set_write_timeout(Some(HANDSHAKE_TIMEOUT))
                    .map_err(|error| error.to_string())?;
                stream
                    .write_all(b"QDP1")
                    .map_err(|_| "无法启动配对握手。".to_string())?;
                return self.run_pair(stream, true, Some(&peer.discovery_id), stop);
            }
        }
        Err("无法连接到附近设备。".to_string())
    }

    fn connect_and_manage(
        self: &Arc<Self>,
        peer: OutboundPeer,
        expected_key: &str,
        action: DeviceAction,
        stop: Arc<AtomicBool>,
    ) -> Result<(), String> {
        for address in peer.addresses {
            if stop.load(Ordering::Acquire) {
                return Err("设备发现已关闭。".to_string());
            }
            if let Ok(mut stream) = TcpStream::connect_timeout(&address, HANDSHAKE_TIMEOUT) {
                stream.set_write_timeout(Some(HANDSHAKE_TIMEOUT)).map_err(|e| e.to_string())?;
                stream.write_all(b"QDM1").map_err(|_| "无法启动设备操作。".to_string())?;
                return self.run_management(stream, true, Some((&peer.discovery_id, expected_key)), Some(action), stop);
            }
        }
        if action == DeviceAction::Disconnect {
            return self.revoke_offline(expected_key);
        }
        Err("无法连接到已配对设备。".to_string())
    }

    fn run_management(
        &self,
        mut stream: TcpStream,
        initiator: bool,
        expected: Option<(&str, &str)>,
        action: Option<DeviceAction>,
        stop: Arc<AtomicBool>,
    ) -> Result<(), String> {
        stream.set_read_timeout(Some(HANDSHAKE_TIMEOUT)).map_err(|e| e.to_string())?;
        stream.set_write_timeout(Some(HANDSHAKE_TIMEOUT)).map_err(|e| e.to_string())?;
        let (handshake, remote) = handshake(
            &mut stream, &self.identity.private, &self.discovery_id, &self.name,
            initiator, MANAGEMENT_PROLOGUE,
        )?;
        let key = hex(handshake.get_remote_static().ok_or("对端身份验证失败。")?);
        let record = {
            let state = self.state.lock().unwrap_or_else(|error| error.into_inner());
            state.records.get(&key).or_else(|| state.tombstones.get(&key))
                .cloned().ok_or("设备尚未配对。")?
        };
        if remote.discovery_id != record.discovery_id ||
            expected.is_some_and(|(id, expected_key)| id != remote.discovery_id || expected_key != key) {
            return Err("对端配对身份不匹配。".to_string());
        }
        {
            let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
            if state.records.contains_key(&key) {
                state.last_authenticated.insert(key.clone(), Instant::now());
            }
        }
        let mut transport = handshake.into_transport_mode().map_err(|_| "加密设备会话无法启动。".to_string())?;
        if initiator {
            let action = action.ok_or("设备操作无效。")?;
            let request = serde_json::to_vec(&ManagementRequest {
                version: 1, action: action.wire_name().to_string(), percent: None, charging: None,
                app_name: None, title: None, body: None,
            })
                .map_err(|e| e.to_string())?;
            write_encrypted(&mut stream, &mut transport, &request)?;
            let answer = read_encrypted_timeout(&mut stream, &mut transport,
                if matches!(action, DeviceAction::DisconnectNotice | DeviceAction::Ping) { HANDSHAKE_TIMEOUT } else { PAIR_TIMEOUT })?;
            if matches!(action, DeviceAction::DisconnectNotice | DeviceAction::Ping) {
                if answer != b"D" { return Err("设备在线验证失败。".to_string()); }
                if action == DeviceAction::DisconnectNotice { self.clear_tombstone(&key)?; }
                return Ok(());
            }
            if answer == b"R" { return Err("对方拒绝了设备操作。".to_string()); }
            if answer != b"A" { return Err("设备操作确认无效。".to_string()); }
            if stop.load(Ordering::Acquire) { return Err("设备发现已关闭。".to_string()); }
            write_encrypted(&mut stream, &mut transport, b"C")?;
            if read_encrypted_timeout(&mut stream, &mut transport, HANDSHAKE_TIMEOUT)? != b"D" {
                return Err("设备操作完成确认无效。".to_string());
            }
            self.apply_action(&key, action, &record.name)?;
        } else {
            let request = read_encrypted_timeout(&mut stream, &mut transport, HANDSHAKE_TIMEOUT)?;
            let request: ManagementRequest = serde_json::from_slice(&request)
                .map_err(|_| "设备操作消息无效。".to_string())?;
            if request.version != 1 { return Err("设备操作版本不兼容。".to_string()); }
            if request.action == "disconnectNotice" {
                self.apply_disconnect_notice(&key, &record.name)?;
                write_encrypted(&mut stream, &mut transport, b"D")?;
                return Ok(());
            }
            let already_revoked = !self.state.lock().unwrap_or_else(|e| e.into_inner())
                .records.contains_key(&key);
            if already_revoked && request.action == "disconnect" {
                // We already committed this user's approval, but the completion
                // frame may have been lost. Let the initiator finish idempotently.
                write_encrypted(&mut stream, &mut transport, b"A")?;
                if read_encrypted_timeout(&mut stream, &mut transport, HANDSHAKE_TIMEOUT)? != b"C" {
                    return Err("设备操作提交无效。".to_string());
                }
                write_encrypted(&mut stream, &mut transport, b"D")?;
                return Ok(());
            }
            if already_revoked { return Err("设备已断开。".to_string()); }
            if request.action == "ping" {
                write_encrypted(&mut stream, &mut transport, b"D")?;
                return Ok(());
            }
            if request.action == "battery" {
                let trusted = self.state.lock().unwrap_or_else(|e| e.into_inner())
                    .records.get(&key).is_some_and(|peer| peer.relationship == Relationship::Intimate);
                if !trusted {
                    return Err("非亲密设备不得发送电量。".to_string());
                }
                let percent = request.percent.filter(|value| *value <= 100)
                    .ok_or("电量数据无效。")?;
                let charging = request.charging.ok_or("充电状态无效。")?;
                let received_at_ms = SystemTime::now().duration_since(UNIX_EPOCH)
                    .map_err(|e| e.to_string())?.as_millis() as u64;
                self.state.lock().unwrap_or_else(|e| e.into_inner()).batteries.insert(
                    key.clone(), DeviceBattery { peer_id: key, percent, charging, received_at_ms }
                );
                write_encrypted(&mut stream, &mut transport, b"D")?;
                return Ok(());
            }
            if request.action == "notification" {
                let trusted = self.state.lock().unwrap_or_else(|e| e.into_inner())
                    .records.get(&key).is_some_and(can_accept_notification);
                if !trusted { return Err("非亲密安卓设备不得发送通知。".to_string()); }
                let app_name = bounded_notification_field(request.app_name, 80, false)?;
                let title = bounded_notification_field(request.title, 160, false)?;
                let body = bounded_notification_field(request.body, 700, true)?;
                if title.is_empty() && body.is_empty() {
                    return Err("通知内容为空。".to_string());
                }
                let mut state = self.state.lock().unwrap_or_else(|e| e.into_inner());
                if state.notification_queue.len() >= 16 { state.notification_queue.pop_front(); }
                state.notification_queue.push_back(ForwardedNotification {
                    id: random_id()?, device_name: record.name, app_name, title, body,
                });
                drop(state);
                write_encrypted(&mut stream, &mut transport, b"D")?;
                return Ok(());
            }
            let action = DeviceAction::from_wire(&request.action).ok_or("设备操作消息无效。")?;
            // A repeated upgrade/demotion is allowed: if the final completion
            // frame was lost, the user can explicitly approve the retry.
            let session_id = random_id()?;
            {
                let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
                if state.actions.values().any(|pending| pending.peer_id == key) {
                    return Err("该设备已有待处理操作。".to_string());
                }
                state.actions.insert(session_id.clone(), PendingDeviceAction {
                    session_id: session_id.clone(), peer_id: key.clone(), name: record.name.clone(),
                    action, local_approved: false, decision: None,
                });
            }
            let answer = self.await_action_decision(&session_id, &stop);
            self.state.lock().unwrap_or_else(|e| e.into_inner()).actions.remove(&session_id);
            match answer? {
                false => { write_encrypted(&mut stream, &mut transport, b"R")?; return Ok(()); }
                true => write_encrypted(&mut stream, &mut transport, b"A")?,
            }
            if read_encrypted_timeout(&mut stream, &mut transport, HANDSHAKE_TIMEOUT)? != b"C" {
                return Err("设备操作提交无效。".to_string());
            }
            self.apply_action(&key, action, &record.name)?;
            write_encrypted(&mut stream, &mut transport, b"D")?;
        }
        Ok(())
    }

    fn await_action_decision(&self, session_id: &str, stop: &AtomicBool) -> Result<bool, String> {
        let started = Instant::now();
        while started.elapsed() < PAIR_TIMEOUT && !stop.load(Ordering::Acquire) {
            if let Some(decision) = self.state.lock().unwrap_or_else(|e| e.into_inner())
                .actions.get(session_id).and_then(|action| action.decision) {
                return Ok(decision);
            }
            thread::sleep(Duration::from_millis(70));
        }
        Err("设备操作确认已超时。".to_string())
    }

    fn apply_action(&self, peer_id: &str, action: DeviceAction, name: &str) -> Result<(), String> {
        if action == DeviceAction::Disconnect {
            // Keep a durable revocation on both ends until the other device
            // acknowledges it. This also repairs a lost completion frame.
            return self.revoke_offline(peer_id);
        }
        let mut state = self.state.lock().unwrap_or_else(|e| e.into_inner());
        let previous = state.records.get(peer_id).cloned().ok_or("设备已断开。")?;
        match action {
            DeviceAction::Upgrade => {
                state.records.get_mut(peer_id).unwrap().relationship = Relationship::Intimate;
            }
            DeviceAction::Demote => {
                state.records.get_mut(peer_id).unwrap().relationship = Relationship::Connected;
            }
            DeviceAction::Disconnect | DeviceAction::DisconnectNotice | DeviceAction::Ping => {
                return Err("设备操作无效。".to_string());
            }
        }
        if let Err(error) = self.persist(&state.records) {
            state.records.insert(peer_id.to_owned(), previous);
            return Err(error);
        }
        state.notices.push(DeviceNotice { id: random_id()?, peer_name: name.to_owned(), action });
        if state.notices.len() > 16 { state.notices.remove(0); }
        Ok(())
    }

    fn clear_tombstone(&self, peer_id: &str) -> Result<(), String> {
        let mut state = self.state.lock().unwrap_or_else(|e| e.into_inner());
        // A previous paired-file write may have failed after the revocation
        // was safely persisted. Do not clear that safety record first.
        self.persist(&state.records)?;
        let Some(removed) = state.tombstones.remove(peer_id) else { return Ok(()) };
        if let Err(error) = self.persist_tombstones(&state.tombstones) {
            state.tombstones.insert(peer_id.to_owned(), removed);
            return Err(error);
        }
        Ok(())
    }

    fn apply_disconnect_notice(&self, peer_id: &str, name: &str) -> Result<(), String> {
        let mut state = self.state.lock().unwrap_or_else(|e| e.into_inner());
        let old_record = state.records.remove(peer_id);
        let old_tombstone = state.tombstones.remove(peer_id);
        if let Err(error) = self.persist(&state.records) {
            if let Some(record) = old_record { state.records.insert(peer_id.to_owned(), record); }
            if let Some(record) = old_tombstone { state.tombstones.insert(peer_id.to_owned(), record); }
            return Err(error);
        }
        if old_tombstone.is_some() {
            if let Err(error) = self.persist_tombstones(&state.tombstones) {
                // The paired file has already been durably cleared. Keep the
                // old revocation so a restart still fails closed.
                if let Some(record) = old_tombstone { state.tombstones.insert(peer_id.to_owned(), record); }
                return Err(error);
            }
        }
        state.batteries.remove(peer_id);
        state.last_authenticated.remove(peer_id);
        state.last_ping.remove(peer_id);
        state.notices.push(DeviceNotice { id: random_id()?, peer_name: name.to_owned(), action: DeviceAction::Disconnect });
        if state.notices.len() > 16 { state.notices.remove(0); }
        Ok(())
    }

    fn run_pair(
        self: &Arc<Self>,
        mut stream: TcpStream,
        initiator: bool,
        expected_id: Option<&str>,
        stop: Arc<AtomicBool>,
    ) -> Result<(), String> {
        stream
            .set_read_timeout(Some(HANDSHAKE_TIMEOUT))
            .map_err(|error| error.to_string())?;
        stream
            .set_write_timeout(Some(HANDSHAKE_TIMEOUT))
            .map_err(|error| error.to_string())?;
        let (handshake, remote) = handshake(
            &mut stream,
            &self.identity.private,
            &self.discovery_id,
            &self.name,
            initiator,
            PROLOGUE,
        )?;
        if remote.discovery_id == self.discovery_id {
            return Err("不能与本机设备配对。".to_string());
        }
        if expected_id.is_some_and(|id| id != remote.discovery_id) {
            return Err("设备发现标识与配对握手不一致。".to_string());
        }
        let remote_key = handshake
            .get_remote_static()
            .ok_or("握手缺少对端设备密钥。")?;
        if remote_key.len() != 32 {
            return Err("对端设备密钥无效。".to_string());
        }
        let key = hex(remote_key);
        let code = verification_code(handshake.get_handshake_hash());
        let session_id = random_id()?;
        let pending = PendingPair {
            session_id: session_id.clone(),
            discovery_id: remote.discovery_id.clone(),
            name: remote.name.clone(),
            platform: remote.platform.clone(),
            code,
            incoming: !initiator,
            local_approved: false,
            decision: None,
        };
        {
            let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
            if state.records.contains_key(&key) {
                return Err("设备已配对。".to_string());
            }
            if state
                .pending
                .values()
                .any(|existing| existing.discovery_id == remote.discovery_id)
            {
                return Err("该设备已有配对请求。".to_string());
            }
            if state
                .records
                .values()
                .any(|record| record.discovery_id == remote.discovery_id && record.id != key)
            {
                return Err("设备密钥已变化；请先移除旧配对后再验证。".to_string());
            }
            state.pending.insert(session_id.clone(), pending);
        }
        let result = self.await_both_confirmations(&mut stream, handshake, &session_id, stop);
        self.state
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .pending
            .remove(&session_id);
        result?;
        self.save_new_pair(PairedDevice {
            id: key,
            discovery_id: remote.discovery_id,
            name: remote.name,
            platform: remote.platform,
            relationship: Relationship::Connected,
        })
    }

    fn await_both_confirmations(
        &self,
        stream: &mut TcpStream,
        handshake: HandshakeState,
        session_id: &str,
        stop: Arc<AtomicBool>,
    ) -> Result<(), String> {
        let mut transport = handshake
            .into_transport_mode()
            .map_err(|_| "加密会话无法启动。".to_string())?;
        stream
            .set_read_timeout(Some(Duration::from_millis(150)))
            .map_err(|error| error.to_string())?;
        let start = Instant::now();
        let mut local_sent = false;
        let mut remote_approved = false;
        while start.elapsed() < PAIR_TIMEOUT && !stop.load(Ordering::Acquire) {
            let decision = self
                .state
                .lock()
                .unwrap_or_else(|error| error.into_inner())
                .pending
                .get(session_id)
                .and_then(|pending| pending.decision);
            if let Some(approve) = decision {
                if !local_sent {
                    write_encrypted(stream, &mut transport, if approve { b"A" } else { b"R" })?;
                    local_sent = true;
                    if !approve {
                        return Err("已拒绝配对。".to_string());
                    }
                }
            }
            if !remote_approved {
                let mut ready = [0u8; 1];
                match stream.peek(&mut ready) {
                    Ok(0) => return Err("对端已断开配对。".to_string()),
                    Ok(_) => match read_encrypted(stream, &mut transport)?.as_slice() {
                        b"A" => remote_approved = true,
                        b"R" => return Err("对端拒绝了配对。".to_string()),
                        _ => return Err("收到无效的配对确认。".to_string()),
                    },
                    Err(error)
                        if matches!(
                            error.kind(),
                            std::io::ErrorKind::TimedOut | std::io::ErrorKind::WouldBlock
                        ) => {}
                    Err(_) => return Err("配对连接已断开。".to_string()),
                }
            }
            if local_sent && remote_approved {
                return Ok(());
            }
            thread::sleep(Duration::from_millis(70));
        }
        if stop.load(Ordering::Acquire) {
            Err("设备发现已关闭。".to_string())
        } else {
            Err("配对确认已超时。".to_string())
        }
    }

    fn save_new_pair(&self, record: PairedDevice) -> Result<(), String> {
        let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        if state.records.len() >= MAX_RECORDS {
            return Err("已配对设备数量已达上限。".to_string());
        }
        if state.records.contains_key(&record.id) {
            return Err("设备已配对。".to_string());
        }
        if state.records.values().any(|existing| {
            existing.discovery_id == record.discovery_id && existing.id != record.id
        }) {
            return Err("设备密钥已变化；请先移除旧配对后再验证。".to_string());
        }
        let id = record.id.clone();
        state.records.insert(id.clone(), record);
        if let Err(error) = self.persist(&state.records) {
            state.records.remove(&id);
            return Err(error);
        }
        state.last_authenticated.insert(id, Instant::now());
        Ok(())
    }

    fn persist(&self, records: &BTreeMap<String, PairedDevice>) -> Result<(), String> {
        self.persist_file(self.records_file.as_deref(), records)
    }

    fn persist_tombstones(&self, records: &BTreeMap<String, PairedDevice>) -> Result<(), String> {
        self.persist_file(self.tombstones_file.as_deref(), records)
    }

    fn persist_file(&self, file: Option<&Path>, records: &BTreeMap<String, PairedDevice>) -> Result<(), String> {
        let Some(file) = file else { return Ok(()); };
        let value = PairRecords {
            version: 1,
            peers: records.values().cloned().collect(),
        };
        let data = serde_json::to_vec_pretty(&value).map_err(|error| error.to_string())?;
        let temporary = file.with_extension("json.pending");
        let mut handle =
            fs::File::create(&temporary).map_err(|error| format!("无法保存配对记录：{error}"))?;
        handle
            .write_all(&data)
            .and_then(|_| handle.sync_all())
            .map_err(|error| format!("无法保存配对记录：{error}"))?;
        drop(handle);
        // The original remains intact if replacement fails. Corrupt data
        // always fails closed on the next startup.
        fs::rename(&temporary, file).map_err(|error| format!("无法完成配对记录保存：{error}"))
    }
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct PairHello {
    discovery_id: String,
    name: String,
    platform: String,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ManagementRequest {
    version: u8,
    action: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    percent: Option<u8>,
    #[serde(skip_serializing_if = "Option::is_none")]
    charging: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    app_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    title: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    body: Option<String>,
}

fn bounded_notification_field(value: Option<String>, max_chars: usize, multiline: bool) -> Result<String, String> {
    let value = value.ok_or("通知字段缺失。")?;
    if value.chars().count() > max_chars || value.chars().any(|c|
        c.is_control() && !(multiline && (c == '\n' || c == '\t'))
    ) { return Err("通知字段无效。".to_string()); }
    Ok(value.trim().to_string())
}

fn can_accept_notification(peer: &PairedDevice) -> bool {
    peer.relationship == Relationship::Intimate && peer.platform == "android"
}

impl DeviceAction {
    fn wire_name(self) -> &'static str {
        match self {
            Self::Upgrade => "upgrade",
            Self::Disconnect => "disconnect",
            Self::Demote => "demote",
            Self::DisconnectNotice => "disconnectNotice",
            Self::Ping => "ping",
        }
    }

    fn from_wire(value: &str) -> Option<Self> {
        match value {
            "upgrade" => Some(Self::Upgrade),
            "disconnect" => Some(Self::Disconnect),
            "demote" => Some(Self::Demote),
            _ => None,
        }
    }
}

fn handshake(
    stream: &mut TcpStream,
    private: &[u8],
    discovery_id: &str,
    name: &str,
    initiator: bool,
    prologue: &[u8],
) -> Result<(HandshakeState, PairHello), String> {
    let parameters = NOISE_PATTERN
        .parse()
        .map_err(|_| "配对协议不可用。".to_string())?;
    let builder = Builder::new(parameters)
        .local_private_key(private)
        .prologue(prologue);
    let mut state = if initiator {
        builder.build_initiator()
    } else {
        builder.build_responder()
    }
    .map_err(|_| "无法初始化设备配对。".to_string())?;
    let local = serde_json::to_vec(&PairHello {
        discovery_id: discovery_id.to_owned(),
        name: safe_name(name),
        platform: "windows".to_string(),
    })
    .map_err(|error| error.to_string())?;
    let mut output = [0u8; MAX_FRAME];
    let mut payload = [0u8; MAX_FRAME];
    let remote = if initiator {
        let count = state
            .write_message(&[], &mut output)
            .map_err(|_| "配对握手失败。".to_string())?;
        write_frame(stream, &output[..count])?;
        let message = read_frame(stream)?;
        let count = state
            .read_message(&message, &mut payload)
            .map_err(|_| "无法验证对端配对握手。".to_string())?;
        let hello = parse_hello(&payload[..count])?;
        let count = state
            .write_message(&local, &mut output)
            .map_err(|_| "配对握手失败。".to_string())?;
        write_frame(stream, &output[..count])?;
        hello
    } else {
        let message = read_frame(stream)?;
        state
            .read_message(&message, &mut payload)
            .map_err(|_| "无法验证对端配对握手。".to_string())?;
        let count = state
            .write_message(&local, &mut output)
            .map_err(|_| "配对握手失败。".to_string())?;
        write_frame(stream, &output[..count])?;
        let message = read_frame(stream)?;
        let count = state
            .read_message(&message, &mut payload)
            .map_err(|_| "无法验证对端配对握手。".to_string())?;
        parse_hello(&payload[..count])?
    };
    if !state.is_handshake_finished() {
        return Err("配对握手未完成。".to_string());
    }
    Ok((state, remote))
}

fn parse_hello(bytes: &[u8]) -> Result<PairHello, String> {
    let mut hello: PairHello =
        serde_json::from_slice(bytes).map_err(|_| "设备配对信息无效。".to_string())?;
    if !valid_id(&hello.discovery_id)
        || hello.name.trim().is_empty()
        || hello.name.chars().count() > 64
        || (hello.platform != "windows" && hello.platform != "android")
    {
        return Err("设备配对信息无效。".to_string());
    }
    hello.name = safe_name(&hello.name);
    Ok(hello)
}

fn read_frame(stream: &mut TcpStream) -> Result<Vec<u8>, String> {
    let mut header = [0u8; 2];
    stream
        .read_exact(&mut header)
        .map_err(|_| "配对连接已断开。".to_string())?;
    let length = u16::from_be_bytes(header) as usize;
    if length == 0 || length > MAX_FRAME {
        return Err("配对消息长度无效。".to_string());
    }
    let mut message = vec![0u8; length];
    stream
        .read_exact(&mut message)
        .map_err(|_| "配对连接已断开。".to_string())?;
    Ok(message)
}

fn write_frame(stream: &mut TcpStream, message: &[u8]) -> Result<(), String> {
    if message.is_empty() || message.len() > MAX_FRAME {
        return Err("配对消息长度无效。".to_string());
    }
    stream
        .write_all(&(message.len() as u16).to_be_bytes())
        .and_then(|_| stream.write_all(message))
        .map_err(|_| "无法发送配对消息。".to_string())
}

fn write_encrypted(
    stream: &mut TcpStream,
    transport: &mut TransportState,
    message: &[u8],
) -> Result<(), String> {
    let mut output = [0u8; MAX_FRAME];
    let count = transport
        .write_message(message, &mut output)
        .map_err(|_| "加密配对确认失败。".to_string())?;
    write_frame(stream, &output[..count])
}

fn read_encrypted(
    stream: &mut TcpStream,
    transport: &mut TransportState,
) -> Result<Vec<u8>, String> {
    read_encrypted_timeout(stream, transport, HANDSHAKE_TIMEOUT)
}

fn read_encrypted_timeout(
    stream: &mut TcpStream,
    transport: &mut TransportState,
    timeout: Duration,
) -> Result<Vec<u8>, String> {
    stream
        .set_read_timeout(Some(timeout))
        .map_err(|error| error.to_string())?;
    let message = read_frame(stream)?;
    stream
        .set_read_timeout(Some(Duration::from_millis(150)))
        .map_err(|error| error.to_string())?;
    let mut output = [0u8; MAX_FRAME];
    let count = transport
        .read_message(&message, &mut output)
        .map_err(|_| "无法验证对端配对确认。".to_string())?;
    Ok(output[..count].to_vec())
}

fn verification_code(hash: &[u8]) -> String {
    let mut hasher = Sha256::new();
    hasher.update(b"QingToolbox device pairing code v1");
    hasher.update(hash);
    let digest = hasher.finalize();
    let value = u64::from_be_bytes(digest[..8].try_into().unwrap()) % 100_000_000;
    format!("{value:08}")
}

fn random_id() -> Result<String, String> {
    let mut bytes = [0u8; 16];
    getrandom::fill(&mut bytes).map_err(|_| "无法生成配对会话标识。".to_string())?;
    Ok(hex(&bytes))
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

fn valid_id(value: &str) -> bool {
    value.len() == 32 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn safe_name(value: &str) -> String {
    let value = value
        .trim()
        .chars()
        .filter(|character| !character.is_control())
        .take(64)
        .collect::<String>();
    if value.is_empty() {
        "QingToolbox".to_string()
    } else {
        value
    }
}

fn read_records(file: &Path) -> Result<BTreeMap<String, PairedDevice>, String> {
    let data = match fs::read(file) {
        Ok(data) => data,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(BTreeMap::new()),
        Err(error) => return Err(format!("无法读取配对记录：{error}")),
    };
    if data.len() > 128 * 1024 {
        return Err("配对记录超出安全限制。".to_string());
    }
    let saved: PairRecords =
        serde_json::from_slice(&data).map_err(|_| "配对记录无效；已停止加载。".to_string())?;
    if saved.version != 1 || saved.peers.len() > MAX_RECORDS {
        return Err("配对记录版本或数量无效。".to_string());
    }
    let mut records = BTreeMap::new();
    let mut discovery_ids = BTreeSet::new();
    for peer in saved.peers {
        if peer.id.len() != 64
            || !peer.id.bytes().all(|byte| byte.is_ascii_hexdigit())
            || !valid_id(&peer.discovery_id)
            || peer.id != peer.id.to_ascii_lowercase()
            || peer.discovery_id != peer.discovery_id.to_ascii_lowercase()
            || peer.name.trim().is_empty()
            || peer.name.chars().count() > 64
            || peer.name.chars().any(|character| character.is_control())
            || (peer.platform != "windows" && peer.platform != "android")
            || !discovery_ids.insert(peer.discovery_id.clone())
            || records.insert(peer.id.clone(), peer).is_some()
        {
            return Err("配对记录包含无效设备。".to_string());
        }
    }
    Ok(records)
}

fn generate_identity() -> Result<LocalIdentity, String> {
    let parameters = NOISE_PATTERN
        .parse()
        .map_err(|_| "配对协议不可用。".to_string())?;
    let keypair = Builder::new(parameters)
        .generate_keypair()
        .map_err(|_| "无法生成设备配对密钥。".to_string())?;
    if keypair.private.len() != 32 || keypair.public.len() != 32 {
        return Err("设备配对密钥长度无效。".to_string());
    }
    Ok(LocalIdentity {
        private: keypair.private,
    })
}

fn load_or_create_identity(directory: &Path) -> Result<LocalIdentity, String> {
    let file = directory.join("pairing-key.dpapi");
    match fs::read(&file) {
        Ok(data) => return decode_identity(&data),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(error) => return Err(format!("无法读取设备配对密钥：{error}")),
    }
    let identity = generate_identity()?;
    let mut plain = Vec::with_capacity(32);
    plain.extend_from_slice(&identity.private);
    let protected = protect_secret(&plain)?;
    plain.fill(0);
    match fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&file)
    {
        Ok(mut handle) => {
            handle
                .write_all(&protected)
                .map_err(|error| format!("无法保存设备配对密钥：{error}"))?;
            handle
                .sync_all()
                .map_err(|error| format!("无法保存设备配对密钥：{error}"))?;
            Ok(identity)
        }
        Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => {
            let data = fs::read(&file).map_err(|error| format!("无法读取设备配对密钥：{error}"))?;
            decode_identity(&data)
        }
        Err(error) => Err(format!("无法保存设备配对密钥：{error}")),
    }
}

fn decode_identity(data: &[u8]) -> Result<LocalIdentity, String> {
    if data.len() > 4096 {
        return Err("设备配对密钥文件无效。".to_string());
    }
    let mut plain = unprotect_secret(data)?;
    if plain.len() != 32 {
        plain.fill(0);
        return Err("设备配对密钥文件无效。".to_string());
    }
    let private = plain.to_vec();
    plain.fill(0);
    Ok(LocalIdentity { private })
}

#[cfg(windows)]
fn protect_secret(data: &[u8]) -> Result<Vec<u8>, String> {
    use windows_sys::Win32::{
        Foundation::LocalFree,
        Security::Cryptography::{CryptProtectData, CRYPTPROTECT_UI_FORBIDDEN, CRYPT_INTEGER_BLOB},
    };
    let input = CRYPT_INTEGER_BLOB {
        cbData: data.len() as u32,
        pbData: data.as_ptr() as *mut u8,
    };
    let mut output = CRYPT_INTEGER_BLOB::default();
    let success = unsafe {
        CryptProtectData(
            &input,
            std::ptr::null(),
            std::ptr::null(),
            std::ptr::null(),
            std::ptr::null(),
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output,
        )
    };
    if success == 0 {
        return Err(format!(
            "Windows 无法保护设备密钥：{}",
            std::io::Error::last_os_error()
        ));
    }
    let protected =
        unsafe { std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec() };
    unsafe {
        LocalFree(output.pbData.cast());
    }
    Ok(protected)
}

#[cfg(windows)]
fn unprotect_secret(data: &[u8]) -> Result<Vec<u8>, String> {
    use windows_sys::Win32::{
        Foundation::LocalFree,
        Security::Cryptography::{
            CryptUnprotectData, CRYPTPROTECT_UI_FORBIDDEN, CRYPT_INTEGER_BLOB,
        },
    };
    let input = CRYPT_INTEGER_BLOB {
        cbData: data.len() as u32,
        pbData: data.as_ptr() as *mut u8,
    };
    let mut output = CRYPT_INTEGER_BLOB::default();
    let success = unsafe {
        CryptUnprotectData(
            &input,
            std::ptr::null_mut(),
            std::ptr::null(),
            std::ptr::null(),
            std::ptr::null(),
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output,
        )
    };
    if success == 0 {
        return Err("无法解锁设备配对密钥；请检查当前 Windows 用户身份。".to_string());
    }
    let plain =
        unsafe { std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec() };
    unsafe {
        LocalFree(output.pbData.cast());
    }
    Ok(plain)
}

#[cfg(not(windows))]
fn protect_secret(_data: &[u8]) -> Result<Vec<u8>, String> {
    Err("此平台尚未接入系统密钥存储。".to_string())
}

#[cfg(not(windows))]
fn unprotect_secret(_data: &[u8]) -> Result<Vec<u8>, String> {
    Err("此平台尚未接入系统密钥存储。".to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::TcpListener;

    #[test]
    fn forwarded_notifications_require_an_intimate_android_identity() {
        let peer = PairedDevice {
            id: "a".repeat(64), discovery_id: "b".repeat(32), name: "Phone".to_string(),
            platform: "android".to_string(), relationship: Relationship::Connected,
        };
        assert!(!can_accept_notification(&peer));
        assert!(!can_accept_notification(&PairedDevice {
            platform: "windows".to_string(), relationship: Relationship::Intimate, ..peer.clone()
        }));
        assert!(can_accept_notification(&PairedDevice {
            relationship: Relationship::Intimate, ..peer
        }));
        assert!(bounded_notification_field(Some("a".repeat(701)), 700, true).is_err());
        assert!(bounded_notification_field(Some("hello\u{0000}".to_string()), 160, false).is_err());
    }

    #[test]
    fn android_notification_wire_fields_are_read_as_camel_case() {
        let request: ManagementRequest = serde_json::from_str(
            r#"{"version":1,"action":"notification","appName":"Messages","title":"Hello","body":"World"}"#,
        ).unwrap();
        assert_eq!(request.app_name.as_deref(), Some("Messages"));
        assert_eq!(request.title.as_deref(), Some("Hello"));
        assert_eq!(request.body.as_deref(), Some("World"));
    }

    fn wait_until(mut predicate: impl FnMut() -> bool) {
        let deadline = Instant::now() + Duration::from_secs(8);
        while Instant::now() < deadline {
            if predicate() {
                return;
            }
            thread::sleep(Duration::from_millis(25));
        }
        panic!("pairing did not reach the expected state");
    }

    #[test]
    fn two_local_instances_require_both_users_then_default_to_connected() {
        let first = PairingCore::ephemeral("0123456789abcdef0123456789abcdef", "First PC");
        let second = PairingCore::ephemeral("fedcba9876543210fedcba9876543210", "Second PC");
        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let address = listener.local_addr().unwrap();
        let stopped = Arc::new(AtomicBool::new(false));
        let responder = Arc::clone(&second);
        let responder_stop = Arc::clone(&stopped);
        let receiver = thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            let mut marker = [0u8; 4];
            stream.read_exact(&mut marker).unwrap();
            assert_eq!(&marker, b"QDP1");
            responder.accept(stream, responder_stop);
        });
        first
            .begin_outbound(
                OutboundPeer {
                    discovery_id: "fedcba9876543210fedcba9876543210".to_string(),
                    addresses: vec![address],
                },
                Arc::clone(&stopped),
            )
            .unwrap();
        receiver.join().unwrap();
        wait_until(|| first.snapshot().pending.len() == 1 && second.snapshot().pending.len() == 1);
        let a = first.snapshot().pending[0].clone();
        let b = second.snapshot().pending[0].clone();
        assert_eq!(a.code, b.code);
        assert!(a.code.len() == 8 && a.code.bytes().all(|byte| byte.is_ascii_digit()));
        first.decide(&a.session_id, true).unwrap();
        thread::sleep(Duration::from_millis(300));
        assert!(first.snapshot().paired.is_empty());
        assert!(second.snapshot().paired.is_empty());
        second.decide(&b.session_id, true).unwrap();
        wait_until(|| first.snapshot().paired.len() == 1 && second.snapshot().paired.len() == 1);
        assert_eq!(
            first.snapshot().paired[0].relationship,
            Relationship::Connected
        );
        assert_eq!(
            second.snapshot().paired[0].relationship,
            Relationship::Connected
        );
        let peer_id = first.snapshot().paired[0].id.clone();
        let action_listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let action_address = action_listener.local_addr().unwrap();
        let action_responder = Arc::clone(&second);
        let action_stop = Arc::clone(&stopped);
        let action_receiver = thread::spawn(move || {
            let (mut stream, _) = action_listener.accept().unwrap();
            let mut marker = [0u8; 4];
            stream.read_exact(&mut marker).unwrap();
            assert_eq!(&marker, b"QDM1");
            action_responder.accept_action(stream, action_stop);
        });
        first.request_action(&peer_id, OutboundPeer {
            discovery_id: second.discovery_id.clone(), addresses: vec![action_address],
        }, DeviceAction::Upgrade, Arc::clone(&stopped)).unwrap();
        action_receiver.join().unwrap();
        wait_until(|| second.snapshot().actions.len() == 1);
        assert_eq!(first.snapshot().paired[0].relationship, Relationship::Connected);
        assert_eq!(second.snapshot().paired[0].relationship, Relationship::Connected);
        let action_id = second.snapshot().actions[0].session_id.clone();
        second.decide_action(&action_id, true).unwrap();
        wait_until(|| first.snapshot().paired[0].relationship == Relationship::Intimate &&
            second.snapshot().paired[0].relationship == Relationship::Intimate);

        first.state.lock().unwrap().last_authenticated.clear();
        second.state.lock().unwrap().last_authenticated.clear();
        assert!(first.snapshot().online.is_empty());
        let ping_listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let ping_address = ping_listener.local_addr().unwrap();
        let ping_responder = Arc::clone(&second);
        let ping_stop = Arc::clone(&stopped);
        let ping_receiver = thread::spawn(move || {
            let (mut stream, _) = ping_listener.accept().unwrap();
            let mut marker = [0u8; 4];
            stream.read_exact(&mut marker).unwrap();
            assert_eq!(&marker, b"QDM1");
            ping_responder.accept_action(stream, ping_stop);
        });
        first.probe_online(OutboundPeer {
            discovery_id: second.discovery_id.clone(), addresses: vec![ping_address],
        }, Arc::clone(&stopped));
        ping_receiver.join().unwrap();
        wait_until(|| first.snapshot().online.len() == 1 && second.snapshot().online.len() == 1);

        let disconnect_listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let disconnect_address = disconnect_listener.local_addr().unwrap();
        let disconnect_responder = Arc::clone(&second);
        let disconnect_stop = Arc::clone(&stopped);
        let disconnect_receiver = thread::spawn(move || {
            let (mut stream, _) = disconnect_listener.accept().unwrap();
            let mut marker = [0u8; 4];
            stream.read_exact(&mut marker).unwrap();
            assert_eq!(&marker, b"QDM1");
            disconnect_responder.accept_action(stream, disconnect_stop);
        });
        first.request_action(&peer_id, OutboundPeer {
            discovery_id: second.discovery_id.clone(), addresses: vec![disconnect_address],
        }, DeviceAction::Disconnect, Arc::clone(&stopped)).unwrap();
        disconnect_receiver.join().unwrap();
        wait_until(|| second.snapshot().actions.len() == 1);
        assert_eq!(first.snapshot().paired.len(), 1);
        assert_eq!(second.snapshot().paired.len(), 1);
        let disconnect_id = second.snapshot().actions[0].session_id.clone();
        second.decide_action(&disconnect_id, true).unwrap();
        wait_until(|| first.snapshot().paired.is_empty() && second.snapshot().paired.is_empty());
        assert_eq!(first.snapshot().revocations.len(), 1);
        assert_eq!(second.snapshot().revocations.len(), 1);

        let sync_listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let sync_address = sync_listener.local_addr().unwrap();
        let sync_responder = Arc::clone(&second);
        let sync_stop = Arc::clone(&stopped);
        let sync_receiver = thread::spawn(move || {
            let (mut stream, _) = sync_listener.accept().unwrap();
            let mut marker = [0u8; 4];
            stream.read_exact(&mut marker).unwrap();
            assert_eq!(&marker, b"QDM1");
            sync_responder.accept_action(stream, sync_stop);
        });
        first.retry_tombstone(OutboundPeer {
            discovery_id: second.discovery_id.clone(), addresses: vec![sync_address],
        }, Arc::clone(&stopped));
        sync_receiver.join().unwrap();
        wait_until(|| first.snapshot().revocations.is_empty() && second.snapshot().revocations.is_empty());
        stopped.store(true, Ordering::Release);
    }

    #[test]
    fn manual_relationships_require_existing_pair() {
        let core = PairingCore::ephemeral("0123456789abcdef0123456789abcdef", "One");
        assert!(core
            .set_relationship("unknown", Relationship::Intimate)
            .is_err());
        let peer = PairedDevice {
            id: "a".repeat(64),
            discovery_id: "f".repeat(32),
            name: "Two".to_string(),
            platform: "windows".to_string(),
            relationship: Relationship::Connected,
        };
        core.save_new_pair(peer.clone()).unwrap();
        let mut replaced_key = peer.clone();
        replaced_key.id = "b".repeat(64);
        assert!(core.save_new_pair(replaced_key).is_err());
        assert_eq!(
            core.snapshot().paired[0].relationship,
            Relationship::Connected
        );
        core.set_relationship(&peer.id, Relationship::Intimate)
            .unwrap();
        assert_eq!(
            core.snapshot().paired[0].relationship,
            Relationship::Intimate
        );
        core.set_relationship(&peer.id, Relationship::Connected)
            .unwrap();
        core.revoke(&peer.id).unwrap();
        assert!(core.snapshot().paired.is_empty());
    }

    #[test]
    fn authenticated_presence_expires_and_discovery_stop_clears_it() {
        let core = PairingCore::ephemeral("0123456789abcdef0123456789abcdef", "One");
        let peer = PairedDevice {
            id: "a".repeat(64), discovery_id: "f".repeat(32),
            name: "Two".to_string(), platform: "android".to_string(),
            relationship: Relationship::Connected,
        };
        core.save_new_pair(peer.clone()).unwrap();
        assert_eq!(core.snapshot().online, vec![peer.id.clone()]);
        core.state.lock().unwrap().last_authenticated.insert(
            peer.id.clone(), Instant::now() - PRESENCE_TTL,
        );
        assert!(core.snapshot().online.is_empty());
        core.state.lock().unwrap().last_authenticated.insert(peer.id, Instant::now());
        core.cancel_pending();
        assert!(core.snapshot().online.is_empty());
    }

    #[test]
    fn protected_key_round_trip_or_platform_unavailable() {
        let secret = [37u8; 64];
        #[cfg(windows)]
        {
            let protected = protect_secret(&secret).unwrap();
            assert_ne!(protected, secret);
            assert_eq!(unprotect_secret(&protected).unwrap(), secret);
        }
        #[cfg(not(windows))]
        assert!(protect_secret(&secret).is_err());
    }

    #[cfg(windows)]
    #[test]
    fn pairing_key_and_manual_relation_survive_a_new_process_state() {
        let profile = std::env::temp_dir().join(format!(
            "qingtoolbox-device-pair-test-{}",
            random_id().unwrap()
        ));
        assert!(profile.starts_with(std::env::temp_dir()));
        let id = "0123456789abcdef0123456789abcdef";
        let first = PairingCore::new(&profile, id, "First PC").unwrap();
        let peer = PairedDevice {
            id: "a".repeat(64),
            discovery_id: "f".repeat(32),
            name: "Second PC".to_string(),
            platform: "windows".to_string(),
            relationship: Relationship::Connected,
        };
        first.save_new_pair(peer.clone()).unwrap();
        first
            .set_relationship(&peer.id, Relationship::Intimate)
            .unwrap();
        let original_private = first.identity.private.clone();
        drop(first);
        let restored = PairingCore::new(&profile, id, "First PC").unwrap();
        assert_eq!(restored.identity.private, original_private);
        assert_eq!(
            restored.snapshot().paired[0].relationship,
            Relationship::Intimate
        );
        drop(restored);
        fs::remove_dir_all(&profile).unwrap();
    }

    #[cfg(windows)]
    #[test]
    fn offline_disconnect_persists_a_revocation_until_authenticated_sync() {
        let profile = std::env::temp_dir().join(format!(
            "qingtoolbox-device-revoke-test-{}", random_id().unwrap()
        ));
        let id = "0123456789abcdef0123456789abcdef";
        let first = PairingCore::new(&profile, id, "First PC").unwrap();
        let peer = PairedDevice {
            id: "a".repeat(64), discovery_id: "f".repeat(32),
            name: "Second PC".to_string(), platform: "windows".to_string(),
            relationship: Relationship::Connected,
        };
        first.save_new_pair(peer.clone()).unwrap();
        first.revoke_offline(&peer.id).unwrap();
        assert!(first.snapshot().paired.is_empty());
        assert_eq!(first.snapshot().revocations.len(), 1);
        drop(first);
        let restored = PairingCore::new(&profile, id, "First PC").unwrap();
        assert!(restored.snapshot().paired.is_empty());
        assert_eq!(restored.snapshot().revocations[0].id, peer.id);
        restored.clear_tombstone(&peer.id).unwrap();
        drop(restored);
        let synchronized = PairingCore::new(&profile, id, "First PC").unwrap();
        assert!(synchronized.snapshot().revocations.is_empty());
        drop(synchronized);
        fs::remove_dir_all(&profile).unwrap();
    }

    #[cfg(windows)]
    #[test]
    fn old_pairings_are_not_inherited_after_local_key_loss() {
        let profile = std::env::temp_dir().join(format!(
            "qingtoolbox-device-pair-test-{}",
            random_id().unwrap()
        ));
        assert!(profile.starts_with(std::env::temp_dir()));
        let directory = profile.join("Devices");
        fs::create_dir_all(&directory).unwrap();
        fs::write(
            directory.join("paired.json"),
            b"{\"version\":1,\"peers\":[]}",
        )
        .unwrap();
        assert!(
            PairingCore::new(&profile, "0123456789abcdef0123456789abcdef", "First PC").is_err()
        );
        assert!(!directory.join("pairing-key.dpapi").exists());
        fs::remove_dir_all(&profile).unwrap();
    }
}
