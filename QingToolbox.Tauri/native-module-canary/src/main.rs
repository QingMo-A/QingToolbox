use std::io::{self, BufRead, Write};

use serde::{Deserialize, Serialize};
use serde_json::Value;

const PROTOCOL_VERSION: u16 = 1;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Envelope {
    protocol_version: u16,
    message_type: String,
    request_id: String,
    payload: Value,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct Response<'a> {
    protocol_version: u16,
    message_type: &'a str,
    request_id: &'a str,
    payload: Value,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<ErrorBody>,
}

#[derive(Debug, Serialize)]
struct ErrorBody {
    code: &'static str,
    message: &'static str,
}

fn main() {
    let stdin = io::stdin();
    let mut stdout = io::BufWriter::new(io::stdout());
    for line in stdin.lock().lines() {
        let line = match line {
            Ok(line) if !line.trim().is_empty() => line,
            Ok(_) => continue,
            Err(_) => break,
        };
        let envelope: Envelope = match serde_json::from_str(&line) {
            Ok(envelope) => envelope,
            Err(_) => break,
        };
        if envelope.protocol_version != PROTOCOL_VERSION {
            break;
        }

        match envelope.message_type.as_str() {
            "module.hello.request" => {
                let Some(object) = envelope.payload.as_object() else {
                    break;
                };
                let Some(module_id) = object.get("moduleId").and_then(Value::as_str) else {
                    break;
                };
                let Some(nonce) = object.get("nonce").and_then(Value::as_str) else {
                    break;
                };
                let response = Response {
                    protocol_version: PROTOCOL_VERSION,
                    message_type: "module.hello.response",
                    request_id: &envelope.request_id,
                    payload: serde_json::json!({
                        "moduleId": module_id,
                        "nonce": nonce,
                        "version": env!("CARGO_PKG_VERSION"),
                    }),
                    error: None,
                };
                write_response(&mut stdout, &response);
            }
            "module.shutdown.request" => {
                let response = Response {
                    protocol_version: PROTOCOL_VERSION,
                    message_type: "module.shutdown.response",
                    request_id: &envelope.request_id,
                    payload: serde_json::json!({ "ok": true }),
                    error: None,
                };
                write_response(&mut stdout, &response);
                break;
            }
            "module.invoke.request" => {
                let Some(object) = envelope.payload.as_object() else {
                    write_error(&mut stdout, &envelope.request_id, "invalid_payload", "Invoke payload must be an object.");
                    continue;
                };
                let Some(method) = object.get("method").and_then(Value::as_str) else {
                    write_error(&mut stdout, &envelope.request_id, "invalid_method", "Invoke method is required.");
                    continue;
                };
                if method == "ping" {
                    let payload = object.get("payload").cloned().unwrap_or(Value::Null);
                    let response = Response {
                        protocol_version: PROTOCOL_VERSION,
                        message_type: "module.invoke.response",
                        request_id: &envelope.request_id,
                        payload: serde_json::json!({ "pong": true, "echo": payload }),
                        error: None,
                    };
                    write_response(&mut stdout, &response);
                } else {
                    write_error(&mut stdout, &envelope.request_id, "unknown_method", "The canary does not implement this operation.");
                }
            }
            _ => {
                let response = Response {
                    protocol_version: PROTOCOL_VERSION,
                    message_type: "module.error.response",
                    request_id: &envelope.request_id,
                    payload: Value::Null,
                    error: Some(ErrorBody {
                        code: "unknown_message",
                        message: "The canary does not implement this message.",
                    }),
                };
                write_response(&mut stdout, &response);
            }
        }
    }
}

fn write_error(stdout: &mut impl Write, request_id: &str, code: &'static str, message: &'static str) {
    let response = Response {
        protocol_version: PROTOCOL_VERSION,
        message_type: "module.invoke.response",
        request_id,
        payload: Value::Null,
        error: Some(ErrorBody { code, message }),
    };
    write_response(stdout, &response);
}

fn write_response(stdout: &mut impl Write, response: &Response<'_>) {
    if serde_json::to_writer(&mut *stdout, response).is_ok() {
        let _ = stdout.write_all(b"\n");
        let _ = stdout.flush();
    }
}
