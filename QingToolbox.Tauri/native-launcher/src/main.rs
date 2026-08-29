#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::{
    collections::{BTreeSet, HashMap},
    env, fs,
    io::{self, BufRead, BufWriter, Write},
    path::{Path, PathBuf},
    process::{Command, Stdio},
    time::{SystemTime, UNIX_EPOCH},
};

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

const PROTOCOL_VERSION: u16 = 1;
const MAX_FRAME_BYTES: usize = 1024 * 1024;
const MAX_ITEMS: usize = 512;
const MAX_ORDER_IDS: usize = 512;

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
#[serde(rename_all = "camelCase")]
struct ErrorBody {
    code: &'static str,
    message: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct LauncherItem {
    id: String,
    name: String,
    target: String,
    arguments: String,
    working_directory: String,
    last_launched_at: Option<String>,
    source: String,
}

#[derive(Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct StoreDocument {
    sort_mode: Option<String>,
    items: Option<Vec<LauncherItem>>,
    desktop_items: Option<Vec<LauncherItem>>,
    custom_order: Option<Vec<String>>,
}

#[derive(Debug)]
struct LauncherStore {
    path: PathBuf,
    sort_mode: String,
    items: Vec<LauncherItem>,
    desktop_items: Vec<LauncherItem>,
    custom_order: Vec<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ItemView {
    id: String,
    name: String,
    icon_key: Option<String>,
    last_launched_at: Option<String>,
    source: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct LauncherState {
    sort_mode: String,
    items: Vec<ItemView>,
    folders: Vec<Value>,
    custom_order: Vec<String>,
    recent: Vec<ItemView>,
    hotkey: HotkeyView,
    hotkey_status: &'static str,
    active: bool,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct HotkeyView {
    ctrl: bool,
    alt: bool,
    shift: bool,
    win: bool,
    virtual_key: u32,
    key_label: &'static str,
}

impl LauncherStore {
    fn load(data_directory: &Path) -> Self {
        let _ = fs::create_dir_all(data_directory);
        let path = data_directory.join("launcher.json");
        let document = fs::read(&path)
            .ok()
            .and_then(|bytes| serde_json::from_slice::<StoreDocument>(&bytes).ok())
            .unwrap_or_default();
        let mut store = Self {
            path,
            sort_mode: normalize_sort_mode(document.sort_mode.as_deref()),
            items: normalize_items(document.items.unwrap_or_default(), "custom"),
            desktop_items: normalize_items(document.desktop_items.unwrap_or_default(), "desktop"),
            custom_order: document.custom_order.unwrap_or_default(),
        };
        store.normalize_order();
        store
    }

    fn save(&self) -> io::Result<()> {
        let document = StoreDocument {
            sort_mode: Some(self.sort_mode.clone()),
            items: Some(self.items.clone()),
            desktop_items: Some(self.desktop_items.clone()),
            custom_order: Some(self.custom_order.clone()),
        };
        let bytes = serde_json::to_vec_pretty(&document)
            .map_err(|error| io::Error::other(error.to_string()))?;
        let temporary = self
            .path
            .with_extension(format!("json.tmp.{}", unique_id("write")));
        fs::write(&temporary, bytes)?;
        replace_file(&temporary, &self.path)
    }

    fn normalize_order(&mut self) {
        let known = self
            .items
            .iter()
            .map(|item| item.id.as_str())
            .collect::<BTreeSet<_>>();
        let mut seen = BTreeSet::new();
        self.custom_order
            .retain(|id| known.contains(id.as_str()) && seen.insert(id.clone()));
        for item in &self.items {
            if seen.insert(item.id.clone()) {
                self.custom_order.push(item.id.clone());
            }
        }
        self.custom_order.truncate(MAX_ORDER_IDS);
    }

    fn synchronize_desktop(&mut self, discovered: Vec<LauncherItem>) {
        // Keep the user's saved Desktop order when a refresh discovers the
        // same shortcuts again. New entries are appended in scan order; a
        // refresh must never silently reset a manually arranged grid.
        let discovered_order = discovered
            .iter()
            .map(|item| identity_key(&item.target))
            .collect::<Vec<_>>();
        let mut discovered_by_key = discovered
            .into_iter()
            .map(|item| (identity_key(&item.target), item))
            .collect::<HashMap<_, _>>();
        let mut next = Vec::new();
        for previous in self.desktop_items.drain(..) {
            let key = identity_key(&previous.target);
            let Some(mut item) = discovered_by_key.remove(&key) else {
                continue;
            };
            item.id = previous.id;
            item.last_launched_at = previous.last_launched_at;
            next.push(item);
            if next.len() >= MAX_ITEMS {
                break;
            }
        }
        if next.len() < MAX_ITEMS {
            for key in discovered_order {
                let Some(item) = discovered_by_key.remove(&key) else {
                    continue;
                };
                next.push(item);
                if next.len() >= MAX_ITEMS {
                    break;
                }
            }
        }
        self.desktop_items = next;
        let _ = self.save();
    }

    fn set_sort_mode(&mut self, mode: &str) -> bool {
        let normalized = normalize_sort_mode(Some(mode));
        if normalized != mode {
            return false;
        }
        self.sort_mode = normalized;
        self.save().is_ok()
    }

    fn set_order(&mut self, ids: &[String]) -> bool {
        if ids.len() > MAX_ORDER_IDS {
            return false;
        }
        if self.sort_mode == "alphabetical" {
            return false;
        }
        if self.sort_mode == "desktop" {
            if !same_ids(ids, self.desktop_items.iter().map(|item| &item.id)) {
                return false;
            }
            let by_id = self
                .desktop_items
                .iter()
                .cloned()
                .map(|item| (item.id.clone(), item))
                .collect::<HashMap<_, _>>();
            self.desktop_items = ids.iter().filter_map(|id| by_id.get(id).cloned()).collect();
        } else {
            if !same_ids(ids, self.items.iter().map(|item| &item.id)) {
                return false;
            }
            self.custom_order = ids.to_vec();
            let by_id = self
                .items
                .iter()
                .cloned()
                .map(|item| (item.id.clone(), item))
                .collect::<HashMap<_, _>>();
            self.items = ids.iter().filter_map(|id| by_id.get(id).cloned()).collect();
        }
        self.save().is_ok()
    }

    fn remove(&mut self, id: &str) -> bool {
        let before = self.items.len() + self.desktop_items.len();
        self.items.retain(|item| item.id != id);
        self.desktop_items.retain(|item| item.id != id);
        self.custom_order.retain(|value| value != id);
        let changed = before != self.items.len() + self.desktop_items.len();
        if changed {
            let _ = self.save();
        }
        changed
    }

    fn launch(&mut self, id: &str) -> Result<(), &'static str> {
        let item = self
            .items
            .iter()
            .chain(self.desktop_items.iter())
            .find(|item| item.id == id)
            .cloned()
            .ok_or("selected item is unavailable")?;
        let target = PathBuf::from(&item.target);
        if !target.is_file() {
            return Err("selected item no longer exists");
        }
        spawn_target(&target, &item.arguments, &item.working_directory)
            .map_err(|_| "selected item could not be started")?;
        let stamp = now_iso();
        if let Some(found) = self.items.iter_mut().find(|value| value.id == id) {
            found.last_launched_at = Some(stamp.clone());
        }
        if let Some(found) = self.desktop_items.iter_mut().find(|value| value.id == id) {
            found.last_launched_at = Some(stamp);
        }
        self.save().map_err(|_| "launcher state could not be saved")
    }

    fn state(&self) -> LauncherState {
        let source = if self.sort_mode == "desktop" {
            self.desktop_items.clone()
        } else if self.sort_mode == "alphabetical" {
            let mut values = self.items.clone();
            values.sort_by(|left, right| {
                left.name
                    .to_ascii_lowercase()
                    .cmp(&right.name.to_ascii_lowercase())
                    .then_with(|| left.id.cmp(&right.id))
            });
            values
        } else {
            self.custom_order
                .iter()
                .filter_map(|id| self.items.iter().find(|item| &item.id == id).cloned())
                .collect()
        };
        let mut all = self
            .items
            .iter()
            .chain(self.desktop_items.iter())
            .filter(|item| item.last_launched_at.is_some())
            .cloned()
            .collect::<Vec<_>>();
        all.sort_by(|left, right| {
            right
                .last_launched_at
                .cmp(&left.last_launched_at)
                .then_with(|| left.name.cmp(&right.name))
        });
        let mut recent_ids = BTreeSet::new();
        let recent = all
            .into_iter()
            .filter(|item| recent_ids.insert(identity_key(&item.target)))
            .take(10)
            .map(item_view)
            .collect();
        LauncherState {
            sort_mode: self.sort_mode.clone(),
            items: source.into_iter().map(item_view).collect(),
            folders: Vec::new(),
            custom_order: if self.sort_mode == "custom" {
                self.custom_order.clone()
            } else {
                Vec::new()
            },
            recent,
            hotkey: HotkeyView {
                ctrl: true,
                alt: true,
                shift: false,
                win: false,
                virtual_key: 32,
                key_label: "Space",
            },
            hotkey_status: "Inactive",
            active: true,
        }
    }
}

fn main() {
    let module_id = env::var("QINGTOOLBOX_MODULE_ID").unwrap_or_default();
    let expected_nonce = env::var("QINGTOOLBOX_MODULE_NONCE").unwrap_or_default();
    let data_directory = env::var_os("QINGTOOLBOX_MODULE_DATA_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| env::temp_dir().join("QingToolbox").join("qing.launcher"));
    let mut store = LauncherStore::load(&data_directory);
    let mut desktop_loaded = false;
    let stdin = io::stdin();
    let mut stdout = BufWriter::new(io::stdout());

    for line in stdin.lock().lines() {
        let line = match line {
            Ok(line) if !line.trim().is_empty() => line,
            Ok(_) => continue,
            Err(_) => break,
        };
        if line.len() > MAX_FRAME_BYTES {
            break;
        }
        let envelope: Envelope = match serde_json::from_str(&line) {
            Ok(envelope) if valid_envelope(&envelope) => envelope,
            _ => break,
        };
        match envelope.message_type.as_str() {
            "module.hello.request" => {
                if !valid_hello(&envelope.payload, &module_id, &expected_nonce) {
                    write_error(
                        &mut stdout,
                        "module.hello.response",
                        &envelope.request_id,
                        "hello_invalid",
                        "module hello identity does not match the host environment",
                    );
                    break;
                }
                write_response(
                    &mut stdout,
                    Response {
                        protocol_version: PROTOCOL_VERSION,
                        message_type: "module.hello.response",
                        request_id: &envelope.request_id,
                        payload: json!({
                            "moduleId": module_id,
                            "nonce": expected_nonce,
                            "version": env!("CARGO_PKG_VERSION"),
                        }),
                        error: None,
                    },
                );
            }
            "module.invoke.request" => {
                let Some(object) = envelope.payload.as_object() else {
                    write_error(
                        &mut stdout,
                        "module.invoke.response",
                        &envelope.request_id,
                        "invalid_payload",
                        "invoke payload must be an object",
                    );
                    continue;
                };
                let Some(method) = object.get("method").and_then(Value::as_str) else {
                    write_error(
                        &mut stdout,
                        "module.invoke.response",
                        &envelope.request_id,
                        "invalid_method",
                        "invoke method is required",
                    );
                    continue;
                };
                let payload = object.get("payload").cloned().unwrap_or(Value::Null);
                match handle_method(method, payload, &mut store, &mut desktop_loaded) {
                    Ok(value) => write_response(
                        &mut stdout,
                        Response {
                            protocol_version: PROTOCOL_VERSION,
                            message_type: "module.invoke.response",
                            request_id: &envelope.request_id,
                            payload: value,
                            error: None,
                        },
                    ),
                    Err((code, message)) => write_error(
                        &mut stdout,
                        "module.invoke.response",
                        &envelope.request_id,
                        code,
                        message,
                    ),
                }
            }
            "module.shutdown.request" => {
                write_response(
                    &mut stdout,
                    Response {
                        protocol_version: PROTOCOL_VERSION,
                        message_type: "module.shutdown.response",
                        request_id: &envelope.request_id,
                        payload: json!({ "ok": true }),
                        error: None,
                    },
                );
                break;
            }
            _ => write_error(
                &mut stdout,
                "module.error.response",
                &envelope.request_id,
                "unknown_message",
                "the launcher does not implement this message",
            ),
        }
    }
}

fn handle_method(
    method: &str,
    payload: Value,
    store: &mut LauncherStore,
    desktop_loaded: &mut bool,
) -> Result<Value, (&'static str, String)> {
    match method {
        "ping" => Ok(json!({ "pong": true })),
        "getState" => {
            ensure_desktop(store, desktop_loaded);
            serde_json::to_value(store.state()).map_err(|_| {
                (
                    "serialization_failed",
                    "state could not be serialized".to_string(),
                )
            })
        }
        "refreshDesktop" => {
            *desktop_loaded = false;
            ensure_desktop(store, desktop_loaded);
            serde_json::to_value(store.state()).map_err(|_| {
                (
                    "serialization_failed",
                    "state could not be serialized".to_string(),
                )
            })
        }
        "setSortMode" => {
            let mode = required_string(&payload, "mode")?;
            if !store.set_sort_mode(&mode) {
                return Err((
                    "invalid_sort_mode",
                    "sort mode must be custom, alphabetical or desktop".to_string(),
                ));
            }
            if mode == "desktop" {
                ensure_desktop(store, desktop_loaded);
            }
            serde_json::to_value(store.state()).map_err(|_| {
                (
                    "serialization_failed",
                    "state could not be serialized".to_string(),
                )
            })
        }
        "setCustomOrder" => {
            let ids = required_string_array(&payload, "ids")?;
            if !store.set_order(&ids) {
                return Err((
                    "invalid_order",
                    "order must contain every active item exactly once".to_string(),
                ));
            }
            serde_json::to_value(store.state()).map_err(|_| {
                (
                    "serialization_failed",
                    "state could not be serialized".to_string(),
                )
            })
        }
        "launchItem" => {
            let id = required_string(&payload, "id")?;
            store
                .launch(&id)
                .map_err(|message| ("launch_failed", message.to_string()))?;
            serde_json::to_value(store.state()).map_err(|_| {
                (
                    "serialization_failed",
                    "state could not be serialized".to_string(),
                )
            })
        }
        "removeItem" => {
            let id = required_string(&payload, "id")?;
            let _ = store.remove(&id);
            serde_json::to_value(store.state()).map_err(|_| {
                (
                    "serialization_failed",
                    "state could not be serialized".to_string(),
                )
            })
        }
        _ => Err((
            "unknown_method",
            format!("unknown launcher operation: {method}"),
        )),
    }
}

fn ensure_desktop(store: &mut LauncherStore, loaded: &mut bool) {
    if *loaded {
        return;
    }
    store.synchronize_desktop(scan_desktop());
    *loaded = true;
}

fn scan_desktop() -> Vec<LauncherItem> {
    let mut roots = Vec::new();
    if let Some(profile) = env::var_os("USERPROFILE") {
        roots.push(PathBuf::from(profile).join("Desktop"));
    }
    if let Some(one_drive) = env::var_os("OneDrive") {
        roots.push(PathBuf::from(one_drive).join("Desktop"));
    }
    let mut entries = Vec::new();
    for root in roots {
        let Ok(read_dir) = fs::read_dir(root) else {
            continue;
        };
        for entry in read_dir.flatten() {
            let path = entry.path();
            let Some(extension) = path.extension().and_then(|value| value.to_str()) else {
                continue;
            };
            if !matches!(
                extension.to_ascii_lowercase().as_str(),
                "exe" | "lnk" | "url"
            ) {
                continue;
            }
            let Some(name) = path.file_stem().and_then(|value| value.to_str()) else {
                continue;
            };
            let target = normalize_path(&path);
            entries.push(LauncherItem {
                id: stable_id(&target),
                name: name.trim().to_string(),
                target: target.clone(),
                arguments: String::new(),
                working_directory: path.parent().map(normalize_path).unwrap_or_default(),
                last_launched_at: None,
                source: "desktop".to_string(),
            });
        }
    }
    entries.sort_by(|left, right| {
        left.name
            .to_ascii_lowercase()
            .cmp(&right.name.to_ascii_lowercase())
    });
    entries.truncate(MAX_ITEMS);
    entries
}

fn spawn_target(target: &Path, arguments: &str, working_directory: &str) -> io::Result<()> {
    let extension = target
        .extension()
        .and_then(|value| value.to_str())
        .unwrap_or_default()
        .to_ascii_lowercase();
    let mut command = if matches!(extension.as_str(), "lnk" | "url") {
        // Explorer understands both Windows shortcuts and Internet
        // shortcuts directly. Avoid routing a user-controlled filename
        // through `cmd.exe`, where shell metacharacters would be re-parsed.
        let mut command = Command::new("explorer.exe");
        command.arg(target);
        command
    } else {
        let mut command = Command::new(target);
        if !arguments.trim().is_empty() {
            command.args(split_arguments(arguments));
        }
        command
    };
    if !working_directory.trim().is_empty() {
        command.current_dir(working_directory);
    }
    command
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map(|_| ())
}

fn split_arguments(value: &str) -> Vec<String> {
    // Keep the first migration slice deliberately conservative. A future
    // shortcut resolver will preserve Windows quoting exactly instead of
    // pretending whitespace splitting is a complete command-line parser.
    value.split_whitespace().map(str::to_string).collect()
}

fn normalize_items(items: Vec<LauncherItem>, source: &str) -> Vec<LauncherItem> {
    let mut seen = BTreeSet::new();
    items
        .into_iter()
        .filter(|item| !item.id.trim().is_empty() && !item.target.trim().is_empty())
        .filter_map(|mut item| {
            item.id = item.id.trim().to_string();
            item.name = if item.name.trim().is_empty() {
                Path::new(&item.target)
                    .file_stem()
                    .and_then(|value| value.to_str())
                    .unwrap_or("Application")
                    .to_string()
            } else {
                item.name.trim().chars().take(256).collect()
            };
            item.target = normalize_path(Path::new(&item.target));
            item.working_directory = if item.working_directory.trim().is_empty() {
                Path::new(&item.target)
                    .parent()
                    .map(normalize_path)
                    .unwrap_or_default()
            } else {
                normalize_path(Path::new(&item.working_directory))
            };
            item.source = source.to_string();
            seen.insert(item.id.clone()).then_some(item)
        })
        .take(MAX_ITEMS)
        .collect()
}

fn item_view(item: LauncherItem) -> ItemView {
    ItemView {
        id: item.id,
        name: item.name,
        icon_key: None,
        last_launched_at: item.last_launched_at,
        source: item.source,
    }
}

fn required_string(payload: &Value, name: &str) -> Result<String, (&'static str, String)> {
    payload
        .get(name)
        .and_then(Value::as_str)
        .filter(|value| !value.trim().is_empty())
        .map(str::to_string)
        .ok_or(("invalid_payload", format!("{name} is required")))
}

fn required_string_array(
    payload: &Value,
    name: &str,
) -> Result<Vec<String>, (&'static str, String)> {
    let values = payload
        .get(name)
        .and_then(Value::as_array)
        .ok_or(("invalid_payload", format!("{name} is required")))?;
    if values.len() > MAX_ORDER_IDS {
        return Err(("invalid_payload", format!("{name} is too large")));
    }
    values
        .iter()
        .map(|value| {
            value
                .as_str()
                .filter(|item| !item.trim().is_empty())
                .map(str::to_string)
                .ok_or(("invalid_payload", format!("{name} must contain strings")))
        })
        .collect()
}

fn same_ids<'a>(left: &[String], right: impl Iterator<Item = &'a String>) -> bool {
    let values = right.cloned().collect::<Vec<_>>();
    left.len() == values.len()
        && left.iter().collect::<BTreeSet<_>>() == values.iter().collect::<BTreeSet<_>>()
        && left.len() == left.iter().collect::<BTreeSet<_>>().len()
}

fn valid_envelope(envelope: &Envelope) -> bool {
    envelope.protocol_version == PROTOCOL_VERSION
        && valid_token(&envelope.message_type, 64)
        && valid_token(&envelope.request_id, 128)
}

fn valid_hello(payload: &Value, module_id: &str, nonce: &str) -> bool {
    payload.get("moduleId").and_then(Value::as_str) == Some(module_id)
        && payload.get("nonce").and_then(Value::as_str) == Some(nonce)
}

fn valid_token(value: &str, max: usize) -> bool {
    !value.is_empty()
        && value.len() <= max
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-' | b'_' | b':'))
}

fn normalize_sort_mode(value: Option<&str>) -> String {
    match value {
        Some("alphabetical") => "alphabetical".to_string(),
        Some("desktop") => "desktop".to_string(),
        _ => "custom".to_string(),
    }
}

fn identity_key(value: &str) -> String {
    value.replace('/', "\\").to_ascii_lowercase()
}

fn stable_id(value: &str) -> String {
    use std::hash::{Hash, Hasher};
    let mut hasher = std::collections::hash_map::DefaultHasher::new();
    identity_key(value).hash(&mut hasher);
    format!("desktop-{:016x}", hasher.finish())
}

fn normalize_path(path: &Path) -> String {
    fs::canonicalize(path)
        .unwrap_or_else(|_| path.to_path_buf())
        .to_string_lossy()
        .to_string()
}

fn unique_id(prefix: &str) -> String {
    let timestamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|value| value.as_nanos())
        .unwrap_or_default();
    format!("{prefix}-{timestamp:x}")
}

fn now_iso() -> String {
    // A sortable UTC-ish stamp without pulling a date/time dependency into
    // every module. The host only needs monotonic ordering for Recent.
    let millis = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|value| value.as_millis())
        .unwrap_or_default();
    format!("{millis:020}")
}

