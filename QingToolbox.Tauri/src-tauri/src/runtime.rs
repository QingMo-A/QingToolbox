use std::{
    collections::BTreeMap,
    fs,
    io::{BufRead, BufReader, Write},
    process::{Child, ChildStdin, ChildStdout, Command, ExitStatus, Stdio},
    sync::mpsc::{self, Receiver, TryRecvError},
    thread,
    time::{Duration, Instant},
};

use serde::Serialize;
use serde_json::Value;

use crate::{
    modules::ModuleRecord,
    paths::module_data_directory,
    protocol::{decode_line, ProtocolEnvelope, MAX_FRAME_BYTES},
};

const SHUTDOWN_GRACE: Duration = Duration::from_millis(350);
const HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(5);
const INVOKE_TIMEOUT: Duration = Duration::from_secs(10);

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum ModuleRuntimeState {
    NotStarted,
    Starting,
    Running,
    Stopped,
    Failed,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ModuleRuntimeSnapshot {
    pub module_id: String,
    pub state: ModuleRuntimeState,
    pub generation: u64,
    pub last_error: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RuntimeError {
    pub code: &'static str,
    pub message: String,
}

struct RunningModule {
    child: Child,
    stdin: ChildStdin,
    generation: u64,
    hello_request_id: String,
    nonce: String,
    handshake_deadline: Instant,
    handshake_complete: bool,
    messages: Receiver<RuntimeMessage>,
}

#[derive(Debug)]
enum RuntimeMessage {
    Frame(ProtocolEnvelope<Value>),
    Invalid(String),
    Io(String),
}

/// Owns only processes explicitly started by this host. Module paths come
/// from the backend discovery index; callers never provide an executable path.
pub struct ModuleRuntimeManager {
    running: BTreeMap<String, RunningModule>,
    snapshots: BTreeMap<String, ModuleRuntimeSnapshot>,
    next_generation: u64,
    next_request_id: u64,
}

impl ModuleRuntimeManager {
    pub fn new() -> Self {
        Self {
            running: BTreeMap::new(),
            snapshots: BTreeMap::new(),
            next_generation: 0,
            next_request_id: 0,
        }
    }

    pub fn start(
        &mut self,
        module_id: &str,
        record: &ModuleRecord,
    ) -> Result<ModuleRuntimeSnapshot, RuntimeError> {
        if let Some(snapshot) = self.refresh_one(module_id) {
            if matches!(
                snapshot.state,
                ModuleRuntimeState::Starting | ModuleRuntimeState::Running
            ) {
                return Ok(snapshot);
            }
        }

        if !is_supported_entry(&record.entry) || !entry_still_belongs_to_module(record) {
            return Err(RuntimeError {
                code: "runtimeUnsupported",
                message: "新宿主模块必须提供独立的 Windows executable entry。".to_string(),
            });
        }

        self.next_generation = self.next_generation.saturating_add(1);
        let generation = self.next_generation;
        let nonce = new_nonce().map_err(|message| RuntimeError {
            code: "nonceGenerationFailed",
            message,
        })?;
        let data_directory = module_data_directory(module_id).map_err(|error| RuntimeError {
            code: "moduleDataPathInvalid",
            message: format!("无法解析模块数据目录：{error}"),
        })?;
        fs::create_dir_all(&data_directory).map_err(|error| RuntimeError {
            code: "moduleDataUnavailable",
            message: format!("无法创建模块数据目录：{error}"),
        })?;
        let mut command = Command::new(&record.entry);
        command
            .current_dir(&record.directory)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .env("QINGTOOLBOX_MODULE_PROTOCOL", "qing.module/1")
            .env("QINGTOOLBOX_MODULE_ID", module_id)
            .env("QINGTOOLBOX_MODULE_NONCE", &nonce)
            .env("QINGTOOLBOX_MODULE_DATA_DIR", &data_directory);
        apply_hidden_process_flags(&mut command);

        let mut child = command.spawn().map_err(|error| RuntimeError {
            code: "runtimeStartFailed",
            message: format!("无法启动模块进程：{error}"),
        })?;
        let mut stdin = match child.stdin.take() {
            Some(stdin) => stdin,
            None => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(RuntimeError {
                    code: "runtimePipeUnavailable",
                    message: "模块进程没有可用的 stdin 管道。".to_string(),
                });
            }
        };
        let stderr = child.stderr.take();
        let messages = match child.stdout.take() {
            Some(stdout) => spawn_stdout_reader(stdout),
            None => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(RuntimeError {
                    code: "runtimePipeUnavailable",
                    message: "模块进程没有可用的 stdout 管道。".to_string(),
                });
            }
        };

        let hello_request_id = format!("hello-{generation}");
        let hello = ProtocolEnvelope::new(
            "module.hello.request",
            hello_request_id.clone(),
            serde_json::json!({
                "moduleId": module_id,
                "nonce": nonce,
            }),
        );
        if let Err(error) = write_frame(&mut stdin, &hello) {
            let _ = child.kill();
            let _ = child.wait();
            return Err(error);
        }
        drain_stderr(stderr);

        self.running.insert(
            module_id.to_string(),
            RunningModule {
                child,
                stdin,
                generation,
                hello_request_id,
                nonce,
                handshake_deadline: Instant::now() + HANDSHAKE_TIMEOUT,
                handshake_complete: false,
                messages,
            },
        );
        let snapshot = ModuleRuntimeSnapshot {
            module_id: module_id.to_string(),
            state: ModuleRuntimeState::Starting,
            generation,
            last_error: None,
        };
        self.snapshots
            .insert(module_id.to_string(), snapshot.clone());
        Ok(snapshot)
    }

    /// Invoke one manifest-declared operation and wait for its matching
    /// response. The runtime mutex is held by the caller while this method is
    /// waiting, so the supervisor cannot consume the response out from under
    /// the request. Vue never gets access to the process pipe itself.
    pub fn invoke(
        &mut self,
        module_id: &str,
        record: &ModuleRecord,
        method: &str,
        payload: Value,
    ) -> Result<Value, RuntimeError> {
        if !crate::valid_operation_name(method) {
            return Err(RuntimeError {
                code: "operationInvalid",
                message: "模块操作名无效。".to_string(),
            });
        }
        let initial = self.start(module_id, record)?;
        if matches!(
            initial.state,
            ModuleRuntimeState::Starting | ModuleRuntimeState::Running
        ) {
            let deadline = Instant::now() + HANDSHAKE_TIMEOUT;
            loop {
                let snapshot = self.snapshot(module_id);
                match snapshot.state {
                    ModuleRuntimeState::Running => break,
                    ModuleRuntimeState::Failed => {
                        return Err(RuntimeError {
                            code: "moduleHandshakeFailed",
                            message: snapshot
                                .last_error
                                .unwrap_or_else(|| "模块 hello 握手失败。".to_string()),
                        });
                    }
                    ModuleRuntimeState::Starting if Instant::now() < deadline => {
                        thread::sleep(Duration::from_millis(10));
                    }
                    ModuleRuntimeState::Starting => {
                        return Err(RuntimeError {
                            code: "moduleHandshakeTimeout",
                            message: "模块 hello 握手超时。".to_string(),
                        });
                    }
                    _ => {
                        return Err(RuntimeError {
                            code: "moduleNotRunning",
                            message: "模块没有处于运行状态。".to_string(),
                        });
                    }
                }
            }
        }

        self.next_request_id = self.next_request_id.saturating_add(1);
        let request_id = format!("invoke-{}-{}", initial.generation, self.next_request_id);
        let request = ProtocolEnvelope::new(
            "module.invoke.request",
            request_id.clone(),
            serde_json::json!({
                "method": method,
                "payload": payload,
            }),
        );

        {
            let running = self
                .running
                .get_mut(module_id)
                .ok_or_else(|| RuntimeError {
                    code: "moduleNotRunning",
                    message: "模块没有处于运行状态。".to_string(),
                })?;
            if !running.handshake_complete {
                return Err(RuntimeError {
                    code: "moduleNotRunning",
                    message: "模块 hello 尚未完成。".to_string(),
                });
            }
            write_frame(&mut running.stdin, &request)?;
        }

        let deadline = Instant::now() + INVOKE_TIMEOUT;
        loop {
            let mut response = None;
            let mut failure = None;
            {
                let running = self
                    .running
                    .get_mut(module_id)
                    .ok_or_else(|| RuntimeError {
                        code: "moduleNotRunning",
                        message: "模块在调用期间退出。".to_string(),
                    })?;
                loop {
                    match running.messages.try_recv() {
                        Ok(RuntimeMessage::Frame(frame)) if frame.request_id == request_id => {
                            if frame.message_type != "module.invoke.response" {
                                failure = Some(RuntimeError {
                                    code: "moduleResponseInvalid",
                                    message: format!(
                                        "模块返回了意外的 invoke 消息类型：{}",
                                        frame.message_type
                                    ),
                                });
                            } else if let Some(error) = frame.error {
                                failure = Some(RuntimeError {
                                    code: "moduleInvokeFailed",
                                    message: format!("{} ({})", error.message, error.code),
                                });
                            } else {
                                response = Some(frame.payload);
                            }
                            break;
                        }
                        Ok(RuntimeMessage::Frame(_)) => {
                            // Events and late responses for another request
                            // cannot satisfy this request and are ignored.
                        }
                        Ok(RuntimeMessage::Invalid(error)) | Ok(RuntimeMessage::Io(error)) => {
                            failure = Some(RuntimeError {
                                code: "moduleProtocolFailed",
                                message: error,
                            });
                            break;
                        }
                        Err(TryRecvError::Empty) | Err(TryRecvError::Disconnected) => break,
                    }
                }
                if failure.is_none() && response.is_none() {
                    match running.child.try_wait() {
                        Ok(Some(status)) => {
                            failure = Some(RuntimeError {
                                code: "moduleExited",
                                message: format!("模块进程已退出：{status}"),
                            });
                        }
                        Err(error) => {
                            failure = Some(RuntimeError {
                                code: "moduleStatusUnavailable",
                                message: format!("无法读取模块进程状态：{error}"),
                            });
                        }
                        Ok(None) => {}
                    }
                }
            }

            if let Some(response) = response {
                return Ok(response);
            }
            if let Some(failure) = failure {
                if failure.code != "moduleInvokeFailed" {
                    self.fail_running(module_id, failure.message.clone());
                }
                return Err(failure);
            }
            if Instant::now() >= deadline {
                let failure = RuntimeError {
                    code: "moduleInvokeTimeout",
                    message: "模块操作响应超时。".to_string(),
                };
                self.fail_running(module_id, failure.message.clone());
                return Err(failure);
            }
            thread::sleep(Duration::from_millis(10));
        }
    }

    pub fn stop(&mut self, module_id: &str) -> ModuleRuntimeSnapshot {
        let previous =
            self.snapshots
                .get(module_id)
                .cloned()
                .unwrap_or_else(|| ModuleRuntimeSnapshot {
                    module_id: module_id.to_string(),
                    state: ModuleRuntimeState::NotStarted,
                    generation: 0,
                    last_error: None,
                });

        if let Some(mut running) = self.running.remove(module_id) {
            let shutdown = ProtocolEnvelope::new(
                "module.shutdown.request",
                format!("shutdown-{}", previous.generation),
                serde_json::json!({}),
            );
            let _ = write_frame(&mut running.stdin, &shutdown);
            let deadline = Instant::now() + SHUTDOWN_GRACE;
            loop {
                match running.child.try_wait() {
                    Ok(Some(_)) => break,
                    Ok(None) if Instant::now() < deadline => {
                        thread::sleep(Duration::from_millis(15));
                    }
                    _ => {
                        let _ = running.child.kill();
                        let _ = running.child.wait();
                        break;
                    }
                }
            }
        }

        let snapshot = ModuleRuntimeSnapshot {
            module_id: module_id.to_string(),
            state: ModuleRuntimeState::Stopped,
            generation: previous.generation,
            last_error: None,
        };
        self.snapshots
            .insert(module_id.to_string(), snapshot.clone());
        snapshot
    }

    pub fn snapshot(&mut self, module_id: &str) -> ModuleRuntimeSnapshot {
        self.refresh_one(module_id).unwrap_or_else(|| {
            self.snapshots
                .get(module_id)
                .cloned()
                .unwrap_or_else(|| ModuleRuntimeSnapshot {
                    module_id: module_id.to_string(),
                    state: ModuleRuntimeState::NotStarted,
                    generation: 0,
                    last_error: None,
                })
        })
    }

    pub fn snapshots(&mut self) -> Vec<ModuleRuntimeSnapshot> {
        let ids = self.running.keys().cloned().collect::<Vec<String>>();
        for id in ids {
            let _ = self.refresh_one(&id);
        }
        self.snapshots.values().cloned().collect()
    }

    pub fn stop_all(&mut self) {
        let ids = self.running.keys().cloned().collect::<Vec<_>>();
        for id in ids {
            let _ = self.stop(&id);
        }
    }

    fn refresh_one(&mut self, module_id: &str) -> Option<ModuleRuntimeSnapshot> {
        let outcome = {
            let running = self.running.get_mut(module_id)?;
            let mut handshake_error = None;

            while handshake_error.is_none() {
                match running.messages.try_recv() {
                    Ok(RuntimeMessage::Frame(frame)) if !running.handshake_complete => {
                        if frame.message_type != "module.hello.response" {
                            handshake_error = Some(format!(
                                "模块在 hello 完成前发送了意外消息：{}",
                                frame.message_type
                            ));
                        } else if frame.request_id != running.hello_request_id {
                            handshake_error =
                                Some("模块 hello 响应的 requestId 不匹配。".to_string());
                        } else if let Some(error) = frame.error.as_ref() {
                            handshake_error = Some(format!(
                                "模块拒绝 hello：{} ({})",
                                error.message, error.code
                            ));
                        } else if let Err(error) =
                            validate_hello_payload(&frame.payload, module_id, &running.nonce)
                        {
                            handshake_error = Some(error);
                        } else {
                            running.handshake_complete = true;
                        }
                    }
                    Ok(RuntimeMessage::Frame(_)) => {
                        // Module events are intentionally not interpreted by the
                        // host yet. They remain on the process boundary and can
                        // be routed to the UI once an operation contract exists.
                    }
                    Ok(RuntimeMessage::Invalid(error)) | Ok(RuntimeMessage::Io(error)) => {
                        handshake_error = Some(error);
                    }
                    Err(TryRecvError::Empty) => break,
                    Err(TryRecvError::Disconnected) if !running.handshake_complete => {
                        handshake_error = Some("模块 stdout 在 hello 完成前断开。".to_string());
                    }
                    Err(TryRecvError::Disconnected) => break,
                }
            }

            let status = running.child.try_wait();
            let generation = running.generation;
            if let Some(error) = handshake_error {
                RefreshOutcome::Failed { generation, error }
            } else {
                match status {
                    Ok(Some(status)) if running.handshake_complete && status.success() => {
                        RefreshOutcome::Exited {
                            generation,
                            status,
                            handshaken: true,
                        }
                    }
                    Ok(Some(status)) => RefreshOutcome::Exited {
                        generation,
                        status,
                        handshaken: false,
                    },
                    Err(error) => RefreshOutcome::Failed {
                        generation,
                        error: format!("无法读取模块进程状态：{error}"),
                    },
                    Ok(None)
                        if !running.handshake_complete
                            && Instant::now() >= running.handshake_deadline =>
                    {
                        RefreshOutcome::Failed {
                            generation,
                            error: "模块 hello 握手超时。".to_string(),
                        }
                    }
                    Ok(None) => RefreshOutcome::Active {
                        generation,
                        handshaken: running.handshake_complete,
                    },
                }
            }
        };

        let snapshot = match outcome {
            RefreshOutcome::Active {
                generation,
                handshaken,
            } => ModuleRuntimeSnapshot {
                module_id: module_id.to_string(),
                state: if handshaken {
                    ModuleRuntimeState::Running
                } else {
                    ModuleRuntimeState::Starting
                },
                generation,
                last_error: None,
            },
            RefreshOutcome::Exited {
                generation,
                status,
                handshaken,
            } => {
                self.running.remove(module_id);
                ModuleRuntimeSnapshot {
                    module_id: module_id.to_string(),
                    state: if handshaken && status.success() {
                        ModuleRuntimeState::Stopped
                    } else {
                        ModuleRuntimeState::Failed
                    },
                    generation,
                    last_error: if handshaken && status.success() {
                        None
                    } else if handshaken {
                        Some(format!("模块进程已退出：{status}"))
                    } else {
                        Some(format!("模块进程在 hello 握手完成前退出：{status}"))
                    },
                }
            }
            RefreshOutcome::Failed { generation, error } => {
                if let Some(mut running) = self.running.remove(module_id) {
                    terminate_child(&mut running.child);
                }
                ModuleRuntimeSnapshot {
                    module_id: module_id.to_string(),
                    state: ModuleRuntimeState::Failed,
                    generation,
                    last_error: Some(error),
                }
            }
        };
        self.snapshots
            .insert(module_id.to_string(), snapshot.clone());
        Some(snapshot)
    }

    fn fail_running(&mut self, module_id: &str, error: String) {
        let generation = self
            .running
            .remove(module_id)
            .map(|mut running| {
                let generation = running.generation;
                terminate_child(&mut running.child);
                generation
            })
            .or_else(|| {
                self.snapshots
                    .get(module_id)
                    .map(|snapshot| snapshot.generation)
            })
            .unwrap_or_default();
        self.snapshots.insert(
            module_id.to_string(),
            ModuleRuntimeSnapshot {
                module_id: module_id.to_string(),
                state: ModuleRuntimeState::Failed,
                generation,
                last_error: Some(error),
            },
        );
    }
}

