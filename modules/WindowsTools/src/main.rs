#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
mod association;
mod associations;
mod platform;

use serde::Deserialize;
use serde_json::{json, Value};
use std::{
    io::{self, BufRead, BufReader, Write},
    sync::{Arc, Mutex},
    thread,
};

const ID: &str = "qing.windowstools";
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Envelope {
    protocol_version: u16,
    message_type: String,
    request_id: String,
    payload: Value,
}
#[derive(Default)]
struct Operation {
    running: bool,
    action: String,
    error: Option<String>,
    revision: u64,
}
type Shared = Arc<Mutex<Operation>>;
fn lock(op: &Shared) -> std::sync::MutexGuard<'_, Operation> {
    op.lock().unwrap_or_else(|e| e.into_inner())
}
fn operation(op: &Shared) -> Value {
    let op = lock(op);
    json!({ "running": op.running, "action": op.action, "error": op.error, "revision": op.revision })
}
fn audit(action: &str, outcome: &str) {
    use std::fs::OpenOptions;
    let Some(dir) = std::env::var_os("QINGTOOLBOX_MODULE_DATA_DIR").filter(|v| !v.is_empty())
    else {
        return;
    };
    if std::fs::create_dir_all(&dir).is_err() {
        return;
    }
    let path = std::path::PathBuf::from(dir).join("operations.jsonl");
    if std::fs::metadata(&path).is_ok_and(|m| m.len() > 1024 * 1024) {
        let _ = std::fs::rename(&path, path.with_extension("previous.jsonl"));
    }
    if let Ok(mut file) = OpenOptions::new().append(true).create(true).open(path) {
        let timestamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_secs();
        let _ = writeln!(
            file,
            "{}",
            json!({"timestamp":timestamp,"action":action,"outcome":outcome})
        );
    }
}
fn text<'a>(value: &'a Value, name: &str) -> Result<&'a str, String> {
    value
        .get(name)
        .and_then(Value::as_str)
        .ok_or_else(|| format!("缺少 {name}。"))
}
fn invoke(
    method: &str,
    payload: &Value,
    op: &Shared,
    table: &associations::Manager,
) -> Result<Value, String> {
    match method {
        "getAssociationTable" => Ok(table.snapshot()),
        "addAssociationRows"
        | "addCommonAssociations"
        | "removeAssociationRows"
        | "setAssociationTarget"
        | "setAssociationPreferences"
        | "checkAssociations"
        | "registerAssociationBatch" => table.invoke(method, payload),
        "getState" => {
            let mut state = platform::snapshot()?;
            state["operation"] = operation(op);
            Ok(state)
        }
        "getOperation" => Ok(operation(op)),
        "beginAction" => {
            let action = text(payload, "action")?.to_string();
            if !platform::valid_action(&action) {
                return Err("不支持的系统操作。".into());
            }
            if payload.get("confirmed").and_then(Value::as_bool) != Some(true) {
                return Err("请先确认修改及其影响。".into());
            }
            {
                let mut state = lock(op);
                if state.running {
                    return Err("已有系统操作正在进行。".into());
                }
                state.running = true;
                state.action = action.clone();
                state.error = None;
            }
            audit(&action, "requested");
            let shared = op.clone();
            thread::spawn(move || {
                let result = platform::apply(&action);
                audit(
                    &action,
                    result
                        .as_ref()
                        .err()
                        .map(String::as_str)
                        .unwrap_or("completed"),
                );
                let mut state = lock(&shared);
                state.running = false;
                state.error = result.err();
                state.revision += 1;
            });
            Ok(operation(op))
        }
        "getAssociation" => association::inspect(text(payload, "extension")?),
        "registerAssociation" => {
            association::register(text(payload, "extension")?, text(payload, "executable")?)
        }
        "openSettings" => {
            platform::open_settings(text(payload, "page")?)?;
            Ok(json!({}))
        }
        _ => Err("未知的 Windows 设置操作。".into()),
    }
}
fn reply(out: &mut impl Write, kind: &str, request: &str, result: Result<Value, String>) {
    let (payload, error) = match result {
        Ok(v) => (v, Value::Null),
        Err(message) => (
            json!({}),
            json!({"code":"windows_tools_error","message":message}),
        ),
    };
    let mut response =
        json!({"protocolVersion":1,"messageType":kind,"requestId":request,"payload":payload});
    if !error.is_null() {
        response["error"] = error
    }
    let _ = serde_json::to_writer(&mut *out, &response);
    let _ = writeln!(out);
    let _ = out.flush();
}
fn main() {
    let nonce = std::env::var("QINGTOOLBOX_MODULE_NONCE").unwrap_or_default();
    let op = Shared::default();
    let data = std::env::var_os("QINGTOOLBOX_MODULE_DATA_DIR")
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|| std::env::temp_dir().join("QingToolbox/qing.windowstools"));
    let table = associations::Manager::new(data);
    let mut hello = false;
    let mut reader = BufReader::new(io::stdin());
    let mut out = io::BufWriter::new(io::stdout());
    loop {
        let mut line = Vec::new();
        // Bound allocation even for a missing newline in an invalid host frame.
        let read = std::io::Read::take(&mut reader, 1_048_577).read_until(b'\n', &mut line);
        if !matches!(read, Ok(n) if n > 0) || line.len() > 1_048_576 {
            break;
        }
        let req: Envelope = match serde_json::from_slice(&line) {
            Ok(v) => v,
            Err(_) => break,
        };
        if req.protocol_version != 1 || req.request_id.is_empty() || req.request_id.len() > 128 {
            break;
        }
        match req.message_type.as_str() {
            "module.hello.request" if !hello => {
                if nonce.is_empty()
                    || req.payload["moduleId"] != ID
                    || req.payload["nonce"] != nonce
                {
                    break;
                }
                hello = true;
                reply(
                    &mut out,
                    "module.hello.response",
                    &req.request_id,
                    Ok(
                        json!({"moduleId":ID,"nonce":nonce,"lifecycleVersion":1,"version":env!("CARGO_PKG_VERSION")}),
                    ),
                );
            }
            "module.lifecycle.request" if hello => {
                let Some(active) = req.payload["active"].as_bool() else {
                    break;
                };
                // No background tweaks or reapplication on enable/disable.
                reply(
                    &mut out,
                    "module.lifecycle.response",
                    &req.request_id,
                    Ok(json!({"active":active})),
                );
            }
            "module.invoke.request" if hello => {
                let method = req.payload["method"].as_str().unwrap_or_default();
                reply(
                    &mut out,
                    "module.invoke.response",
                    &req.request_id,
                    invoke(method, &req.payload["payload"], &op, &table),
                );
            }
            "module.shutdown.request" if hello => {
                reply(
                    &mut out,
                    "module.shutdown.response",
                    &req.request_id,
                    Ok(json!({})),
                );
                break;
            }
            _ => {
                reply(
                    &mut out,
                    "module.protocol.response",
                    &req.request_id,
                    Err("模块请求顺序无效。".into()),
                );
            }
        }
    }
}
