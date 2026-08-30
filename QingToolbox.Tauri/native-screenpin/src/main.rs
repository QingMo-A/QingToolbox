#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

//! Native Screen Pin capture module.
//!
//! Capture stays inside the Rust process. The Vue surface receives bounded
//! data URLs and opaque pin IDs only; it cannot request a file path or call a
//! screen API directly. A later slice can attach these captures to dedicated
//! native floating windows without changing this protocol boundary.

use std::{
    io::{self, BufRead, BufReader, BufWriter, Cursor, Write},
    sync::{Arc, Mutex},
};

use png::{BitDepth, ColorType, Encoder};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

const HOST_PROTOCOL_VERSION: u16 = 1;
const HOST_MAX_FRAME_BYTES: usize = 1024 * 1024;
const MAX_PINS: usize = 8;
const MAX_CAPTURE_BYTES: usize = 700 * 1024;
// A response contains the complete session pin list. Keep the encoded image
// portion below the 1 MiB protocol frame limit while leaving room for JSON
// metadata and the host envelope. The oldest pins are evicted when a new
// capture would exceed this aggregate budget.
const MAX_PIN_DATA_URL_BYTES: usize = 980 * 1024;
const MAX_DIMENSION: i32 = 4096;

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

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct DisplayBounds {
    x: i32,
    y: i32,
    width: i32,
    height: i32,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct Pin {
    id: String,
    data_url: String,
    x: i32,
    y: i32,
    width: i32,
    height: i32,
}

#[derive(Debug, Default)]
struct PinState {
    pins: Vec<Pin>,
    next_id: u64,
    status: String,
    error: Option<String>,
}

#[derive(Clone, Debug)]
struct PinApp {
    state: Arc<Mutex<PinState>>,
}

impl PinApp {
    fn new() -> Self {
        Self {
            state: Arc::new(Mutex::new(PinState {
                status: "ready".to_string(),
                ..PinState::default()
            })),
        }
    }

    fn snapshot(&self) -> Value {
        let state = self.lock();
        json!({ "pins": state.pins, "status": state.status, "error": state.error, "displayBounds": display_bounds() })
    }

    fn invoke(&self, method: &str, payload: &Value) -> Result<Value, ModuleError> {
        match method {
            "getState" => Ok(self.snapshot()),
            "getDisplayBounds" => Ok(json!({ "displayBounds": display_bounds() })),
            "captureRegion" => {
                let x = required_i32(payload, "x")?;
                let y = required_i32(payload, "y")?;
                let width = required_i32(payload, "width")?;
                let height = required_i32(payload, "height")?;
                let bounds = display_bounds();
                validate_region(&bounds, x, y, width, height)?;
                let png = capture_region(x, y, width, height)
                    .map_err(|error| ModuleError::new(error.0, error.1))?;
                if png.len() > MAX_CAPTURE_BYTES {
                    return Err(ModuleError::new(
                        "capture_too_large",
                        "截图过大，请缩小区域后重试。",
                    ));
                }
                let data_url = format!("data:image/png;base64,{}", base64_encode(&png));
                let mut state = self.lock();
                if state.pins.len() >= MAX_PINS {
                    state.pins.remove(0);
                }
                state.next_id = state.next_id.saturating_add(1);
                let pin_id = format!("pin-{:X}", state.next_id);
                state.pins.push(Pin {
                    id: pin_id,
                    data_url,
                    x,
                    y,
                    width,
                    height,
                });
                trim_pins(&mut state.pins);
                state.status = "captured".to_string();
                state.error = None;
                Ok(
                    json!({ "pins": state.pins, "status": state.status, "error": state.error, "displayBounds": bounds }),
                )
            }
            "removePin" => {
                let id = payload
                    .get("pinId")
                    .and_then(Value::as_str)
                    .ok_or_else(|| ModuleError::new("invalid_payload", "pinId 必须是字符串。"))?;
                if !valid_pin_id(id) {
                    return Err(ModuleError::new("invalid_payload", "pinId 无效。"));
                }
                let mut state = self.lock();
                let before = state.pins.len();
                state.pins.retain(|pin| pin.id != id);
                if before == state.pins.len() {
                    return Err(ModuleError::new("pin_unavailable", "截图已不存在。"));
                }
                state.status = "removed".to_string();
                state.error = None;
                Ok(
                    json!({ "pins": state.pins, "status": state.status, "error": state.error, "displayBounds": display_bounds() }),
                )
            }
            "clearPins" => {
                let mut state = self.lock();
                state.pins.clear();
                state.status = "cleared".to_string();
                state.error = None;
                Ok(
                    json!({ "pins": state.pins, "status": state.status, "error": state.error, "displayBounds": display_bounds() }),
                )
            }
            _ => Err(ModuleError::new("unknown_method", "未知的屏幕钉住操作。")),
        }
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, PinState> {
        self.state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }
}

fn required_i32(payload: &Value, key: &str) -> Result<i32, ModuleError> {
    let value = payload
        .get(key)
        .and_then(Value::as_i64)
        .ok_or_else(|| ModuleError::new("invalid_payload", format!("{key} 必须是整数。")))?;
    i32::try_from(value)
        .map_err(|_| ModuleError::new("invalid_payload", format!("{key} 超出范围。")))
}

fn validate_region(
    bounds: &DisplayBounds,
    x: i32,
    y: i32,
    width: i32,
    height: i32,
) -> Result<(), ModuleError> {
    if !(1..=MAX_DIMENSION).contains(&width) || !(1..=MAX_DIMENSION).contains(&height) {
        return Err(ModuleError::new("invalid_region", "截图尺寸超出允许范围。"));
    }
    let right = x
        .checked_add(width)
        .ok_or_else(|| ModuleError::new("invalid_region", "截图区域无效。"))?;
    let bottom = y
        .checked_add(height)
        .ok_or_else(|| ModuleError::new("invalid_region", "截图区域无效。"))?;
    if x < bounds.x
        || y < bounds.y
        || right > bounds.x.saturating_add(bounds.width)
        || bottom > bounds.y.saturating_add(bounds.height)
    {
        return Err(ModuleError::new(
            "invalid_region",
            "截图区域必须位于虚拟屏幕内。",
        ));
    }
    Ok(())
}

fn valid_pin_id(value: &str) -> bool {
    value.len() >= 5
        && value.len() <= 24
        && value.starts_with("pin-")
        && value[4..].bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn trim_pins(pins: &mut Vec<Pin>) {
    while pins.len() > MAX_PINS
        || pins.iter().map(|pin| pin.data_url.len()).sum::<usize>() > MAX_PIN_DATA_URL_BYTES
    {
        if pins.is_empty() {
            break;
        }
        pins.remove(0);
    }
}

fn base64_encode(bytes: &[u8]) -> String {
    const TABLE: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut output = String::with_capacity(bytes.len().div_ceil(3) * 4);
    for chunk in bytes.chunks(3) {
        let a = chunk[0] as u32;
        let b = chunk.get(1).copied().unwrap_or(0) as u32;
        let c = chunk.get(2).copied().unwrap_or(0) as u32;
        output.push(TABLE[((a >> 2) & 0x3f) as usize] as char);
        output.push(TABLE[(((a << 4) | (b >> 4)) & 0x3f) as usize] as char);
        output.push(if chunk.len() > 1 {
            TABLE[(((b << 2) | (c >> 6)) & 0x3f) as usize] as char
        } else {
            '='
        });
        output.push(if chunk.len() > 2 {
            TABLE[(c & 0x3f) as usize] as char
        } else {
            '='
        });
    }
    output
}

fn display_bounds() -> DisplayBounds {
    #[cfg(windows)]
    {
        use windows_sys::Win32::UI::WindowsAndMessaging::{
            GetSystemMetrics, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN, SM_XVIRTUALSCREEN,
            SM_YVIRTUALSCREEN,
        };
        DisplayBounds {
            x: unsafe { GetSystemMetrics(SM_XVIRTUALSCREEN) },
            y: unsafe { GetSystemMetrics(SM_YVIRTUALSCREEN) },
            width: unsafe { GetSystemMetrics(SM_CXVIRTUALSCREEN) },
            height: unsafe { GetSystemMetrics(SM_CYVIRTUALSCREEN) },
        }
    }
    #[cfg(not(windows))]
    {
        DisplayBounds {
            x: 0,
            y: 0,
            width: 0,
            height: 0,
        }
    }
}

#[cfg(windows)]
fn capture_region(
    x: i32,
    y: i32,
    width: i32,
    height: i32,
) -> Result<Vec<u8>, (&'static str, String)> {
    use std::{mem, ptr};
    use windows_sys::Win32::{
        Foundation::HWND,
        Graphics::Gdi::{
            BitBlt, CreateCompatibleBitmap, CreateCompatibleDC, DeleteDC, DeleteObject, GetDC,
            GetDIBits, ReleaseDC, SelectObject, BITMAPINFO, BITMAPINFOHEADER, BI_RGB,
            DIB_RGB_COLORS, SRCCOPY,
        },
    };
    if width <= 0 || height <= 0 {
        return Err(("invalid_region", "截图区域无效。".to_string()));
    }
    let screen = unsafe { GetDC(ptr::null_mut::<HWND>() as HWND) };
    if screen.is_null() {
        return Err(("capture_failed", "无法获取桌面设备上下文。".to_string()));
    }
    let memory = unsafe { CreateCompatibleDC(screen) };
    let bitmap = unsafe { CreateCompatibleBitmap(screen, width, height) };
    if memory.is_null() || bitmap.is_null() {
        if !memory.is_null() {
            unsafe { DeleteDC(memory) };
        }
        unsafe { ReleaseDC(ptr::null_mut(), screen) };
        return Err(("capture_failed", "无法创建截图缓冲区。".to_string()));
    }
    let previous = unsafe { SelectObject(memory, bitmap as _) };
    let copied = unsafe { BitBlt(memory, 0, 0, width, height, screen, x, y, SRCCOPY) };
    unsafe { SelectObject(memory, previous) };
    if copied == 0 {
        unsafe {
            DeleteObject(bitmap as _);
            DeleteDC(memory);
            ReleaseDC(ptr::null_mut(), screen);
        }
        return Err(("capture_failed", "桌面像素复制失败。".to_string()));
    }
    let mut info = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: mem::size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: width,
            biHeight: -height,
            biPlanes: 1,
            biBitCount: 32,
            biCompression: BI_RGB,
            ..BITMAPINFOHEADER::default()
        },
        bmiColors: [Default::default()],
    };
    let mut bgra = vec![0u8; width as usize * height as usize * 4];
    let lines = unsafe {
        GetDIBits(
            memory,
            bitmap,
            0,
            height as u32,
            bgra.as_mut_ptr() as *mut _,
            &mut info,
            DIB_RGB_COLORS,
        )
    };
    unsafe {
        DeleteObject(bitmap as _);
        DeleteDC(memory);
        ReleaseDC(ptr::null_mut(), screen);
    }
    if lines == 0 {
        return Err(("capture_failed", "无法读取截图像素。".to_string()));
    }
    for pixel in bgra.chunks_exact_mut(4) {
        pixel.swap(0, 2);
        pixel[3] = 255;
    }
    let mut encoded = Vec::new();
    {
        let mut encoder = Encoder::new(Cursor::new(&mut encoded), width as u32, height as u32);
        encoder.set_color(ColorType::Rgba);
        encoder.set_depth(BitDepth::Eight);
        let mut writer = encoder
            .write_header()
            .map_err(|_| ("capture_failed", "PNG 编码失败。".to_string()))?;
        writer
            .write_image_data(&bgra)
            .map_err(|_| ("capture_failed", "PNG 编码失败。".to_string()))?;
    }
    Ok(encoded)
}

#[cfg(not(windows))]
fn capture_region(
    _x: i32,
    _y: i32,
    _width: i32,
    _height: i32,
) -> Result<Vec<u8>, (&'static str, String)> {
    Err(("unsupported", "屏幕截取仅支持 Windows。".to_string()))
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
    let bytes = serde_json::to_vec(&response).ok();
    let bytes = match bytes {
        Some(bytes) if bytes.len() <= HOST_MAX_FRAME_BYTES => bytes,
        _ => serde_json::to_vec(&HostResponse {
            protocol_version: HOST_PROTOCOL_VERSION,
            message_type,
            request_id,
            payload: json!({}),
            error: Some(HostErrorBody {
                code: "response_too_large",
                message: "模块响应超过协议帧大小限制。".to_string(),
            }),
        })
        .unwrap_or_default(),
    };
    if bytes.len() <= HOST_MAX_FRAME_BYTES {
        let _ = writer.write_all(&bytes);
        let _ = writer.write_all(b"\n");
        let _ = writer.flush();
    }
}

fn main() {
    let app = PinApp::new();
    let module_id =
        std::env::var("QINGTOOLBOX_MODULE_ID").unwrap_or_else(|_| "qing.screenpin".to_string());
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
                    json!({ "moduleId": module_id, "nonce": nonce, "name": "Screen Pin", "protocolVersion": HOST_PROTOCOL_VERSION }),
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

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn region_bounds_are_strict() {
        let bounds = DisplayBounds {
            x: 0,
            y: 0,
            width: 100,
            height: 100,
        };
        assert!(validate_region(&bounds, 0, 0, 100, 100).is_ok());
        assert!(validate_region(&bounds, 1, 1, 100, 100).is_err());
        assert!(validate_region(&bounds, 0, 0, 0, 10).is_err());
    }
    #[test]
    fn pin_ids_are_opaque() {
        assert!(valid_pin_id("pin-1A"));
        assert!(!valid_pin_id("/tmp/x"));
        assert!(!valid_pin_id("pin-zz"));
    }
    #[test]
    fn state_limits_pins() {
        let app = PinApp::new();
        let mut state = app.lock();
        for index in 0..MAX_PINS + 2 {
            state.pins.push(Pin {
                id: format!("pin-{index:X}"),
                data_url: "data:image/png;base64,eA==".to_string(),
                x: 0,
                y: 0,
                width: 1,
                height: 1,
            });
        }
        trim_pins(&mut state.pins);
        assert!(state.pins.len() <= MAX_PINS);
    }

    #[test]
    fn state_data_urls_stay_inside_frame_budget() {
        let mut pins = (0..MAX_PINS)
            .map(|index| Pin {
                id: format!("pin-{index:X}"),
                data_url: "x".repeat((MAX_PIN_DATA_URL_BYTES / 2) + 1),
                x: 0,
                y: 0,
                width: 1,
                height: 1,
            })
            .collect::<Vec<_>>();
        trim_pins(&mut pins);
        assert_eq!(pins.len(), 1);
        let payload = json!({
            "pins": pins,
            "status": "captured",
            "error": Value::Null,
            "displayBounds": { "x": 0, "y": 0, "width": 1, "height": 1 }
        });
        let response = HostResponse {
            protocol_version: HOST_PROTOCOL_VERSION,
            message_type: "module.invoke.response",
            request_id: "test",
            payload,
            error: None,
        };
        let bytes = serde_json::to_vec(&response).expect("response JSON");
        assert!(bytes.len() <= HOST_MAX_FRAME_BYTES);
    }
}
