#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

//! Native Text Tools module.
//!
//! The WebView only edits a bounded text snapshot. All transformations and
//! clipboard writes happen in this process, keeping the host boundary JSON
//! only and independent of the legacy WPF assembly.

use std::{
    io::{self, BufRead, BufReader, BufWriter, Write},
    sync::{Arc, Mutex},
};

use base64::{engine::general_purpose::STANDARD as BASE64, Engine as _};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

const HOST_PROTOCOL_VERSION: u16 = 1;
const HOST_MAX_FRAME_BYTES: usize = 1024 * 1024;
const MAX_TEXT_BYTES: usize = 2 * 1024 * 1024;
const MAX_OUTPUT_BYTES: usize = 4 * 1024 * 1024;

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
struct TextError {
    code: &'static str,
    message: String,
}

impl TextError {
    fn new(code: &'static str, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }
}

#[derive(Debug, Default)]
struct TextState {
    input: String,
    output: String,
    status: String,
    error: Option<String>,
}

#[derive(Debug, Clone)]
struct TextApp {
    state: Arc<Mutex<TextState>>,
}

impl TextApp {
    fn new() -> Self {
        Self {
            state: Arc::new(Mutex::new(TextState {
                status: "ready".to_string(),
                ..TextState::default()
            })),
        }
    }

    fn snapshot(&self) -> Value {
        let state = self
            .state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        json!({
            "input": state.input,
            "output": state.output,
            "status": state.status,
            "error": state.error,
        })
    }

    fn invoke(&self, method: &str, payload: &Value) -> Result<Value, TextError> {
        match method {
            "getState" => Ok(self.snapshot()),
            "setInput" => {
                let text = required_text(payload, "text")?;
                ensure_text_size(&text)?;
                let mut state = self
                    .state
                    .lock()
                    .unwrap_or_else(|poisoned| poisoned.into_inner());
                state.input = text;
                state.status = "ready".to_string();
                state.error = None;
                Ok(json!({
                    "input": state.input,
                    "output": state.output,
                    "status": state.status,
                    "error": state.error,
                }))
            }
            "formatJson" => self.transform(|value| {
                let parsed: Value = serde_json::from_str(value)
                    .map_err(|_| TextError::new("invalid_json", "输入不是有效的 JSON。"))?;
                serde_json::to_string_pretty(&parsed)
                    .map_err(|_| TextError::new("operation_failed", "无法格式化 JSON。"))
            }),
            "minifyJson" => self.transform(|value| {
                let parsed: Value = serde_json::from_str(value)
                    .map_err(|_| TextError::new("invalid_json", "输入不是有效的 JSON。"))?;
                serde_json::to_string(&parsed)
                    .map_err(|_| TextError::new("operation_failed", "无法压缩 JSON。"))
            }),
            "base64Encode" => self.transform(|value| Ok(BASE64.encode(value.as_bytes()))),
            "base64Decode" => self.transform(|value| {
                let bytes = BASE64
                    .decode(value.trim())
                    .map_err(|_| TextError::new("invalid_base64", "无效的 Base64 输入。"))?;
                String::from_utf8(bytes).map_err(|_| {
                    TextError::new("invalid_base64", "Base64 内容不是有效的 UTF-8 文本。")
                })
            }),
            "urlEncode" => self.transform(|value| Ok(percent_encode(value))),
            "urlDecode" => self.transform(percent_decode),
            "uppercase" => self.transform(|value| Ok(value.to_uppercase())),
            "lowercase" => self.transform(|value| Ok(value.to_lowercase())),
            "removeEmptyLines" => self.transform(|value| {
                Ok(value
                    .lines()
                    .filter(|line| !line.trim().is_empty())
                    .collect::<Vec<_>>()
                    .join("\n"))
            }),
            "copyOutput" => {
                let output = self.current_output();
                if output.is_empty() {
                    return Err(TextError::new("input_empty", "输出为空。"));
                }
                copy_text_to_clipboard(&output).map_err(|error| {
                    TextError::new("copy_failed", format!("无法复制结果：{error}"))
                })?;
                self.set_status("copied", None);
                Ok(self.snapshot())
            }
            "copyOutputToInput" => {
                let output = self.current_output();
                ensure_text_size(&output)?;
                let mut state = self
                    .state
                    .lock()
                    .unwrap_or_else(|poisoned| poisoned.into_inner());
                state.input = output;
                state.status = "moved".to_string();
                state.error = None;
                Ok(json!({
                    "input": state.input,
                    "output": state.output,
                    "status": state.status,
                    "error": state.error,
                }))
            }
            "swap" => {
                let mut state = self
                    .state
                    .lock()
                    .unwrap_or_else(|poisoned| poisoned.into_inner());
                let old_input = state.input.clone();
                state.input = state.output.clone();
                state.output = old_input;
                ensure_text_size(&state.input)?;
                ensure_output_size(&state.output)?;
                state.status = "swapped".to_string();
                state.error = None;
                Ok(json!({
                    "input": state.input,
                    "output": state.output,
                    "status": state.status,
                    "error": state.error,
                }))
            }
            "clear" => {
                let mut state = self
                    .state
                    .lock()
                    .unwrap_or_else(|poisoned| poisoned.into_inner());
                state.input.clear();
                state.output.clear();
                state.status = "ready".to_string();
                state.error = None;
                Ok(json!({
                    "input": state.input,
                    "output": state.output,
                    "status": state.status,
                    "error": state.error,
                }))
            }
            _ => Err(TextError::new("unknown_method", "未知的文本工具操作。")),
        }
    }