fn replace_file(temporary: &Path, destination: &Path) -> io::Result<()> {
    if destination.exists() {
        let backup = destination.with_extension("json.bak");
        let _ = fs::remove_file(&backup);
        fs::rename(destination, &backup)?;
        if let Err(error) = fs::rename(temporary, destination) {
            let _ = fs::rename(&backup, destination);
            return Err(error);
        }
        let _ = fs::remove_file(backup);
    } else {
        fs::rename(temporary, destination)?;
    }
    Ok(())
}

fn write_error(
    stdout: &mut impl Write,
    message_type: &str,
    request_id: &str,
    code: &'static str,
    message: impl Into<String>,
) {
    write_response(
        stdout,
        Response {
            protocol_version: PROTOCOL_VERSION,
            message_type,
            request_id,
            payload: Value::Null,
            error: Some(ErrorBody {
                code,
                message: message.into(),
            }),
        },
    );
}

fn write_response(stdout: &mut impl Write, response: Response<'_>) {
    if serde_json::to_writer(&mut *stdout, &response).is_ok() {
        let _ = stdout.write_all(b"\n");
        let _ = stdout.flush();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn test_store() -> LauncherStore {
        LauncherStore {
            path: env::temp_dir().join(format!("qing-launcher-test-{}.json", unique_id("store"))),
            sort_mode: "custom".to_string(),
            items: vec![LauncherItem {
                id: "one".to_string(),
                name: "One".to_string(),
                target: "C:\\One.exe".to_string(),
                arguments: String::new(),
                working_directory: "C:\\".to_string(),
                last_launched_at: None,
                source: "custom".to_string(),
            }],
            desktop_items: Vec::new(),
            custom_order: vec!["one".to_string()],
        }
    }

    #[test]
    fn sort_mode_and_order_are_bounded() {
        let mut store = test_store();
        assert!(store.set_sort_mode("alphabetical"));
        assert!(!store.set_order(&["one".to_string()]));
        assert!(!store.set_sort_mode("../desktop"));
    }

    #[test]
    fn state_does_not_expose_targets() {
        let store = test_store();
        let state = serde_json::to_value(store.state()).expect("state JSON");
        assert!(state.get("items").unwrap()[0].get("target").is_none());
        assert!(state.get("items").unwrap()[0].get("id").is_some());
    }

    #[test]
    fn hello_is_bound_to_both_identity_values() {
        let payload = json!({ "moduleId": "qing.launcher", "nonce": "abc" });
        assert!(valid_hello(&payload, "qing.launcher", "abc"));
        assert!(!valid_hello(&payload, "other", "abc"));
        assert!(!valid_hello(&payload, "qing.launcher", "def"));
    }

    #[test]
    fn order_requires_exact_unique_ids() {
        assert!(same_ids(
            &["a".to_string(), "b".to_string()],
            ["b".to_string(), "a".to_string()].iter()
        ));
        assert!(!same_ids(
            &["a".to_string(), "a".to_string()],
            ["a".to_string(), "b".to_string()].iter()
        ));
    }

    #[test]
    fn desktop_refresh_preserves_saved_order_and_appends_new_items() {
        let mut store = test_store();
        store.desktop_items = vec![
            LauncherItem {
                id: "desktop-b".to_string(),
                name: "B".to_string(),
                target: "C:\\B.lnk".to_string(),
                arguments: String::new(),
                working_directory: "C:\\".to_string(),
                last_launched_at: Some("00000000000000000001".to_string()),
                source: "desktop".to_string(),
            },
            LauncherItem {
                id: "desktop-a".to_string(),
                name: "A".to_string(),
                target: "C:\\A.lnk".to_string(),
                arguments: String::new(),
                working_directory: "C:\\".to_string(),
                last_launched_at: None,
                source: "desktop".to_string(),
            },
        ];
        let discovered = vec![
            LauncherItem {
                id: "new-a".to_string(),
                name: "A updated".to_string(),
                target: "C:\\A.lnk".to_string(),
                arguments: String::new(),
                working_directory: "C:\\".to_string(),
                last_launched_at: None,
                source: "desktop".to_string(),
            },
            LauncherItem {
                id: "new-b".to_string(),
                name: "B updated".to_string(),
                target: "C:\\B.lnk".to_string(),
                arguments: String::new(),
                working_directory: "C:\\".to_string(),
                last_launched_at: None,
                source: "desktop".to_string(),
            },
            LauncherItem {
                id: "new-c".to_string(),
                name: "C".to_string(),
                target: "C:\\C.lnk".to_string(),
                arguments: String::new(),
                working_directory: "C:\\".to_string(),
                last_launched_at: None,
                source: "desktop".to_string(),
            },
        ];
        let path = store.path.clone();
        store.synchronize_desktop(discovered);
        assert_eq!(
            store
                .desktop_items
                .iter()
                .map(|item| item.target.as_str())
                .collect::<Vec<_>>(),
            vec!["C:\\B.lnk", "C:\\A.lnk", "C:\\C.lnk"]
        );
        assert_eq!(store.desktop_items[0].id, "desktop-b");
        assert_eq!(
            store.desktop_items[0].last_launched_at.as_deref(),
            Some("00000000000000000001")
        );
        let _ = fs::remove_file(path);
    }
}
