//! Host-owned, untrusted LAN discovery. A probe proves only that the advertised
//! endpoint is live; it is never a pairing or authorization decision.
use std::{
    collections::BTreeMap,
    fs,
    io::{Read, Write},
    net::{IpAddr, SocketAddr, SocketAddrV6, TcpListener, TcpStream},
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Mutex,
    },
    thread,
    time::{Duration, Instant},
};

#[cfg(test)]
use crate::device_pairing::Relationship;
use crate::device_pairing::{
    DeviceAction, ForwardedNotification, OutboundPeer, PairingCore, PairingSnapshot,
};
use mdns_sd::{ResolvedService, ScopedIp, ServiceDaemon, ServiceEvent, ServiceInfo};
use serde::Serialize;

const SERVICE_TYPE: &str = "_qingdevice._tcp.local.";
const PROTOCOL_VERSION: &str = "1";
const PROBE_TIMEOUT: Duration = Duration::from_millis(400);
const RECHECK_INTERVAL: Duration = Duration::from_secs(4);
const MAX_CANDIDATES: usize = 64;

#[derive(Clone)]
struct Candidate {
    id: String,
    name: String,
    platform: String,
    service_name: String,
    addresses: Vec<SocketAddr>,
}

#[derive(Default)]
struct Shared {
    enabled: bool,
    error: Option<String>,
    candidates: BTreeMap<String, Candidate>,
}