    fn transform<F>(&self, operation: F) -> Result<Value, TextError>
    where
        F: FnOnce(&str) -> Result<String, TextError>,
    {
        let input = {
            let state = self
                .state
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner());
            state.input.clone()
        };
        if input.is_empty() {
            self.set_status("input_empty", None);
            return Err(TextError::new("input_empty", "输入为空。"));
        }
        ensure_text_size(&input)?;
        let output = operation(&input)?;
        ensure_output_size(&output)?;
        let mut state = self
            .state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        state.output = output;
        state.status = "done".to_string();
        state.error = None;
        Ok(json!({
            "input": state.input,
            "output": state.output,
            "status": state.status,
            "error": state.error,
        }))
    }

    fn current_output(&self) -> String {
        self.state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .output
            .clone()
    }

    fn set_status(&self, status: &str, error: Option<String>) {
        let mut state = self
            .state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        state.status = status.to_string();
        state.error = error;
    }
}

fn required_text(payload: &Value, key: &str) -> Result<String, TextError> {
    payload
        .get(key)
        .and_then(Value::as_str)
        .map(str::to_string)
        .ok_or_else(|| TextError::new("invalid_payload", format!("{key} 必须是字符串。")))
}

fn ensure_text_size(value: &str) -> Result<(), TextError> {
    if value.len() > MAX_TEXT_BYTES {
        Err(TextError::new(
            "text_too_large",
            "输入文本过大（上限 2 MiB）。",
        ))
    } else {
        Ok(())
    }
}

fn ensure_output_size(value: &str) -> Result<(), TextError> {
    if value.len() > MAX_OUTPUT_BYTES {
        Err(TextError::new("output_too_large", "输出文本超过允许大小。"))
    } else {
        Ok(())
    }
}

fn percent_encode(value: &str) -> String {
    const HEX: &[u8; 16] = b"0123456789ABCDEF";
    let mut output = String::with_capacity(value.len());
    for byte in value.as_bytes() {
        if byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'.' | b'_' | b'~') {
            output.push(*byte as char);
        } else {
            output.push('%');
            output.push(HEX[(byte >> 4) as usize] as char);
            output.push(HEX[(byte & 0x0f) as usize] as char);
        }
    }
    output
}

fn percent_decode(value: &str) -> Result<String, TextError> {
    let bytes = value.as_bytes();
    let mut decoded = Vec::with_capacity(bytes.len());
    let mut index = 0;
    while index < bytes.len() {
        if bytes[index] == b'%' {
            if index + 2 >= bytes.len() {
                return Err(TextError::new("invalid_url", "无效的 URL 编码输入。"));
            }
            let high = hex_value(bytes[index + 1])
                .ok_or_else(|| TextError::new("invalid_url", "无效的 URL 编码输入。"))?;
            let low = hex_value(bytes[index + 2])
                .ok_or_else(|| TextError::new("invalid_url", "无效的 URL 编码输入。"))?;
            decoded.push((high << 4) | low);
            index += 3;
        } else {
            decoded.push(bytes[index]);
            index += 1;
        }
    }
    String::from_utf8(decoded)
        .map_err(|_| TextError::new("invalid_url", "URL 内容不是有效的 UTF-8 文本。"))
}

