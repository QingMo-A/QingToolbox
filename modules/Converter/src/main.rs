#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
mod app;
mod engines;
mod model;

use model::ConvertError;
use serde::Deserialize;
use serde_json::{json, Value};
use std::{
    env,
    io::{self, BufRead, Read, Write},
    path::PathBuf,
};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Envelope {
    protocol_version: u16,
    message_type: String,
    request_id: String,
    payload: Value,
}
fn token(value: &str, max: usize) -> bool {
    !value.is_empty()
        && value.len() <= max
        && value
            .bytes()
            .all(|v| v.is_ascii_alphanumeric() || b".-_:".contains(&v))
}
fn reply(writer: &mut impl Write, kind: &str, id: &str, result: Result<Value, ConvertError>) {
    let frame = match result {
        Ok(payload) => {
            json!({"protocolVersion":1,"messageType":kind,"requestId":id,"payload":payload})
        }
        Err(error) => {
            json!({"protocolVersion":1,"messageType":kind,"requestId":id,"payload":null,"error":error})
        }
    };
    let _ = serde_json::to_writer(&mut *writer, &frame);
    let _ = writer.write_all(b"\n");
    let _ = writer.flush();
}
fn main() {
    let args: Vec<_> = env::args_os().collect();
    if args.len() == 3 && args[1] == "--convert-task" {
        let result = engines::run_task_file(&PathBuf::from(&args[2]));
        match result {
            Ok(note) => println!("{}", json!({"done":true,"note":note})),
            Err(error) => {
                println!("{}", json!({"error":error}));
                std::process::exit(1);
            }
        }
        return;
    }
    let id = env::var("QINGTOOLBOX_MODULE_ID").unwrap_or_default();
    let nonce = env::var("QINGTOOLBOX_MODULE_NONCE").unwrap_or_default();
    if id != "qing.convert" || nonce.is_empty() {
        eprintln!("[convert] host identity is missing");
        return;
    }
    let module = env::var_os("QINGTOOLBOX_MODULE_DIRECTORY")
        .map(PathBuf::from)
        .unwrap_or_else(|| env::current_dir().unwrap_or_default());
    let data = env::var_os("QINGTOOLBOX_MODULE_DATA_DIR").map(PathBuf::from);
    let mut app = app::App::new(module, data);
    let mut reader = io::stdin().lock();
    let mut writer = io::BufWriter::new(io::stdout());
    let mut ready = false;
    loop {
        let mut line = Vec::new();
        match Read::by_ref(&mut reader)
            .take(1024 * 1024 + 1)
            .read_until(b'\n', &mut line)
        {
            Ok(0) | Err(_) => break,
            Ok(_) if line.len() > 1024 * 1024 || line.last() != Some(&b'\n') => break,
            Ok(_) => (),
        }
        let frame: Envelope = match serde_json::from_slice(&line) {
            Ok(frame) => frame,
            Err(_) => break,
        };
        if frame.protocol_version != 1
            || !token(&frame.message_type, 64)
            || !token(&frame.request_id, 128)
        {
            break;
        }
        match frame.message_type.as_str() {
            "module.hello.request" if !ready => {
                if frame.payload.get("moduleId").and_then(Value::as_str) != Some(id.as_str())
                    || frame.payload.get("nonce").and_then(Value::as_str) != Some(nonce.as_str())
                {
                    reply(
                        &mut writer,
                        "module.hello.response",
                        &frame.request_id,
                        Err(ConvertError::new("helloInvalid", "模块身份校验失败。")),
                    );
                    break;
                }
                ready = true;
                reply(
                    &mut writer,
                    "module.hello.response",
                    &frame.request_id,
                    Ok(
                        json!({"moduleId":id,"nonce":nonce,"version":env!("CARGO_PKG_VERSION"),"lifecycleVersion":1}),
                    ),
                );
            }
            "module.lifecycle.request" if ready => {
                if let Some(active) = frame.payload.get("active").and_then(Value::as_bool) {
                    reply(
                        &mut writer,
                        "module.lifecycle.response",
                        &frame.request_id,
                        Ok(json!({"active":active})),
                    );
                } else {
                    reply(
                        &mut writer,
                        "module.lifecycle.response",
                        &frame.request_id,
                        Err(ConvertError::new("invalidPayload", "缺少启停状态。")),
                    );
                }
            }
            "module.invoke.request" if ready => {
                let result =
                    if let Some(method) = frame.payload.get("method").and_then(Value::as_str) {
                        app.invoke(
                            method,
                            frame
                                .payload
                                .get("payload")
                                .cloned()
                                .unwrap_or_else(|| json!({})),
                        )
                    } else {
                        Err(ConvertError::new("invalidPayload", "缺少操作名称。"))
                    };
                reply(
                    &mut writer,
                    "module.invoke.response",
                    &frame.request_id,
                    result,
                );
            }
            "module.shutdown.request" if ready => {
                app.shutdown();
                reply(
                    &mut writer,
                    "module.shutdown.response",
                    &frame.request_id,
                    Ok(json!({"ok":true})),
                );
                break;
            }
            _ => reply(
                &mut writer,
                "module.error.response",
                &frame.request_id,
                Err(ConvertError::new("unknownMessage", "不支持此消息类型。")),
            ),
        }
    }
    app.shutdown();
}