enum RefreshOutcome {
    Active {
        generation: u64,
        handshaken: bool,
    },
    Exited {
        generation: u64,
        status: ExitStatus,
        handshaken: bool,
    },
    Failed {
        generation: u64,
        error: String,
    },
}

fn validate_hello_payload(
    payload: &Value,
    module_id: &str,
    expected_nonce: &str,
) -> Result<(), String> {
    let object = payload
        .as_object()
        .ok_or_else(|| "模块 hello 响应 payload 必须是对象。".to_string())?;
    let actual_module_id = object
        .get("moduleId")
        .and_then(Value::as_str)
        .ok_or_else(|| "模块 hello 响应缺少 moduleId。".to_string())?;
    if actual_module_id != module_id {
        return Err("模块 hello 响应的 moduleId 不匹配。".to_string());
    }
    let actual_nonce = object
        .get("nonce")
        .and_then(Value::as_str)
        .ok_or_else(|| "模块 hello 响应缺少 nonce。".to_string())?;
    if actual_nonce != expected_nonce {
        return Err("模块 hello 响应的 nonce 不匹配。".to_string());
    }
    Ok(())
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

impl Drop for ModuleRuntimeManager {
    fn drop(&mut self) {
        self.stop_all();
    }
}

fn is_supported_entry(path: &std::path::Path) -> bool {
    if !path.is_file() {
        return false;
    }
    if cfg!(windows) {
        path.extension()
            .and_then(|extension| extension.to_str())
            .is_some_and(|extension| extension.eq_ignore_ascii_case("exe"))
    } else {
        true
    }
}

fn entry_still_belongs_to_module(record: &ModuleRecord) -> bool {
    let directory = match fs::canonicalize(&record.directory) {
        Ok(directory) => directory,
        Err(_) => return false,
    };
    let entry = match fs::canonicalize(&record.entry) {
        Ok(entry) => entry,
        Err(_) => return false,
    };
    entry.is_file() && entry.starts_with(directory)
}

fn write_frame<T: Serialize>(
    stdin: &mut ChildStdin,
    envelope: &ProtocolEnvelope<T>,
) -> Result<(), RuntimeError> {
    let mut bytes = serde_json::to_vec(envelope).map_err(|error| RuntimeError {
        code: "frameSerializeFailed",
        message: format!("无法序列化模块协议帧：{error}"),
    })?;
    if bytes.len() > MAX_FRAME_BYTES {
        return Err(RuntimeError {
            code: "frameTooLarge",
            message: "模块协议帧超过 1 MiB 限制。".to_string(),
        });
    }
    bytes.push(b'\n');
    stdin.write_all(&bytes).map_err(|error| RuntimeError {
        code: "frameWriteFailed",
        message: format!("无法写入模块协议帧：{error}"),
    })?;
    stdin.flush().map_err(|error| RuntimeError {
        code: "frameWriteFailed",
        message: format!("无法刷新模块协议帧：{error}"),
    })
}

fn spawn_stdout_reader(stdout: ChildStdout) -> Receiver<RuntimeMessage> {
    let (sender, receiver) = mpsc::channel();
    thread::spawn(move || {
        let mut reader = BufReader::new(stdout);
        let mut line = Vec::new();
        loop {
            line.clear();
            match reader.read_until(b'\n', &mut line) {
                Ok(0) => break,
                Ok(_) => match decode_line::<Value>(&line) {
                    Ok(frame) => {
                        if sender.send(RuntimeMessage::Frame(frame)).is_err() {
                            break;
                        }
                    }
                    Err(error) => {
                        let message = format!("模块协议帧无效：{}", error.message);
                        let _ = sender.send(RuntimeMessage::Invalid(message));
                        break;
                    }
                },
                Err(error) => {
                    let _ =
                        sender.send(RuntimeMessage::Io(format!("无法读取模块 stdout：{error}")));
                    break;
                }
            }
        }
    });
    receiver
}

fn drain_stderr(pipe: Option<impl std::io::Read + Send + 'static>) {
    if let Some(pipe) = pipe {
        thread::spawn(move || {
            let mut reader = BufReader::new(pipe);
            let mut line = String::new();
            while reader.read_line(&mut line).is_ok() {
                if line.is_empty() {
                    break;
                }
                line.clear();
            }
        });
    }
}

#[cfg(windows)]
fn apply_hidden_process_flags(command: &mut Command) {
    use std::os::windows::process::CommandExt;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    command.creation_flags(CREATE_NO_WINDOW);
}

#[cfg(not(windows))]
fn apply_hidden_process_flags(_command: &mut Command) {}

/// Generate a nonce from the operating system RNG. A timestamp/process id is
/// useful for diagnostics but is predictable; the handshake binding must not
/// be reusable by a stale or unrelated child process.
fn new_nonce() -> Result<String, String> {
    let mut bytes = [0_u8; 32];
    getrandom::fill(&mut bytes).map_err(|error| format!("无法生成模块握手 nonce：{error}"))?;
    let mut encoded = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        use std::fmt::Write as _;
        let _ = write!(&mut encoded, "{byte:02x}");
    }
    Ok(encoded)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::paths::ModuleSource;
    use std::{collections::BTreeSet, env, path::PathBuf};

    #[test]
    fn unsupported_entries_are_rejected_before_spawn() {
        let path = std::path::Path::new("module.dll");
        assert!(!is_supported_entry(path));
    }

    #[test]
    fn unknown_module_starts_as_not_started() {
        let mut manager = ModuleRuntimeManager::new();
        let snapshot = manager.snapshot("missing.module");
        assert_eq!(snapshot.state, ModuleRuntimeState::NotStarted);
    }

    #[test]
    fn hello_payload_requires_the_backend_nonce_and_module_id() {
        let payload = serde_json::json!({
            "moduleId": "demo.module",
            "nonce": "nonce-1"
        });
        validate_hello_payload(&payload, "demo.module", "nonce-1").expect("valid hello");

        let wrong_nonce = serde_json::json!({
            "moduleId": "demo.module",
            "nonce": "other"
        });
        assert!(validate_hello_payload(&wrong_nonce, "demo.module", "nonce-1").is_err());
    }

    #[test]
    fn malformed_hello_payload_is_rejected() {
        let payload = serde_json::json!({ "moduleId": "demo.module" });
        let error = validate_hello_payload(&payload, "demo.module", "nonce-1")
            .expect_err("nonce is required");
        assert!(error.contains("nonce"));
    }

    #[test]
    fn nonce_is_random_length_and_token_safe() {
        let first = new_nonce().expect("OS RNG should be available");
        let second = new_nonce().expect("OS RNG should be available");
        assert_eq!(first.len(), 64);
        assert!(first.bytes().all(|byte| byte.is_ascii_hexdigit()));
        assert_ne!(first, second);
    }

    #[test]
    fn invoke_round_trip_with_canary_when_verification_provides_one() {
        let Some(path) = env::var_os("QING_TAURI_CANARY_PATH") else {
            return;
        };
        let entry = PathBuf::from(path);
        if !entry.is_file() {
            return;
        }
        let directory = entry.parent().expect("canary directory").to_path_buf();
        let record = ModuleRecord {
            name: "Canary".to_string(),
            version: "0.1.0".to_string(),
            icon_data_url: None,
            directory,
            entry,
            web_entry: None,
            operations: BTreeSet::from(["ping".to_string()]),
            source: ModuleSource::Bundled,
        };
        let mut manager = ModuleRuntimeManager::new();
        let response = manager
            .invoke(
                "qing.canary",
                &record,
                "ping",
                serde_json::json!({ "source": "runtime-test" }),
            )
            .expect("canary invoke response");
        assert_eq!(response["pong"], true);
        assert_eq!(response["echo"]["source"], "runtime-test");
        assert_eq!(
            manager.stop("qing.canary").state,
            ModuleRuntimeState::Stopped
        );
    }
}