fn hex_value(value: u8) -> Option<u8> {
    match value {
        b'0'..=b'9' => Some(value - b'0'),
        b'a'..=b'f' => Some(value - b'a' + 10),
        b'A'..=b'F' => Some(value - b'A' + 10),
        _ => None,
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
    let app = TextApp::new();
    let module_id =
        std::env::var("QINGTOOLBOX_MODULE_ID").unwrap_or_else(|_| "qing.texttools".to_string());
    let nonce = std::env::var("QINGTOOLBOX_MODULE_NONCE").unwrap_or_default();
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
                    json!({ "moduleId": module_id, "nonce": nonce, "name": "Text Tools", "protocolVersion": HOST_PROTOCOL_VERSION }),
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
}

#[cfg(windows)]
fn copy_text_to_clipboard(value: &str) -> io::Result<()> {
    use std::{mem, ptr, thread, time::Duration};
    use windows_sys::Win32::{
        Foundation::{GetLastError, GlobalFree, HANDLE},
        System::{
            DataExchange::{CloseClipboard, EmptyClipboard, OpenClipboard, SetClipboardData},
            Memory::{GlobalAlloc, GlobalLock, GlobalUnlock, GMEM_MOVEABLE},
        },
    };
    const CF_UNICODETEXT: u32 = 13;
    let mut wide = value.encode_utf16().collect::<Vec<_>>();
    wide.push(0);
    let bytes = wide.len() * mem::size_of::<u16>();
    let opened = (0..8).any(|attempt| {
        if unsafe { OpenClipboard(ptr::null_mut()) } != 0 {
            true
        } else {
            if attempt < 7 {
                thread::sleep(Duration::from_millis(20));
            }
            false
        }
    });
    if !opened {
        return Err(io::Error::from_raw_os_error(
            unsafe { GetLastError() } as i32
        ));
    }
    let result = (|| unsafe {
        if EmptyClipboard() == 0 {
            return Err(io::Error::from_raw_os_error(GetLastError() as i32));
        }
        let memory: HANDLE = GlobalAlloc(GMEM_MOVEABLE, bytes);
        if memory.is_null() {
            return Err(io::Error::from_raw_os_error(GetLastError() as i32));
        }
        let target = GlobalLock(memory) as *mut u16;
        if target.is_null() {
            GlobalFree(memory);
            return Err(io::Error::from_raw_os_error(GetLastError() as i32));
        }
        ptr::copy_nonoverlapping(wide.as_ptr(), target, wide.len());
        let _ = GlobalUnlock(memory);
        if SetClipboardData(CF_UNICODETEXT, memory).is_null() {
            GlobalFree(memory);
            return Err(io::Error::from_raw_os_error(GetLastError() as i32));
        }
        Ok(())
    })();
    unsafe { CloseClipboard() };
    result
}

#[cfg(not(windows))]
fn copy_text_to_clipboard(_value: &str) -> io::Result<()> {
    Err(io::Error::new(
        io::ErrorKind::Unsupported,
        "clipboard is only available on Windows",
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn percent_encoding_round_trips_utf8() {
        let source = "hello 世界 /?";
        assert_eq!(percent_decode(&percent_encode(source)).unwrap(), source);
    }

    #[test]
    fn percent_decode_rejects_malformed_sequences() {
        assert!(percent_decode("%0").is_err());
        assert!(percent_decode("%GG").is_err());
    }

    #[test]
    fn transforms_are_bounded_and_stateful() {
        let app = TextApp::new();
        app.invoke("setInput", &json!({"text":"{\"a\":1}"}))
            .unwrap();
        let result = app.invoke("formatJson", &json!({})).unwrap();
        assert_eq!(result["status"], "done");
        assert!(result["output"].as_str().unwrap().contains("\n"));
        assert!(app
            .invoke("setInput", &json!({"text":"x".repeat(MAX_TEXT_BYTES + 1)}))
            .is_err());
    }

    #[test]
    fn host_tokens_reject_control_and_empty_values() {
        assert!(valid_token("module.invoke.request", 64));
        assert!(!valid_token("", 64));
        assert!(!valid_token("module\nrequest", 64));
    }
}
