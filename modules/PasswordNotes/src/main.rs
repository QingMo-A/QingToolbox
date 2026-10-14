#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
mod app;
mod model;
mod platform;
mod store;
mod totp;

use model::{Error, Result};
use serde::Deserialize;
use serde_json::{json, Value};
use std::{
    env,
    io::{self, BufRead, Read, Write},
    path::PathBuf,
};
use zeroize::Zeroizing;
const MAX_FRAME: usize = 1024 * 1024;
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Envelope {
    protocol_version: u16,
    message_type: String,
    request_id: String,
    payload: Value,
}
fn token(v: &str, max: usize) -> bool {
    !v.is_empty()
        && v.len() <= max
        && v.bytes()
            .all(|b| b.is_ascii_alphanumeric() || b".-_:".contains(&b))
}
fn reply(writer: &mut impl Write, kind: &str, id: &str, result: Result<Value>) -> io::Result<()> {
    let mut value = match result {
        Ok(payload) => {
            json!({"protocolVersion":1,"messageType":kind,"requestId":id,"payload":payload})
        }
        Err(error) => {
            json!({"protocolVersion":1,"messageType":kind,"requestId":id,"payload":null,"error":error})
        }
    };
    let encoded = app::bytes(&value).map_err(|_| io::Error::from(io::ErrorKind::InvalidData))?;
    app::wipe(&mut value);
    if encoded.len() > MAX_FRAME {
        return Err(io::Error::from(io::ErrorKind::InvalidData));
    }
    writer.write_all(&encoded)?;
    writer.write_all(b"\n")?;
    writer.flush()
}
fn main() {
    let id = env::var("QINGTOOLBOX_MODULE_ID").unwrap_or_default();
    let nonce = env::var("QINGTOOLBOX_MODULE_NONCE").unwrap_or_default();
    if id != "qing.passwordnotes" || !token(&nonce, 128) {
        return;
    }
    let data = env::var_os("QINGTOOLBOX_MODULE_DATA_DIR").map(PathBuf::from);
    let mut app = app::App::new(data);
    let mut reader = io::stdin().lock();
    let mut writer = io::stdout().lock();
    let mut ready = false;
    loop {
        let mut line = Zeroizing::new(Vec::new());
        match Read::by_ref(&mut reader)
            .take(MAX_FRAME as u64 + 1)
            .read_until(b'\n', &mut line)
        {
            Ok(0) | Err(_) => break,
            Ok(_) if line.len() > MAX_FRAME || line.last() != Some(&b'\n') => break,
            _ => (),
        }
        let mut frame: Envelope = match serde_json::from_slice(&line) {
            Ok(v) => v,
            Err(_) => break,
        };
        if frame.protocol_version != 1
            || !token(&frame.message_type, 64)
            || !token(&frame.request_id, 128)
        {
            app::wipe(&mut frame.payload);
            break;
        }
        let mut shutdown = false;
        let (kind, result) = match frame.message_type.as_str() {
            "module.hello.request" if !ready => {
                if frame.payload.get("moduleId").and_then(Value::as_str) != Some(id.as_str())
                    || frame.payload.get("nonce").and_then(Value::as_str) != Some(nonce.as_str())
                {
                    shutdown = true;
                    (
                        "module.hello.response",
                        Err(Error::new("helloInvalid", "模块身份校验失败。")),
                    )
                } else {
                    ready = true;
                    (
                        "module.hello.response",
                        Ok(
                            json!({"moduleId":id,"nonce":nonce,"version":env!("CARGO_PKG_VERSION"),"lifecycleVersion":1}),
                        ),
                    )
                }
            }
            "module.lifecycle.request" if ready => (
                "module.lifecycle.response",
                frame
                    .payload
                    .get("active")
                    .and_then(Value::as_bool)
                    .map(|active| json!({"active":active}))
                    .ok_or_else(|| Error::new("invalidPayload", "缺少启停状态。")),
            ),
            "module.invoke.request" if ready => (
                "module.invoke.response",
                frame
                    .payload
                    .get("method")
                    .and_then(Value::as_str)
                    .ok_or_else(|| Error::new("invalidPayload", "缺少操作名称。"))
                    .and_then(|method| {
                        app.invoke(method, frame.payload.get("payload").unwrap_or(&Value::Null))
                    }),
            ),
            "module.shutdown.request" if ready => {
                shutdown = true;
                ("module.shutdown.response", Ok(json!({"ok":true})))
            }
            _ => (
                "module.error.response",
                Err(Error::new("unknownMessage", "消息类型或顺序无效。")),
            ),
        };
        app::wipe(&mut frame.payload);
        if reply(&mut writer, kind, &frame.request_id, result).is_err() || shutdown {
            break;
        }
    }
}