struct Runtime {
    daemon: ServiceDaemon,
    fullname: String,
    stop: Arc<AtomicBool>,
    workers: Vec<thread::JoinHandle<()>>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DeviceSnapshot {
    enabled: bool,
    error: Option<String>,
    nearby: Vec<NearbyDevice>,
    pairing: PairingSnapshot,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct NearbyDevice {
    id: String,
    name: String,
    platform: String,
    // This is intentionally not a trust or authentication state.
    status: &'static str,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TransferTarget {
    pub device_id: String,
    pub platform: String,
    pub addresses: Vec<String>,
}

pub struct DeviceManager {
    identity: Result<String, String>,
    name: String,
    settings_file: Option<PathBuf>,
    shared: Arc<Mutex<Shared>>,
    runtime: Mutex<Option<Runtime>>,
    pairing: Result<Arc<PairingCore>, String>,
}

impl DeviceManager {
    pub fn is_enabled(&self) -> bool {
        self.shared
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .enabled
    }

    pub fn take_notifications(&self) -> Vec<ForwardedNotification> {
        self.pairing
            .as_ref()
            .map(|core| core.take_notifications())
            .unwrap_or_default()
    }
    pub fn new(profile: Option<&Path>) -> Self {
        let identity = profile
            .ok_or_else(|| "设备数据目录不可用。".to_string())
            .and_then(load_or_create_identity);
        let name = std::env::var("COMPUTERNAME")
            .or_else(|_| std::env::var("HOSTNAME"))
            .unwrap_or_else(|_| "QingToolbox".to_string());
        let name = safe_name(&name);
        let pairing = match (profile, identity.as_ref()) {
            (Some(profile), Ok(id)) => PairingCore::new(profile, id, &name),
            _ => Err("设备配对身份不可用。".to_string()),
        };
        Self {
            identity,
            name,
            settings_file: profile.map(|root| root.join("Devices").join("discovery-enabled")),
            shared: Arc::new(Mutex::new(Shared::default())),
            runtime: Mutex::new(None),
            pairing,
        }
    }

    pub fn snapshot(&self) -> DeviceSnapshot {
        let shared = self.shared.lock().unwrap_or_else(|e| e.into_inner());
        let mut nearby = shared
            .candidates
            .values()
            .map(|candidate| NearbyDevice {
                id: candidate.id.clone(),
                name: candidate.name.clone(),
                platform: candidate.platform.clone(),
                status: "Unverified",
            })
            .collect::<Vec<_>>();
        nearby.sort_by(|a, b| a.name.cmp(&b.name).then(a.id.cmp(&b.id)));
        DeviceSnapshot {
            enabled: shared.enabled,
            error: shared
                .error
                .clone()
                .or_else(|| self.identity.as_ref().err().cloned()),
            nearby,
            pairing: match &self.pairing {
                Ok(core) => core.snapshot(),
                Err(error) => PairingSnapshot::unavailable(error.clone()),
            },
        }
    }

    pub fn request_pairing(&self, device_id: &str) -> Result<(), String> {
        let core = self.pairing.as_ref().map_err(Clone::clone)?;
        let runtime = self
            .runtime
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        let active = runtime.as_ref().ok_or("请先开启附近发现。")?;
        let candidate = self
            .shared
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .candidates
            .values()
            .find(|peer| peer.id == device_id)
            .cloned()
            .ok_or("附近设备已离线，请刷新后重试。")?;
        core.begin_outbound(
            OutboundPeer {
                discovery_id: candidate.id,
                addresses: candidate.addresses,
            },
            Arc::clone(&active.stop),
        )
    }

    pub fn transfer_target(&self, peer_id: &str) -> Result<TransferTarget, String> {
        let pairing = self.pairing.as_ref().map_err(Clone::clone)?.snapshot();
        let peer = pairing
            .paired
            .iter()
            .find(|peer| peer.id == peer_id)
            .ok_or("设备未配对。")?;
        if !pairing.online.iter().any(|id| id == peer_id) {
            return Err("设备当前不在线。".to_string());
        }
        let shared = self
            .shared
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        let candidate = shared
            .candidates
            .values()
            .find(|candidate| {
                candidate.id == peer.discovery_id && candidate.platform == peer.platform
            })
            .ok_or("设备传输端点暂不可用。")?;
        Ok(TransferTarget {
            device_id: peer.discovery_id.clone(),
            platform: peer.platform.clone(),
            addresses: candidate
                .addresses
                .iter()
                .map(|address| address.ip().to_string())
                .collect(),
        })
    }

    /// Resolve an incoming legacy transfer socket against an authenticated,
    /// currently discoverable paired device. A name alone is never sufficient.
    pub fn paired_transfer_peer(
        &self,
        platform: &str,
        device_id: Option<&str>,
        addresses: &[String],
    ) -> Option<(String, String)> {
        let pairing = self.pairing.as_ref().ok()?.snapshot();
        let matches = pairing
            .paired
            .iter()
            .filter(|peer| {
                peer.platform == platform
                    && pairing.online.iter().any(|id| id == &peer.id)
                    && (device_id.is_some_and(|id| id.eq_ignore_ascii_case(&peer.discovery_id))
                        || device_id.is_none()
                            && self.transfer_target(&peer.id).is_ok_and(|target| {
                                target.addresses.iter().any(|address| {
                                    addresses.iter().any(|incoming| same_ip(address, incoming))
                                })
                            }))
            })
            .collect::<Vec<_>>();
        if matches.len() != 1 {
            return None;
        }
        Some((matches[0].id.clone(), matches[0].name.clone()))
    }

    pub fn decide_pairing(&self, session_id: &str, approve: bool) -> Result<(), String> {
        self.pairing
            .as_ref()
            .map_err(Clone::clone)?
            .decide(session_id, approve)
    }

    pub fn set_relationship(&self, peer_id: &str, intimate: bool) -> Result<(), String> {
        self.request_action(
            peer_id,
            if intimate {
                DeviceAction::Upgrade
            } else {
                DeviceAction::Demote
            },
        )
    }

    pub fn revoke_pairing(&self, peer_id: &str) -> Result<(), String> {
        self.request_action(peer_id, DeviceAction::Disconnect)
    }

    pub fn decide_action(&self, session_id: &str, approve: bool) -> Result<(), String> {
        self.pairing
            .as_ref()
            .map_err(Clone::clone)?
            .decide_action(session_id, approve)
    }

    fn request_action(&self, peer_id: &str, action: DeviceAction) -> Result<(), String> {
        let core = self.pairing.as_ref().map_err(Clone::clone)?;
        let peer = core
            .snapshot()
            .paired
            .into_iter()
            .find(|record| record.id == peer_id)
            .ok_or("设备未配对。")?;
        let runtime = self
            .runtime
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        let candidate = self
            .shared
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .candidates
            .values()
            .find(|candidate| candidate.id == peer.discovery_id)
            .cloned();
        let Some(active) = runtime.as_ref() else {
            return if action == DeviceAction::Disconnect {
                core.revoke_offline(peer_id)
            } else {
                Err("设备发现未开启。".to_string())
            };
        };
        let Some(candidate) = candidate else {
            return if action == DeviceAction::Disconnect {
                core.revoke_offline(peer_id)
            } else {
                Err("对方目前不在线。".to_string())
            };
        };
        core.request_action(
            peer_id,
            OutboundPeer {
                discovery_id: candidate.id,
                addresses: candidate.addresses,
            },
            action,
            Arc::clone(&active.stop),
        )
    }

    pub fn set_enabled(&self, enabled: bool) -> DeviceSnapshot {
        let save_error = self
            .settings_file
            .as_ref()
            .and_then(|file| fs::write(file, if enabled { b"1" } else { b"0" }).err());
        if enabled {
            self.start();
        } else {
            self.stop();
        }
        if let Some(error) = save_error {
            self.shared.lock().unwrap_or_else(|e| e.into_inner()).error =
                Some(format!("无法保存设备发现设置：{error}"));
        }
        self.snapshot()
    }

    pub fn restore_enabled(&self) {
        let Some(file) = &self.settings_file else {
            return;
        };
        match fs::read_to_string(file) {
            Ok(value) if value.trim() == "1" => self.start(),
            Ok(value) if value.trim() == "0" => {}
            Ok(_) => {
                self.shared.lock().unwrap_or_else(|e| e.into_inner()).error =
                    Some("设备发现设置无效。".to_string())
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => {
                self.shared.lock().unwrap_or_else(|e| e.into_inner()).error =
                    Some(format!("无法读取设备发现设置：{error}"))
            }
        }
    }

    fn start(&self) {
        let mut runtime = self.runtime.lock().unwrap_or_else(|e| e.into_inner());
        if runtime.is_some() {
            return;
        }
        let result = self.start_inner();
        match result {
            Ok(active) => {
                *runtime = Some(active);
                let mut shared = self.shared.lock().unwrap_or_else(|e| e.into_inner());
                shared.enabled = true;
                shared.error = None;
            }
            Err(error) => {
                let mut shared = self.shared.lock().unwrap_or_else(|e| e.into_inner());
                shared.enabled = false;
                shared.error = Some(error);
                shared.candidates.clear();
            }
        }
    }

    fn start_inner(&self) -> Result<Runtime, String> {
        let id = self.identity.as_ref().map_err(Clone::clone)?;
        let listener =
            TcpListener::bind(("0.0.0.0", 0)).map_err(|e| format!("无法打开设备发现端口：{e}"))?;
        listener.set_nonblocking(true).map_err(|e| e.to_string())?;
        let port = listener.local_addr().map_err(|e| e.to_string())?.port();
        let daemon = ServiceDaemon::new().map_err(|e| format!("局域网发现不可用：{e}"))?;
        let instance = format!("Qing-{}", &id[..12]);
        let host = format!("{}.local.", instance.to_ascii_lowercase());
        let properties = [
            ("v", PROTOCOL_VERSION),
            ("id", id.as_str()),
            ("name", self.name.as_str()),
            ("pf", "windows"),
        ];
        let service =
            match ServiceInfo::new(SERVICE_TYPE, &instance, &host, "", port, &properties[..]) {
                Ok(service) => service.enable_addr_auto(),
                Err(error) => {
                    let _ = daemon.shutdown();
                    return Err(format!("设备服务创建失败：{error}"));
                }
            };
        let fullname = service.get_fullname().to_string();
        if let Err(error) = daemon.register(service) {
            let _ = daemon.shutdown();
            return Err(format!("设备服务注册失败：{error}"));
        }
        let receiver = match daemon.browse(SERVICE_TYPE) {
            Ok(receiver) => receiver,
            Err(error) => {
                let _ = daemon.unregister(&fullname);
                let _ = daemon.shutdown();
                return Err(format!("设备发现启动失败：{error}"));
            }
        };
        let stop = Arc::new(AtomicBool::new(false));
        let accept_stop = Arc::clone(&stop);
        let accept_id = id.clone();
        let pairing = self.pairing.as_ref().ok().cloned();
        let accept = thread::spawn(move || accept_loop(listener, accept_stop, accept_id, pairing));
        let browse_stop = Arc::clone(&stop);
        let shared = Arc::clone(&self.shared);
        let own_id = id.clone();
        let browse_pairing = self.pairing.as_ref().ok().cloned();
        let browse = thread::spawn(move || {
            browse_loop(receiver, browse_stop, shared, own_id, browse_pairing)
        });
        Ok(Runtime {
            daemon,
            fullname,
            stop,
            workers: vec![accept, browse],
        })
    }

    pub fn stop(&self) {
        let mut runtime = self.runtime.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(mut active) = runtime.take() {
            active.stop.store(true, Ordering::Release);
            if let Ok(core) = &self.pairing {
                core.cancel_pending();
            }
            let _ = active.daemon.unregister(&active.fullname);
            let _ = active.daemon.shutdown();
            for worker in active.workers.drain(..) {
                let _ = worker.join();
            }
        }
        let mut shared = self.shared.lock().unwrap_or_else(|e| e.into_inner());
        shared.enabled = false;
        shared.error = None;
        shared.candidates.clear();
    }
}

fn load_or_create_identity(profile: &Path) -> Result<String, String> {
    let directory = profile.join("Devices");
    fs::create_dir_all(&directory).map_err(|e| format!("无法创建设备数据目录：{e}"))?;
    let file = directory.join("discovery-id");
    if let Ok(value) = fs::read_to_string(&file) {
        let id = value.trim();
        if valid_id(id) {
            return Ok(id.to_ascii_lowercase());
        }
        return Err("设备发现身份文件无效，请手动检查数据目录。".to_string());
    }
    let mut bytes = [0u8; 16];
    getrandom::fill(&mut bytes).map_err(|e| format!("无法生成设备标识：{e}"))?;
    let id = bytes.iter().map(|b| format!("{b:02x}")).collect::<String>();
    // create_new prevents another process using the same profile from
    // silently replacing the first instance's identifier.
    match fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&file)
    {
        Ok(mut handle) => handle
            .write_all(id.as_bytes())
            .map_err(|e| e.to_string())
            .map(|_| id),
        Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => {
            let existing = fs::read_to_string(&file).map_err(|e| e.to_string())?;
            let existing = existing.trim();
            if valid_id(existing) {
                Ok(existing.to_ascii_lowercase())
            } else {
                Err("设备发现身份文件无效。".to_string())
            }
        }
        Err(error) => Err(format!("无法保存设备标识：{error}")),
    }
}

fn valid_id(value: &str) -> bool {
    value.len() == 32 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn same_ip(first: &str, second: &str) -> bool {
    let parse = |value: &str| {
        value
            .split('%')
            .next()
            .and_then(|raw| raw.parse::<IpAddr>().ok())
    };
    match (parse(first), parse(second)) {
        (Some(IpAddr::V6(value)), Some(other)) if value.to_ipv4_mapped().is_some() => {
            Some(IpAddr::V4(value.to_ipv4_mapped().unwrap())) == Some(other)
        }
        (Some(first), Some(IpAddr::V6(value))) if value.to_ipv4_mapped().is_some() => {
            Some(first) == Some(IpAddr::V4(value.to_ipv4_mapped().unwrap()))
        }
        (Some(first), Some(second)) => first == second,
        _ => false,
    }
}

fn safe_name(value: &str) -> String {
    let name = value
        .trim()
        .chars()
        .filter(|c| !c.is_control())
        .take(64)
        .collect::<String>();
    if name.is_empty() {
        "QingToolbox".to_string()
    } else {
        name
    }
}

fn parse_candidate(info: &ResolvedService, own_id: &str) -> Option<Candidate> {
    if info.get_property_val_str("v")? != PROTOCOL_VERSION {
        return None;
    }
    let id = info.get_property_val_str("id")?.to_ascii_lowercase();
    if !valid_id(&id) || id == own_id {
        return None;
    }
    let platform = info.get_property_val_str("pf")?;
    if platform != "windows" && platform != "android" {
        return None;
    }
    let name = safe_name(info.get_property_val_str("name")?);
    let addresses = info
        .get_addresses()
        .iter()
        .filter_map(|address| match address {
            ScopedIp::V4(v4) => Some(SocketAddr::new(IpAddr::V4(*v4.addr()), info.get_port())),
            ScopedIp::V6(v6) => Some(SocketAddr::V6(SocketAddrV6::new(
                *v6.addr(),
                info.get_port(),
                0,
                v6.scope_id().index,
            ))),
            _ => None,
        })
        .filter(|address| !address.ip().is_unspecified() && !address.ip().is_multicast())
        .take(8)
        .collect::<Vec<_>>();
    if addresses.is_empty() || info.get_port() == 0 {
        return None;
    }
    Some(Candidate {
        id,
        name,
        platform: platform.to_string(),
        service_name: info.get_fullname().to_ascii_lowercase(),
        addresses,
    })
}

fn browse_loop(
    receiver: mdns_sd::Receiver<ServiceEvent>,
    stop: Arc<AtomicBool>,
    shared: Arc<Mutex<Shared>>,
    own_id: String,
    pairing: Option<Arc<PairingCore>>,
) {
    let mut last_recheck = Instant::now();
    while !stop.load(Ordering::Acquire) {
        match receiver.recv_timeout(Duration::from_millis(300)) {
            Ok(ServiceEvent::ServiceResolved(info)) => {
                if let Some(mut candidate) = parse_candidate(&info, &own_id) {
                    if let Some(verified_address) = probe(&candidate) {
                        candidate.addresses = vec![verified_address];
                        let stored = {
                            let mut state = shared.lock().unwrap_or_else(|e| e.into_inner());
                            if state.candidates.len() < MAX_CANDIDATES
                                || state.candidates.contains_key(&candidate.service_name)
                            {
                                state
                                    .candidates
                                    .insert(candidate.service_name.clone(), candidate.clone());
                                true
                            } else {
                                false
                            }
                        };
                        if stored {
                            if let Some(core) = &pairing {
                                let peer = OutboundPeer {
                                    discovery_id: candidate.id,
                                    addresses: candidate.addresses,
                                };
                                core.retry_tombstone(peer.clone(), Arc::clone(&stop));
                                core.probe_online(peer, Arc::clone(&stop));
                            }
                        }
                    }
                }
            }
            Ok(ServiceEvent::ServiceRemoved(_, fullname)) => {
                shared
                    .lock()
                    .unwrap_or_else(|e| e.into_inner())
                    .candidates
                    .remove(&fullname.to_ascii_lowercase());
            }
            Err(mdns_sd::RecvTimeoutError::Timeout) => {}
            Err(_) => break,
            _ => {}
        }
        if last_recheck.elapsed() >= RECHECK_INTERVAL {
            // Re-probe because mDNS goodbye/removal events are not guaranteed.
            // Never hold the state lock during network I/O.
            let candidates = shared
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .candidates
                .values()
                .cloned()
                .collect::<Vec<_>>();
            for candidate in candidates {
                if stop.load(Ordering::Acquire) {
                    break;
                }
                if probe(&candidate).is_none() {
                    shared
                        .lock()
                        .unwrap_or_else(|e| e.into_inner())
                        .candidates
                        .remove(&candidate.service_name);
                } else if let Some(core) = &pairing {
                    core.retry_tombstone(
                        OutboundPeer {
                            discovery_id: candidate.id.clone(),
                            addresses: candidate.addresses.clone(),
                        },
                        Arc::clone(&stop),
                    );
                    core.probe_online(
                        OutboundPeer {
                            discovery_id: candidate.id.clone(),
                            addresses: candidate.addresses.clone(),
                        },
                        Arc::clone(&stop),
                    );
                }
            }
            last_recheck = Instant::now();
        }
    }
    if !stop.load(Ordering::Acquire) {
        let mut state = shared.lock().unwrap_or_else(|e| e.into_inner());
        state.error = Some("附近设备发现已中断，请关闭后重试。".to_string());
        state.candidates.clear();
    }
}

fn accept_loop(
    listener: TcpListener,
    stop: Arc<AtomicBool>,
    own_id: String,
    pairing: Option<Arc<PairingCore>>,
) {
    let id = decode_id(&own_id).expect("validated local discovery identifier");
    while !stop.load(Ordering::Acquire) {
        match listener.accept() {
            Ok((mut stream, _)) => {
                // On Windows an accepted socket can inherit the listener's
                // nonblocking mode. Pairing uses bounded blocking frame I/O.
                let _ = stream.set_nonblocking(false);
                let _ = stream.set_read_timeout(Some(PROBE_TIMEOUT));
                let _ = stream.set_write_timeout(Some(PROBE_TIMEOUT));
                let mut marker = [0u8; 4];
                if stream.read_exact(&mut marker).is_ok() {
                    if &marker == b"QDB1" {
                        let mut nonce = [0u8; 16];
                        if stream.read_exact(&mut nonce).is_ok() {
                            let mut answer = [0u8; 36];
                            answer[..4].copy_from_slice(b"QDA1");
                            answer[4..20].copy_from_slice(&nonce);
                            answer[20..].copy_from_slice(&id);
                            let _ = stream.write_all(&answer);
                        }
                    } else if &marker == b"QDP1" {
                        if let Some(core) = &pairing {
                            core.accept(stream, Arc::clone(&stop));
                        }
                    } else if &marker == b"QDM1" {
                        if let Some(core) = &pairing {
                            core.accept_action(stream, Arc::clone(&stop));
                        }
                    }
                }
            }
            Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                thread::sleep(Duration::from_millis(40))
            }
            Err(_) => break,
        }
    }
}

fn probe(candidate: &Candidate) -> Option<SocketAddr> {
    let expected_id = decode_id(&candidate.id)?;
    for socket in &candidate.addresses {
        let Ok(mut stream) = TcpStream::connect_timeout(socket, PROBE_TIMEOUT) else {
            continue;
        };
        let _ = stream.set_read_timeout(Some(PROBE_TIMEOUT));
        let _ = stream.set_write_timeout(Some(PROBE_TIMEOUT));
        let mut nonce = [0u8; 16];
        if getrandom::fill(&mut nonce).is_err() {
            return None;
        }
        let mut request = [0u8; 20];
        request[..4].copy_from_slice(b"QDB1");
        request[4..].copy_from_slice(&nonce);
        let mut answer = [0u8; 36];
        if stream.write_all(&request).is_ok()
            && stream.read_exact(&mut answer).is_ok()
            && &answer[..4] == b"QDA1"
            && answer[4..20] == nonce
            && answer[20..] == expected_id
        {
            return Some(*socket);
        }
    }
    None
}

fn decode_id(value: &str) -> Option<[u8; 16]> {
    if !valid_id(value) {
        return None;
    }
    let mut bytes = [0u8; 16];
    for (index, byte) in bytes.iter_mut().enumerate() {
        *byte = u8::from_str_radix(&value[index * 2..index * 2 + 2], 16).ok()?;
    }
    Some(bytes)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn two_isolated_local_peers_probe_and_disappear_when_stopped() {
        let first_id = "0123456789abcdef0123456789abcdef".to_string();
        let second_id = "fedcba9876543210fedcba9876543210".to_string();
        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        listener.set_nonblocking(true).unwrap();
        let port = listener.local_addr().unwrap().port();
        let stop = Arc::new(AtomicBool::new(false));
        let worker_stop = Arc::clone(&stop);
        let worker = thread::spawn(move || accept_loop(listener, worker_stop, second_id, None));
        let candidate = Candidate {
            id: "fedcba9876543210fedcba9876543210".to_string(),
            name: "Second test device".to_string(),
            platform: "windows".to_string(),
            service_name: "test".to_string(),
            addresses: vec![SocketAddr::new("127.0.0.1".parse().unwrap(), port)],
        };
        assert_ne!(first_id, candidate.id);
        assert!(probe(&candidate).is_some());
        let mut fake = candidate.clone();
        fake.id = first_id;
        assert!(
            probe(&fake).is_none(),
            "a live port cannot claim another device identity"
        );
        stop.store(true, Ordering::Release);
        worker.join().unwrap();
        assert!(
            probe(&candidate).is_none(),
            "stopped peers must leave discovery"
        );
    }

    #[test]
    fn corrupt_or_missing_identity_never_becomes_trusted() {
        assert!(!valid_id("localhost"));
        assert!(!valid_id("0123456789abcdef0123456789abcdeg"));
        let manager = DeviceManager::new(None);
        assert!(!manager.set_enabled(true).enabled);
        assert!(manager.snapshot().nearby.is_empty());
    }

    #[test]
    #[ignore = "requires local multicast support and firewall permission"]
    fn two_device_instances_discover_each_other_on_one_computer() {
        fn isolated(id: &str, name: &str) -> DeviceManager {
            DeviceManager {
                identity: Ok(id.to_string()),
                name: name.to_string(),
                settings_file: None,
                shared: Arc::new(Mutex::new(Shared::default())),
                runtime: Mutex::new(None),
                pairing: Ok(PairingCore::ephemeral(id, name)),
            }
        }
        let first = isolated("0123456789abcdef0123456789abcdef", "Qing test one");
        let second = isolated("fedcba9876543210fedcba9876543210", "Qing test two");
        first.start();
        second.start();
        assert!(first.snapshot().enabled, "{:?}", first.snapshot().error);
        assert!(second.snapshot().enabled, "{:?}", second.snapshot().error);
        let deadline = Instant::now() + Duration::from_secs(12);
        while Instant::now() < deadline {
            if first.snapshot().nearby.len() == 1 && second.snapshot().nearby.len() == 1 {
                break;
            }
            thread::sleep(Duration::from_millis(250));
        }
        assert_eq!(first.snapshot().nearby[0].name, "Qing test two");
        assert_eq!(second.snapshot().nearby[0].name, "Qing test one");
        first
            .request_pairing("fedcba9876543210fedcba9876543210")
            .unwrap();
        let pair_deadline = Instant::now() + Duration::from_secs(8);
        while Instant::now() < pair_deadline {
            if first.snapshot().pairing.pending.len() == 1
                && second.snapshot().pairing.pending.len() == 1
            {
                break;
            }
            thread::sleep(Duration::from_millis(50));
        }
        assert_eq!(
            first.snapshot().pairing.pending.len(),
            1,
            "first pairing error: {:?}; second pairing error: {:?}",
            first.snapshot().pairing.error,
            second.snapshot().pairing.error
        );
        assert_eq!(
            second.snapshot().pairing.pending.len(),
            1,
            "second pairing error: {:?}",
            second.snapshot().pairing.error
        );
        let first_pending = first.snapshot().pairing.pending[0].clone();
        let second_pending = second.snapshot().pairing.pending[0].clone();
        assert_eq!(first_pending.code, second_pending.code);
        first
            .decide_pairing(&first_pending.session_id, true)
            .unwrap();
        assert!(first.snapshot().pairing.paired.is_empty());
        second
            .decide_pairing(&second_pending.session_id, true)
            .unwrap();
        let commit_deadline = Instant::now() + Duration::from_secs(8);
        while Instant::now() < commit_deadline {
            if first.snapshot().pairing.paired.len() == 1
                && second.snapshot().pairing.paired.len() == 1
            {
                break;
            }
            thread::sleep(Duration::from_millis(50));
        }
        assert_eq!(
            first.snapshot().pairing.paired[0].relationship,
            Relationship::Connected
        );
        assert_eq!(
            second.snapshot().pairing.paired[0].relationship,
            Relationship::Connected
        );
        first.stop();
        second.stop();
        assert!(first.snapshot().nearby.is_empty());
        assert!(second.snapshot().nearby.is_empty());
    }
}
