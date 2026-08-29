use std::{fs, path::Path};

use tauri::{
    http::{Request, Response, StatusCode},
    AppHandle, Manager, Runtime, Url, WebviewUrl, WebviewWindowBuilder,
};

use crate::{
    module_window_label,
    paths::{module_data_directory, resolve_existing_asset},
    HostState,
};

const MAX_ASSET_BYTES: u64 = 8 * 1024 * 1024;

/// Serve only assets inside a module that the backend has already discovered.
/// The URI contains an id and a manifest-relative route, never an absolute
/// path. Every request is canonicalized again to make replacement/symlink
/// races fail closed.
pub fn serve_module_asset<R: Runtime>(
    app: &AppHandle<R>,
    request: Request<Vec<u8>>,
) -> Response<Vec<u8>> {
    if request.method() != "GET" && request.method() != "HEAD" {
        return error_response(StatusCode::METHOD_NOT_ALLOWED, "method not allowed");
    }

    let Some((module_id, route)) = parse_route(request.uri().path()) else {
        return error_response(StatusCode::BAD_REQUEST, "invalid module asset route");
    };
    let state = app.state::<HostState>();
    let record = match state.module_index.lock() {
        Ok(index) => index.get(module_id).cloned(),
        Err(_) => {
            return error_response(
                StatusCode::INTERNAL_SERVER_ERROR,
                "module index unavailable",
            )
        }
    };
    let Some(record) = record else {
        return error_response(StatusCode::NOT_FOUND, "module not found");
    };

    let path = match resolve_existing_asset(&record.directory, route) {
        Ok(path) => path,
        Err(_) => return error_response(StatusCode::NOT_FOUND, "module asset not found"),
    };
    let Some(web_entry) = record.web_entry.as_deref() else {
        return error_response(StatusCode::FORBIDDEN, "module has no web surface");
    };
    // A Web module may reference sibling assets in the entry directory, but
    // never exposes an unrelated package directory through this scheme.
    if !route_is_allowed(route, web_entry) {
        return error_response(
            StatusCode::FORBIDDEN,
            "module asset is outside the web surface",
        );
    }

    let metadata = match fs::metadata(&path) {
        Ok(metadata) if metadata.len() <= MAX_ASSET_BYTES => metadata,
        Ok(_) => return error_response(StatusCode::PAYLOAD_TOO_LARGE, "module asset is too large"),
        Err(_) => return error_response(StatusCode::NOT_FOUND, "module asset not found"),
    };
    let body = if request.method() == "HEAD" {
        Vec::new()
    } else {
        match fs::read(&path) {
            Ok(body) => body,
            Err(_) => {
                return error_response(StatusCode::INTERNAL_SERVER_ERROR, "module asset unreadable")
            }
        }
    };
    Response::builder()
        .status(StatusCode::OK)
        .header("Content-Type", content_type(&path))
        .header("Content-Length", metadata.len().to_string())
        .header("Cache-Control", "no-store")
        .body(body)
        .expect("static response builder cannot fail")
}

/// Open or focus a module-owned Web surface. The frontend supplies only the
/// discovered module id; the entry route is read from the backend index.
pub fn open_module_window<R: Runtime>(
    app: &AppHandle<R>,
    state: &HostState,
    module_id: &str,
) -> Result<(), String> {
    let record = state
        .module_index
        .lock()
        .map_err(|_| "模块索引状态不可用。".to_string())?
        .get(module_id)
        .cloned()
        .ok_or_else(|| "模块尚未发现或清单无效，请先刷新模块。".to_string())?;
    let route = record
        .web_entry
        .as_deref()
        .ok_or_else(|| "该模块没有 Web 界面。".to_string())?;
    // The manifest validator already rejects traversal. Keep this check at
    // the window boundary as a defense against a stale/replaced index.
    resolve_existing_asset(&record.directory, route)
        .map_err(|_| "模块 Web 入口不存在或已被替换。".to_string())?;
    if route.contains([' ', '%', '?', '#', '\\']) {
        return Err("模块 Web 入口包含暂不支持的 URL 字符。".to_string());
    }
    let url = Url::parse(&format!("qmod://localhost/{module_id}/{route}"))
        .map_err(|_| "模块 Web 入口不是有效 URL。".to_string())?;
    let label = module_window_label(module_id);
    if let Some(window) = app.get_webview_window(&label) {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
        return Ok(());
    }

    let module_id_for_close = module_id.to_string();
    let app_for_close = app.clone();
    let webview_data_directory = module_data_directory(module_id)
        .map_err(|_| "模块 WebView 数据目录不可用。".to_string())?
        .join("tauri-webview");
    let window = WebviewWindowBuilder::new(app, label, WebviewUrl::CustomProtocol(url))
        .title(format!("QingToolbox · {}", record.name))
        .inner_size(960.0, 680.0)
        .resizable(true)
        .data_directory(webview_data_directory)
        .build()
        .map_err(|error| format!("无法打开模块窗口：{error}"))?;
    window.on_window_event(move |event| {
        if let tauri::WindowEvent::CloseRequested { .. } = event {
            if let Some(state) = app_for_close.try_state::<HostState>() {
                if let Ok(mut runtime) = state.runtime.lock() {
                    let _ = runtime.stop(&module_id_for_close);
                }
            }
        }
    });
    Ok(())
}

fn parse_route(path: &str) -> Option<(&str, &str)> {
    let path = path.strip_prefix('/')?;
    let (module_id, route) = path.split_once('/')?;
    if !valid_segment(module_id) || route.is_empty() || route.contains(['\\', '%', '?', '#']) {
        return None;
    }
    if route
        .split('/')
        .any(|segment| segment.is_empty() || segment == "." || segment == "..")
    {
        return None;
    }
    Some((module_id, route))
}

fn valid_segment(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value
            .bytes()
            .next()
            .is_some_and(|byte| byte.is_ascii_alphanumeric())
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-' | b'_'))
}

fn route_is_allowed(route: &str, entry: &str) -> bool {
    if route == entry {
        return true;
    }
    let Some((parent, _)) = entry.rsplit_once('/') else {
        // A root-level entry has no safe sibling directory to expose. Keep
        // the exact entry reachable, but fail closed for any other package
        // asset (including bin/ and module data files).
        return false;
    };
    route.starts_with(parent) && route.as_bytes().get(parent.len()) == Some(&b'/')
}

fn content_type(path: &Path) -> &'static str {
    match path
        .extension()
        .and_then(|extension| extension.to_str())
        .map(|extension| extension.to_ascii_lowercase())
        .as_deref()
    {
        Some("html") | Some("htm") => "text/html; charset=utf-8",
        Some("css") => "text/css; charset=utf-8",
        Some("js") | Some("mjs") => "text/javascript; charset=utf-8",
        Some("json") => "application/json; charset=utf-8",
        Some("svg") => "image/svg+xml",
        Some("png") => "image/png",
        Some("jpg") | Some("jpeg") => "image/jpeg",
        Some("gif") => "image/gif",
        Some("webp") => "image/webp",
        Some("woff") => "font/woff",
        Some("woff2") => "font/woff2",
        Some("wasm") => "application/wasm",
        _ => "application/octet-stream",
    }
}

fn error_response(status: StatusCode, message: &'static str) -> Response<Vec<u8>> {
    Response::builder()
        .status(status)
        .header("Content-Type", "text/plain; charset=utf-8")
        .body(message.as_bytes().to_vec())
        .expect("error response builder cannot fail")
}

#[cfg(test)]
mod tests {
    use super::parse_route;

    #[test]
    fn route_parser_rejects_traversal_and_encoded_paths() {
        assert_eq!(
            parse_route("/demo/ui/index.html"),
            Some(("demo", "ui/index.html"))
        );
        assert!(parse_route("/demo/../secret").is_none());
        assert!(parse_route("/demo/%2e%2e/secret").is_none());
        assert!(parse_route("/demo\\secret").is_none());
    }

    #[test]
    fn asset_routes_stay_below_the_web_entry_directory() {
        assert!(super::route_is_allowed("ui/index.html", "ui/index.html"));
        assert!(super::route_is_allowed("ui/assets/app.js", "ui/index.html"));
        assert!(!super::route_is_allowed("secret/data.txt", "ui/index.html"));
        assert!(super::route_is_allowed("index.html", "index.html"));
        assert!(!super::route_is_allowed("secret/data.txt", "index.html"));
    }
}
