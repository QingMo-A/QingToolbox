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

mod everything;
mod icons;

use everything::{
    parse_search_mode, EverythingRuntime, EverythingSearchMode, EverythingSearchResponse,
};

const PROTOCOL_VERSION: u16 = 1;
const MAX_FRAME_BYTES: usize = 1024 * 1024;
const MAX_ITEMS: usize = 512;
const MAX_ORDER_IDS: usize = 512;
const MAX_FOLDERS: usize = 128;
const MAX_FOLDER_ITEMS: usize = 512;
const MAX_FOLDER_NAME_LENGTH: usize = 40;
const MAX_EXTERNAL_DROP_PATHS: usize = 32;
const MAX_EXTERNAL_DROP_PATH_LENGTH: usize = 32 * 1024;
// State contains small icon ids; high-resolution PNGs are fetched separately
// so a large launcher stays inside the 1 MiB line-delimited protocol limit.

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
    /// A bounded, session-local presentation asset. It is intentionally not
    /// persisted in launcher.json; icons are re-read from the trusted target
    /// when a module process starts or the Desktop projection refreshes.
    #[serde(skip)]
    icon_data_url: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct LauncherFolder {
    id: String,
    name: String,
    item_ids: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct HotkeySpec {
    ctrl: bool,
    alt: bool,
    shift: bool,
    win: bool,
    virtual_key: u32,
    key_label: String,
}

impl Default for HotkeySpec {
    fn default() -> Self {
        Self {
            ctrl: true,
            alt: true,
            shift: false,
            win: false,
            virtual_key: 0x4c,
            key_label: "L".to_string(),
        }
    }
}

impl HotkeySpec {
    fn normalized(mut self) -> Self {
        self.virtual_key = self.virtual_key.clamp(1, 0xff);
        self.key_label = self.key_label.trim().chars().take(32).collect();
        if !valid_key_label(&self.key_label) {
            self.key_label = "L".to_string();
            self.virtual_key = 0x4c;
        }
        self
    }

    fn valid(&self) -> bool {
        self.virtual_key > 0
            && self.virtual_key <= 0xff
            && (self.ctrl || self.alt || self.shift || self.win)
    }
}

#[derive(Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct StoreDocument {
    sort_mode: Option<String>,
    items: Option<Vec<LauncherItem>>,
    desktop_items: Option<Vec<LauncherItem>>,
    folders: Option<Vec<LauncherFolder>>,
    custom_order: Option<Vec<String>>,
    hotkey: Option<HotkeySpec>,
}

#[derive(Debug)]
struct LauncherStore {
    path: PathBuf,
    sort_mode: String,
    items: Vec<LauncherItem>,
    desktop_items: Vec<LauncherItem>,
    folders: Vec<LauncherFolder>,
    custom_order: Vec<String>,
    hotkey: HotkeySpec,
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
struct FolderView {
    id: String,
    name: String,
    items: Vec<ItemView>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct LauncherState {
    sort_mode: String,
    items: Vec<ItemView>,
    folders: Vec<FolderView>,
    custom_order: Vec<String>,
    recent: Vec<ItemView>,
    hotkey: HotkeyView,
    hotkey_status: String,
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
    key_label: String,
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
            folders: document.folders.unwrap_or_default(),
            custom_order: document.custom_order.unwrap_or_default(),
            hotkey: document.hotkey.unwrap_or_default().normalized(),
        };
        store.normalize_folders();
        store.normalize_order();
        store
    }

    fn save(&self) -> io::Result<()> {
        let document = StoreDocument {
            sort_mode: Some(self.sort_mode.clone()),
            items: Some(self.items.clone()),
            desktop_items: Some(self.desktop_items.clone()),
            folders: Some(self.folders.clone()),
            custom_order: Some(self.custom_order.clone()),
            hotkey: Some(self.hotkey.clone()),
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
        let foldered = self
            .folders
            .iter()
            .flat_map(|folder| folder.item_ids.iter())
            .cloned()
            .collect::<BTreeSet<_>>();
        let known = self
            .items
            .iter()
            .filter(|item| !foldered.contains(&item.id))
            .map(|item| item.id.as_str())
            .chain(self.folders.iter().map(|folder| folder.id.as_str()))
            .collect::<BTreeSet<_>>();
        let mut seen = BTreeSet::new();
        self.custom_order
            .retain(|id| known.contains(id.as_str()) && seen.insert(id.clone()));
        for item in &self.items {
            if !foldered.contains(&item.id) && seen.insert(item.id.clone()) {
                self.custom_order.push(item.id.clone());
            }
        }
        for folder in &self.folders {
            if seen.insert(folder.id.clone()) {
                self.custom_order.push(folder.id.clone());
            }
        }
        self.custom_order.truncate(MAX_ORDER_IDS);
    }

    fn normalize_folders(&mut self) {
        let known_items = self
            .items
            .iter()
            .map(|item| item.id.clone())
            .collect::<BTreeSet<_>>();
        let mut folder_ids = BTreeSet::new();
        let mut claimed = BTreeSet::new();
        self.folders = std::mem::take(&mut self.folders)
            .into_iter()
            .filter_map(|mut folder| {
                folder.id = folder.id.trim().to_string();
                if !valid_local_id(&folder.id)
                    || known_items.contains(&folder.id)
                    || !folder_ids.insert(folder.id.clone())
                {
                    return None;
                }
                folder.name = normalize_folder_name(&folder.name);
                folder.item_ids = folder
                    .item_ids
                    .into_iter()
                    .map(|id| id.trim().to_string())
                    .filter(|id| known_items.contains(id) && claimed.insert(id.clone()))
                    .take(MAX_FOLDER_ITEMS)
                    .collect();
                Some(folder)
            })
            .take(MAX_FOLDERS)
            .collect();
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

    /// Copy an already discovered Desktop entry into the independent Custom
    /// collection. The WebView can only supply its opaque Desktop id, never a
    /// filesystem path. Repeated drops select the existing Custom entry.
    fn add_desktop_item_to_custom(&mut self, desktop_id: &str) -> Result<(), &'static str> {
        if self.sort_mode != "desktop" {
            return Err("desktop mode is required");
        }
        let desktop = self
            .desktop_items
            .iter()
            .find(|item| item.id == desktop_id)
            .ok_or("desktop item not found")?
            .clone();
        let previous_items = self.items.clone();
        let previous_order = self.custom_order.clone();
        let previous_mode = self.sort_mode.clone();
        let key = identity_key(&desktop.target);
        if !self
            .items
            .iter()
            .any(|item| identity_key(&item.target) == key)
        {
            if self.items.len() >= MAX_ITEMS || self.custom_order.len() >= MAX_ORDER_IDS {
                return Err("custom launcher is full");
            }
            let mut item = desktop;
            item.id = custom_stable_id(&item.target);
            item.source = "custom".to_string();
            self.custom_order.push(item.id.clone());
            self.items.push(item);
            self.normalize_order();
        }
        self.sort_mode = "custom".to_string();
        if self.save().is_err() {
            self.items = previous_items;
            self.custom_order = previous_order;
            self.sort_mode = previous_mode;
            return Err("launcher state could not be saved");
        }
        Ok(())
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
            if !same_ids(ids, self.top_level_ids().iter()) {
                return false;
            }
            self.custom_order = ids.to_vec();
        }
        self.save().is_ok()
    }

    fn remove_custom_item(&mut self, id: &str) -> Result<(), &'static str> {
        if self.sort_mode == "desktop" || !self.items.iter().any(|item| item.id == id) {
            return Err("custom item not found");
        }
        let previous_items = self.items.clone();
        let previous_order = self.custom_order.clone();
        let previous_folders = self.folders.clone();
        self.items.retain(|item| item.id != id);
        self.custom_order.retain(|value| value != id);
        for folder in &mut self.folders {
            folder.item_ids.retain(|value| value != id);
        }
        if self.save().is_err() {
            self.items = previous_items;
            self.custom_order = previous_order;
            self.folders = previous_folders;
            return Err("launcher state could not be saved");
        }
        Ok(())
    }

    fn top_level_ids(&self) -> Vec<String> {
        let foldered = self
            .folders
            .iter()
            .flat_map(|folder| folder.item_ids.iter())
            .cloned()
            .collect::<BTreeSet<_>>();
        self.items
            .iter()
            .filter(|item| !foldered.contains(&item.id))
            .map(|item| item.id.clone())
            .chain(self.folders.iter().map(|folder| folder.id.clone()))
            .collect()
    }

    fn create_folder(&mut self, name: &str) -> Result<(), &'static str> {
        if self.folders.len() >= MAX_FOLDERS {
            return Err("folder limit reached");
        }
        let folder = LauncherFolder {
            id: unique_id("folder"),
            name: normalize_folder_name(name),
            item_ids: Vec::new(),
        };
        self.custom_order.push(folder.id.clone());
        self.folders.push(folder);
        self.normalize_order();
        self.save().map_err(|_| "launcher state could not be saved")
    }

    fn rename_folder(&mut self, id: &str, name: &str) -> Result<(), &'static str> {
        let folder = self
            .folders
            .iter_mut()
            .find(|folder| folder.id == id)
            .ok_or("folder not found")?;
        folder.name = normalize_folder_name(name);
        self.save().map_err(|_| "launcher state could not be saved")
    }

    fn move_item_to_folder(&mut self, item_id: &str, folder_id: &str) -> Result<(), &'static str> {
        if self.sort_mode != "custom"
            || !self.items.iter().any(|item| item.id == item_id)
            || !self.custom_order.iter().any(|id| id == item_id)
        {
            return Err("item is not in the custom grid");
        }
        let target_index = self
            .folders
            .iter()
            .position(|folder| folder.id == folder_id)
            .ok_or("folder not found")?;
        if self.folders[target_index].item_ids.len() >= MAX_FOLDER_ITEMS
            && !self.folders[target_index]
                .item_ids
                .iter()
                .any(|id| id == item_id)
        {
            return Err("folder item limit reached");
        }
        let previous_folders = self.folders.clone();
        let previous_order = self.custom_order.clone();
        for folder in &mut self.folders {
            folder.item_ids.retain(|id| id != item_id);
        }
        self.custom_order.retain(|id| id != item_id);
        let folder = &mut self.folders[target_index];
        if !folder.item_ids.iter().any(|id| id == item_id) {
            folder.item_ids.push(item_id.to_string());
        }
        if self.save().is_err() {
            self.folders = previous_folders;
            self.custom_order = previous_order;
            return Err("launcher state could not be saved");
        }
        Ok(())
    }

    fn move_item_out_of_folder(
        &mut self,
        item_id: &str,
        folder_id: &str,
    ) -> Result<(), &'static str> {
        {
            let folder = self
                .folders
                .iter_mut()
                .find(|folder| folder.id == folder_id)
                .ok_or("folder not found")?;
            if !folder.item_ids.iter().any(|id| id == item_id) {
                return Err("item is not in folder");
            }
            folder.item_ids.retain(|id| id != item_id);
        }
        let position = self
            .custom_order
            .iter()
            .position(|id| id == folder_id)
            .map(|index| index + 1)
            .unwrap_or(self.custom_order.len());
        self.custom_order
            .insert(position.min(self.custom_order.len()), item_id.to_string());
        self.save().map_err(|_| "launcher state could not be saved")
    }

    fn set_folder_order(&mut self, folder_id: &str, ids: &[String]) -> Result<(), &'static str> {
        let folder = self
            .folders
            .iter_mut()
            .find(|folder| folder.id == folder_id)
            .ok_or("folder not found")?;
        if !same_ids(ids, folder.item_ids.iter()) {
            return Err("order must contain every folder item exactly once");
        }
        folder.item_ids = ids.to_vec();
        self.save().map_err(|_| "launcher state could not be saved")
    }

    fn delete_folder(&mut self, folder_id: &str) -> Result<(), &'static str> {
        let index = self
            .folders
            .iter()
            .position(|folder| folder.id == folder_id)
            .ok_or("folder not found")?;
        let folder = self.folders.remove(index);
        let position = self
            .custom_order
            .iter()
            .position(|id| id == folder_id)
            .unwrap_or(self.custom_order.len());
        self.custom_order.retain(|id| id != folder_id);
        let items = folder
            .item_ids
            .into_iter()
            .filter(|id| self.items.iter().any(|item| &item.id == id));
        self.custom_order.splice(
            position.min(self.custom_order.len())..position.min(self.custom_order.len()),
            items,
        );
        self.normalize_order();
        self.save().map_err(|_| "launcher state could not be saved")
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

    fn set_hotkey(&mut self, hotkey: HotkeySpec) -> Result<(), &'static str> {
        let hotkey = hotkey.normalized();
        if !hotkey.valid() {
            return Err("hotkey must contain a modifier and a valid key");
        }
        self.hotkey = hotkey;
        self.save().map_err(|_| "launcher state could not be saved")
    }

    /// Add paths delivered by the host's native Explorer drop boundary. The
    /// module validates them again because process events are an untrusted
    /// input even though the host already canonicalizes the OS payload.
    fn add_external_paths(&mut self, paths: &[String]) -> Result<usize, &'static str> {
        let previous_items = self.items.clone();
        let previous_order = self.custom_order.clone();
        let mut existing = self
            .items
            .iter()
            .map(|item| identity_key(&item.target))
            .collect::<BTreeSet<_>>();
        let mut added = 0;
        for raw in paths.iter().take(MAX_EXTERNAL_DROP_PATHS) {
            if raw.chars().count() > MAX_EXTERNAL_DROP_PATH_LENGTH {
                continue;
            }
            let path = PathBuf::from(raw);
            if !path.is_absolute() || !path.is_file() {
                continue;
            }
            let Some(extension) = path
                .extension()
                .and_then(|value| value.to_str())
                .map(|value| value.to_ascii_lowercase())
            else {
                continue;
            };
            if !matches!(extension.as_str(), "exe" | "lnk" | "url") {
                continue;
            }
            let Ok(canonical) = fs::canonicalize(&path) else {
                continue;
            };
            let target = canonical.to_string_lossy().to_string();
            let key = identity_key(&target);
            if !existing.insert(key) {
                continue;
            }
            if self.items.len() >= MAX_ITEMS {
                break;
            }
            let name = canonical
                .file_stem()
                .and_then(|value| value.to_str())
                .filter(|value| !value.trim().is_empty())
                .unwrap_or("Application")
                .trim()
                .chars()
                .take(256)
                .collect::<String>();
            let id = custom_stable_id(&target);
            if self.items.iter().any(|item| item.id == id) {
                continue;
            }
            self.items.push(LauncherItem {
                id: id.clone(),
                name,
                target: target.clone(),
                arguments: String::new(),
                working_directory: canonical.parent().map(normalize_path).unwrap_or_default(),
                last_launched_at: None,
                source: "custom".to_string(),
                icon_data_url: None,
            });
            self.custom_order.push(id);
            added += 1;
        }
        if added > 0 {
            self.normalize_order();
            if self.save().is_err() {
                self.items = previous_items;
                self.custom_order = previous_order;
                return Err("launcher state could not be saved");
            }
        }
        Ok(added)
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
        let items = source.iter().map(item_view).collect();
        let folders = if self.sort_mode == "custom" {
            self.custom_order
                .iter()
                .filter_map(|id| self.folders.iter().find(|folder| &folder.id == id))
                .map(|folder| FolderView {
                    id: folder.id.clone(),
                    name: folder.name.clone(),
                    items: folder
                        .item_ids
                        .iter()
                        .filter_map(|id| self.items.iter().find(|item| &item.id == id))
                        .map(item_view)
                        .collect(),
                })
                .collect()
        } else {
            Vec::new()
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
            .map(|item| item_view(&item))
            .collect();
        LauncherState {
            sort_mode: self.sort_mode.clone(),
            items,
            folders,
            custom_order: if self.sort_mode == "custom" {
                self.custom_order.clone()
            } else {
                Vec::new()
            },
            recent,
            hotkey: HotkeyView {
                ctrl: self.hotkey.ctrl,
                alt: self.hotkey.alt,
                shift: self.hotkey.shift,
                win: self.hotkey.win,
                virtual_key: self.hotkey.virtual_key,
                key_label: self.hotkey.key_label.clone(),
            },
            hotkey_status: "HostManaged".to_string(),
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
    let module_directory = env::var_os("QINGTOOLBOX_MODULE_DIRECTORY")
        .map(PathBuf::from)
        .or_else(|| env::current_dir().ok())
        .unwrap_or_else(|| PathBuf::from("."));
    let mut everything = EverythingRuntime::new(module_directory, data_directory.clone());
    let mut desktop_loaded = false;
    let mut lifecycle_active = false;
    let stdin = io::stdin();
    let mut stdout = BufWriter::new(io::stdout());

    let mut handshaken = false;
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
            "module.lifecycle.request" if handshaken => {
                let Some(active) = envelope.payload.get("active").and_then(Value::as_bool) else {
                    break;
                };
                if !active {
                    everything.shutdown();
                }
                lifecycle_active = active;
                let response = serde_json::json!({
                    "protocolVersion": 1, "messageType": "module.lifecycle.response",
                    "requestId": envelope.request_id, "payload": { "active": active }
                });
                let _ = serde_json::to_writer(&mut stdout, &response);
                let _ = writeln!(stdout);
                let _ = stdout.flush();
            }
            "module.hello.request" if !handshaken => {
                handshaken = true;
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
                            "nonce": expected_nonce, "lifecycleVersion": 1,
                            "version": env!("CARGO_PKG_VERSION"),
                        }),
                        error: None,
                    },
                );
            }
            "module.invoke.request" if handshaken => {
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
                if method == "searchEverything" && !lifecycle_active {
                    write_error(
                        &mut stdout,
                        "module.invoke.response",
                        &envelope.request_id,
                        "module_inactive",
                        "请先启用模块。",
                    );
                    continue;
                }
                let payload = object.get("payload").cloned().unwrap_or(Value::Null);
                match handle_method(
                    method,
                    payload,
                    &mut store,
                    &mut desktop_loaded,
                    &mut everything,
                ) {
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
            "module.event" if handshaken => {
                let Some(object) = envelope.payload.as_object() else {
                    eprintln!("Qing Launcher ignored an event with a non-object payload");
                    continue;
                };
                let Some(event_type) = object.get("eventType").and_then(Value::as_str) else {
                    eprintln!("Qing Launcher ignored an event without eventType");
                    continue;
                };
                let payload = object.get("payload").cloned().unwrap_or(Value::Null);
                match handle_event(event_type, payload, &mut store, &mut desktop_loaded) {
                    Ok(()) => write_response(
                        &mut stdout,
                        Response {
                            protocol_version: PROTOCOL_VERSION,
                            message_type: "module.event.response",
                            request_id: &envelope.request_id,
                            payload: json!({ "ok": true }),
                            error: None,
                        },
                    ),
                    Err(error) => {
                        eprintln!("Qing Launcher rejected host event {event_type}: {error}");
                        write_error(
                            &mut stdout,
                            "module.event.response",
                            &envelope.request_id,
                            "event_rejected",
                            error,
                        );
                    }
                }
            }
            "module.shutdown.request" if handshaken => {
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
    everything.shutdown();
}

fn handle_event(
    event_type: &str,
    payload: Value,
    store: &mut LauncherStore,
    _desktop_loaded: &mut bool,
) -> Result<(), String> {
    match event_type {
        "launcher.externalDrop" => {
            let paths = payload
                .get("paths")
                .and_then(Value::as_array)
                .ok_or_else(|| "paths must be an array".to_string())?;
            if paths.len() > MAX_EXTERNAL_DROP_PATHS {
                return Err("too many dropped paths".to_string());
            }
            let paths = paths
                .iter()
                .map(|value| {
                    value
                        .as_str()
                        .filter(|path| !path.trim().is_empty())
                        .map(str::to_string)
                        .ok_or_else(|| "paths must contain strings".to_string())
                })
                .collect::<Result<Vec<_>, _>>()?;
            store
                .add_external_paths(&paths)
                .map(|_| ())
                .map_err(|error| error.to_string())
        }
        _ => Err("event is not declared by the launcher".to_string()),
    }
}

fn handle_method(
    method: &str,
    payload: Value,
    store: &mut LauncherStore,
    desktop_loaded: &mut bool,
    everything: &mut EverythingRuntime,
) -> Result<Value, (&'static str, String)> {
    match method {
        "ping" => Ok(json!({ "pong": true })),
        "getItemIcon" => {
            let id = required_string(&payload, "id")?;
            let item = store
                .items
                .iter_mut()
                .chain(store.desktop_items.iter_mut())
                .find(|item| item.id == id)
                .ok_or(("item_not_found", "应用不存在。".to_string()))?;
            if item.icon_data_url.is_none() {
                item.icon_data_url = icon_data_url(Path::new(&item.target));
            }
            Ok(json!({ "dataUrl": item.icon_data_url }))
        }
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
        "addDesktopItemToCustom" => {
            let id = required_string(&payload, "id")?;
            ensure_desktop(store, desktop_loaded);
            store
                .add_desktop_item_to_custom(&id)
                .map_err(|message| ("desktop_add_failed", message.to_string()))?;
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
        "setHotkey" => {
            let hotkey = parse_hotkey_payload(&payload)?;
            store
                .set_hotkey(hotkey)
                .map_err(|message| ("invalid_hotkey", message.to_string()))?;
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
            store
                .remove_custom_item(&id)
                .map_err(|message| ("remove_failed", message.to_string()))?;
            serde_json::to_value(store.state()).map_err(|_| {
                (
                    "serialization_failed",
                    "state could not be serialized".to_string(),
                )
            })
        }
        "createFolder" => {
            let name = required_string(&payload, "name")?;
            store
                .create_folder(&name)
                .map_err(|message| ("folder_failed", message.to_string()))?;
            serde_json::to_value(store.state()).map_err(|_| {
                (
                    "serialization_failed",
                    "state could not be serialized".to_string(),
                )
            })
        }
        "renameFolder" => {
            let id = required_string(&payload, "id")?;
            let name = required_string(&payload, "name")?;
            store
                .rename_folder(&id, &name)
                .map_err(|message| ("folder_failed", message.to_string()))?;
            serde_json::to_value(store.state()).map_err(|_| {
                (
                    "serialization_failed",
                    "state could not be serialized".to_string(),
                )
            })
        }
        "moveItemToFolder" => {
            let item_id = required_string(&payload, "itemId")?;
            let folder_id = required_string(&payload, "folderId")?;
            store
                .move_item_to_folder(&item_id, &folder_id)
                .map_err(|message| ("folder_failed", message.to_string()))?;
            serde_json::to_value(store.state()).map_err(|_| {
                (
                    "serialization_failed",
                    "state could not be serialized".to_string(),
                )
            })
        }
        "moveItemOutOfFolder" => {
            let item_id = required_string(&payload, "itemId")?;
            let folder_id = required_string(&payload, "folderId")?;
            store
                .move_item_out_of_folder(&item_id, &folder_id)
                .map_err(|message| ("folder_failed", message.to_string()))?;
            serde_json::to_value(store.state()).map_err(|_| {
                (
                    "serialization_failed",
                    "state could not be serialized".to_string(),
                )
            })
        }
        "setFolderOrder" => {
            let folder_id = required_string(&payload, "folderId")?;
            let ids = required_string_array(&payload, "ids")?;
            store
                .set_folder_order(&folder_id, &ids)
                .map_err(|message| ("folder_failed", message.to_string()))?;
            serde_json::to_value(store.state()).map_err(|_| {
                (
                    "serialization_failed",
                    "state could not be serialized".to_string(),
                )
            })
        }
        "deleteFolder" => {
            let folder_id = required_string(&payload, "id")?;
            store
                .delete_folder(&folder_id)
                .map_err(|message| ("folder_failed", message.to_string()))?;
            serde_json::to_value(store.state()).map_err(|_| {
                (
                    "serialization_failed",
                    "state could not be serialized".to_string(),
                )
            })
        }
        "searchEverything" => {
            let query = payload
                .get("query")
                .and_then(Value::as_str)
                .ok_or(("invalid_payload", "query is required".to_string()))?
                .to_string();
            let request_id = payload
                .get("requestId")
                .and_then(Value::as_str)
                .filter(|value| valid_token(value, 128))
                .unwrap_or("request")
                .to_string();
            let supplied_mode = match payload.get("mode") {
                None => None,
                Some(value) => Some(
                    parse_wire_search_mode(
                        value
                            .as_str()
                            .ok_or(("invalid_payload", "mode must be a string".to_string()))?,
                    )
                    .ok_or((
                        "invalid_payload",
                        "mode must be everything-all, everything-file or everything-directory"
                            .to_string(),
                    ))?,
                ),
            };
            // The UI sends the prefix-stripped query. If a caller sends a raw
            // `/e...` query, normalize it here rather than allowing a mode and
            // query to disagree.
            let (parsed_mode, parsed_query) = parse_search_mode(&query);
            let has_raw_prefix = parsed_query != query;
            let mode = supplied_mode
                .or_else(|| has_raw_prefix.then_some(parsed_mode))
                .ok_or((
                    "invalid_payload",
                    "mode must be everything-all, everything-file or everything-directory"
                        .to_string(),
                ))?;
            if has_raw_prefix && supplied_mode != Some(parsed_mode) {
                return Err((
                    "invalid_payload",
                    "Everything mode does not match the query prefix".to_string(),
                ));
            }
            let effective_query = if has_raw_prefix {
                parsed_query
            } else {
                query.clone()
            };
            let response = everything
                .search(mode, effective_query.clone(), request_id.clone())
                .unwrap_or_else(|error| EverythingSearchResponse {
                    request_id,
                    mode: mode.as_wire(),
                    query: effective_query,
                    status: error.status(),
                    results: Vec::new(),
                    error: Some(error.message),
                });
            serde_json::to_value(response).map_err(|_| {
                (
                    "serialization_failed",
                    "Everything response could not be serialized".to_string(),
                )
            })
        }
        "openEverythingResult" => {
            let result_id = required_string(&payload, "resultId")?;
            everything
                .open_result(&result_id)
                .map_err(|error| ("everything_open_failed", error.message))?;
            Ok(json!({ "ok": true }))
        }
        "openEverythingResultFolder" => {
            let result_id = required_string(&payload, "resultId")?;
            everything
                .open_result_folder(&result_id)
                .map_err(|error| ("everything_open_failed", error.message))?;
            Ok(json!({ "ok": true }))
        }
        "copyEverythingResultPath" => {
            let result_id = required_string(&payload, "resultId")?;
            everything
                .copy_result_path(&result_id)
                .map_err(|error| ("everything_copy_failed", error.message))?;
            Ok(json!({ "ok": true }))
        }
        _ => Err((
            "unknown_method",
            format!("unknown launcher operation: {method}"),
        )),
    }
}

fn parse_wire_search_mode(value: &str) -> Option<EverythingSearchMode> {
    match value {
        "everything-all" => Some(EverythingSearchMode::All),
        "everything-file" => Some(EverythingSearchMode::File),
        "everything-directory" => Some(EverythingSearchMode::Directory),
        _ => None,
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
                icon_data_url: None,
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
            item.icon_data_url = None;
            seen.insert(item.id.clone()).then_some(item)
        })
        .take(MAX_ITEMS)
        .collect()
}

/// Resolve a trusted launcher target to a high-resolution session-local PNG.
///
/// The launcher deliberately does not persist icon bytes: targets can move or
/// be replaced while the app is not running, and re-reading them on startup
/// keeps the persisted document limited to user state.  Shell extraction also
/// means `.lnk` and `.url` entries receive the same icon Windows shows for
/// them, rather than a synthetic tile background.
#[cfg(windows)]
fn icon_data_url(path: &Path) -> Option<String> {
    use std::{mem, os::windows::ffi::OsStrExt};
    use windows_sys::Win32::UI::{
        Shell::{SHGetFileInfoW, SHFILEINFOW, SHGFI_ICON, SHGFI_LARGEICON},
        WindowsAndMessaging::DestroyIcon,
    };

    if !path.exists() {
        return None;
    }
    if let Some(bytes) =
        icons::extract(path).filter(|bytes| !bytes.is_empty() && bytes.len() <= MAX_ICON_PNG_BYTES)
    {
        return Some(format!("data:image/png;base64,{}", base64_encode(&bytes)));
    }
    let mut wide = path.as_os_str().encode_wide().collect::<Vec<_>>();
    wide.push(0);
    let mut file_info = unsafe { mem::zeroed::<SHFILEINFOW>() };
    let flags = SHGFI_ICON | SHGFI_LARGEICON;
    let found = unsafe {
        SHGetFileInfoW(
            wide.as_ptr(),
            0,
            &mut file_info,
            mem::size_of::<SHFILEINFOW>() as u32,
            flags,
        )
    };
    if found == 0 || file_info.hIcon.is_null() {
        return None;
    }

    let png = render_shell_icon(file_info.hIcon);
    unsafe {
        DestroyIcon(file_info.hIcon);
    }
    png.filter(|bytes| !bytes.is_empty() && bytes.len() <= MAX_ICON_PNG_BYTES)
        .map(|bytes| format!("data:image/png;base64,{}", base64_encode(&bytes)))
}

#[cfg(not(windows))]
fn icon_data_url(_path: &Path) -> Option<String> {
    None
}

#[cfg(windows)]
const LAUNCHER_ICON_SIZE: i32 = 64;

#[cfg(windows)]
const MAX_ICON_PNG_BYTES: usize = 256 * 1024;

#[cfg(windows)]
fn render_shell_icon(icon: windows_sys::Win32::UI::WindowsAndMessaging::HICON) -> Option<Vec<u8>> {
    use std::{mem, ptr, slice};
    use windows_sys::Win32::{
        Graphics::Gdi::{
            CreateCompatibleDC, CreateDIBSection, DeleteDC, DeleteObject, GetDC, SelectObject,
            BITMAPINFO, BITMAPINFOHEADER, BI_RGB, DIB_RGB_COLORS, HGDIOBJ, RGBQUAD,
        },
        UI::WindowsAndMessaging::{DrawIconEx, DI_NORMAL},
    };

    let screen = unsafe { GetDC(ptr::null_mut()) };
    if screen.is_null() {
        return None;
    }
    let memory = unsafe { CreateCompatibleDC(screen) };
    if memory.is_null() {
        unsafe {
            windows_sys::Win32::Graphics::Gdi::ReleaseDC(ptr::null_mut(), screen);
        }
        return None;
    }

    let pixel_count = (LAUNCHER_ICON_SIZE as usize) * (LAUNCHER_ICON_SIZE as usize);
    let byte_count = pixel_count.checked_mul(4)?;
    let mut bits = ptr::null_mut();
    let bitmap_info = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: mem::size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: LAUNCHER_ICON_SIZE,
            // A negative height requests a top-down DIB, so the byte order in
            // the mapped buffer already matches the visual row order.
            biHeight: -LAUNCHER_ICON_SIZE,
            biPlanes: 1,
            biBitCount: 32,
            biCompression: BI_RGB,
            biSizeImage: byte_count as u32,
            ..Default::default()
        },
        bmiColors: [RGBQUAD::default()],
    };
    let bitmap = unsafe {
        CreateDIBSection(
            screen,
            &bitmap_info,
            DIB_RGB_COLORS,
            &mut bits,
            ptr::null_mut(),
            0,
        )
    };
    if bitmap.is_null() || bits.is_null() {
        if !bitmap.is_null() {
            unsafe { DeleteObject(bitmap as HGDIOBJ) };
        }
        unsafe {
            DeleteDC(memory);
            windows_sys::Win32::Graphics::Gdi::ReleaseDC(ptr::null_mut(), screen);
        }
        return None;
    }

    let previous = unsafe { SelectObject(memory, bitmap as HGDIOBJ) };
    let drawn = unsafe {
        DrawIconEx(
            memory,
            0,
            0,
            icon,
            LAUNCHER_ICON_SIZE,
            LAUNCHER_ICON_SIZE,
            0,
            ptr::null_mut(),
            DI_NORMAL,
        )
    } != 0;

    let rgba = if drawn {
        let bgra = unsafe { slice::from_raw_parts(bits as *const u8, byte_count) };
        let mut rgba = vec![0u8; byte_count];
        for (source, target) in bgra.chunks_exact(4).zip(rgba.chunks_exact_mut(4)) {
            let alpha = if source[3] == 0 && (source[0] | source[1] | source[2]) != 0 {
                // Some classic GDI icons leave alpha at zero for opaque
                // pixels.  Preserve real transparency while making those
                // pixels visible in a browser-rendered PNG.
                255
            } else {
                source[3]
            };
            target.copy_from_slice(&[source[2], source[1], source[0], alpha]);
        }
        Some(rgba)
    } else {
        None
    };

    unsafe {
        if !previous.is_null() {
            SelectObject(memory, previous);
        }
        DeleteObject(bitmap as HGDIOBJ);
        DeleteDC(memory);
        windows_sys::Win32::Graphics::Gdi::ReleaseDC(ptr::null_mut(), screen);
    }

    let rgba = rgba?;
    let mut bytes = Vec::new();
    {
        let cursor = std::io::Cursor::new(&mut bytes);
        let mut encoder =
            png::Encoder::new(cursor, LAUNCHER_ICON_SIZE as u32, LAUNCHER_ICON_SIZE as u32);
        encoder.set_color(png::ColorType::Rgba);
        encoder.set_depth(png::BitDepth::Eight);
        encoder.set_compression(png::Compression::Fast);
        let mut writer = encoder.write_header().ok()?;
        writer.write_image_data(&rgba).ok()?;
        writer.finish().ok()?;
    }
    Some(bytes)
}

fn base64_encode(bytes: &[u8]) -> String {
    const TABLE: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut output = String::with_capacity(bytes.len().div_ceil(3) * 4);
    for chunk in bytes.chunks(3) {
        let a = chunk[0] as u32;
        let b = chunk.get(1).copied().unwrap_or_default() as u32;
        let c = chunk.get(2).copied().unwrap_or_default() as u32;
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

fn item_view(item: &LauncherItem) -> ItemView {
    // Send only references in state, so no icon disappears when hundreds of
    // applications exceed the JSON frame budget. PNGs are fetched on demand.
    let icon_key = Some(format!("launcher-icon:{}", item.id));
    ItemView {
        id: item.id.clone(),
        name: item.name.clone(),
        icon_key,
        last_launched_at: item.last_launched_at.clone(),
        source: item.source.clone(),
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

fn parse_hotkey_payload(payload: &Value) -> Result<HotkeySpec, (&'static str, String)> {
    let object = payload.as_object().ok_or((
        "invalid_payload",
        "hotkey payload must be an object".to_string(),
    ))?;
    let bool_value = |name: &str, fallback: bool| {
        object
            .get(name)
            .and_then(Value::as_bool)
            .unwrap_or(fallback)
    };
    let virtual_key = object
        .get("virtualKey")
        .and_then(Value::as_u64)
        .unwrap_or(0x4c);
    if virtual_key > 0xff {
        return Err((
            "invalid_payload",
            "virtualKey must be between 1 and 255".to_string(),
        ));
    }
    let key_label = object
        .get("keyLabel")
        .and_then(Value::as_str)
        .unwrap_or("L")
        .trim()
        .to_string();
    if !valid_key_label(&key_label) {
        return Err((
            "invalid_payload",
            "keyLabel is not a supported keyboard key".to_string(),
        ));
    }
    Ok(HotkeySpec {
        ctrl: bool_value("ctrl", true),
        alt: bool_value("alt", true),
        shift: bool_value("shift", false),
        win: bool_value("win", false),
        virtual_key: virtual_key as u32,
        key_label,
    })
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

fn valid_local_id(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-' | b'_'))
}

fn valid_key_label(value: &str) -> bool {
    if value.is_empty() || value.chars().count() > 32 {
        return false;
    }
    if value.len() == 1
        && (value.as_bytes()[0].is_ascii_uppercase() || value.as_bytes()[0].is_ascii_digit())
    {
        return true;
    }
    if value.len() == 4 && value.starts_with("Key") && value.as_bytes()[3].is_ascii_uppercase() {
        return true;
    }
    if value.len() == 6 && value.starts_with("Digit") && value.as_bytes()[5].is_ascii_digit() {
        return true;
    }
    if let Some(number) = value
        .strip_prefix('F')
        .and_then(|value| value.parse::<u8>().ok())
    {
        if (1..=24).contains(&number) {
            return true;
        }
    }
    matches!(
        value,
        "Space"
            | "Enter"
            | "Escape"
            | "Esc"
            | "Tab"
            | "Backspace"
            | "Delete"
            | "Insert"
            | "Home"
            | "End"
            | "PageUp"
            | "PageDown"
            | "ArrowUp"
            | "ArrowDown"
            | "ArrowLeft"
            | "ArrowRight"
            | "Backquote"
            | "Minus"
            | "Equal"
            | "BracketLeft"
            | "BracketRight"
            | "Backslash"
            | "Semicolon"
            | "Quote"
            | "Comma"
            | "Period"
            | "Slash"
            | "`"
            | "-"
            | "="
            | "["
            | "]"
            | "\\"
            | ";"
            | "'"
            | ","
            | "."
            | "/"
    )
}

fn normalize_folder_name(value: &str) -> String {
    let normalized = value.trim();
    if normalized.is_empty() {
        return "文件夹".to_string();
    }
    normalized.chars().take(MAX_FOLDER_NAME_LENGTH).collect()
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

fn custom_stable_id(value: &str) -> String {
    use std::hash::{Hash, Hasher};
    let mut hasher = std::collections::hash_map::DefaultHasher::new();
    identity_key(value).hash(&mut hasher);
    format!("custom-{:016x}", hasher.finish())
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
                icon_data_url: None,
            }],
            desktop_items: Vec::new(),
            folders: Vec::new(),
            custom_order: vec!["one".to_string()],
            hotkey: HotkeySpec::default(),
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
    fn state_icon_projection_stays_inside_frame_budget() {
        let mut store = test_store();
        let icon = format!("data:image/png;base64,{}", "A".repeat(5_000));
        store.items = (0..MAX_ITEMS)
            .map(|index| LauncherItem {
                id: format!("item-{index}"),
                name: format!("Item {index}"),
                target: format!("C:\\Item{index}.exe"),
                arguments: String::new(),
                working_directory: "C:\\".to_string(),
                last_launched_at: None,
                source: "custom".to_string(),
                icon_data_url: Some(icon.clone()),
            })
            .collect();
        store.custom_order = store.items.iter().map(|item| item.id.clone()).collect();

        let state = store.state();
        let bytes = serde_json::to_vec(&state).expect("state JSON");
        let icon_bytes = state
            .items
            .iter()
            .filter_map(|item| item.icon_key.as_ref())
            .map(String::len)
            .sum::<usize>();
        assert!(icon_bytes < 32 * 1024);
        assert!(state.items.iter().all(|item| item
            .icon_key
            .as_deref()
            .unwrap()
            .starts_with("launcher-icon:")));
        assert!(bytes.len() < MAX_FRAME_BYTES);
    }

    #[test]
    fn hello_is_bound_to_both_identity_values() {
        let payload = json!({ "moduleId": "qing.launcher", "nonce": "abc" });
        assert!(valid_hello(&payload, "qing.launcher", "abc"));
        assert!(!valid_hello(&payload, "other", "abc"));
        assert!(!valid_hello(&payload, "qing.launcher", "def"));
    }

    #[test]
    fn hotkey_payload_accepts_alt_space_and_rejects_unbounded_key_names() {
        let parsed = parse_hotkey_payload(&json!({
            "ctrl": false,
            "alt": true,
            "shift": false,
            "win": false,
            "virtualKey": 0x20,
            "keyLabel": "Space"
        }))
        .expect("Alt+Space should be representable");
        assert!(parsed.alt);
        assert_eq!(parsed.virtual_key, 0x20);
        assert_eq!(parsed.key_label, "Space");

        let error = parse_hotkey_payload(&json!({
            "ctrl": true,
            "keyLabel": "not-a-key"
        }))
        .expect_err("arbitrary key labels must fail closed");
        assert_eq!(error.0, "invalid_payload");
    }

    #[test]
    fn hotkey_normalization_recovers_invalid_persisted_values() {
        let normalized = HotkeySpec {
            ctrl: false,
            alt: false,
            shift: false,
            win: false,
            virtual_key: 0,
            key_label: "../../command".to_string(),
        }
        .normalized();
        assert_eq!(normalized.key_label, "L");
        assert_eq!(normalized.virtual_key, 0x4c);
        assert!(!normalized.valid());
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
                icon_data_url: None,
            },
            LauncherItem {
                id: "desktop-a".to_string(),
                name: "A".to_string(),
                target: "C:\\A.lnk".to_string(),
                arguments: String::new(),
                working_directory: "C:\\".to_string(),
                last_launched_at: None,
                source: "desktop".to_string(),
                icon_data_url: None,
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
                icon_data_url: None,
            },
            LauncherItem {
                id: "new-b".to_string(),
                name: "B updated".to_string(),
                target: "C:\\B.lnk".to_string(),
                arguments: String::new(),
                working_directory: "C:\\".to_string(),
                last_launched_at: None,
                source: "desktop".to_string(),
                icon_data_url: None,
            },
            LauncherItem {
                id: "new-c".to_string(),
                name: "C".to_string(),
                target: "C:\\C.lnk".to_string(),
                arguments: String::new(),
                working_directory: "C:\\".to_string(),
                last_launched_at: None,
                source: "desktop".to_string(),
                icon_data_url: None,
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

    #[test]
    fn desktop_drop_adds_one_custom_item_and_keeps_desktop_independent() {
        let mut store = test_store();
        store.sort_mode = "desktop".to_string();
        store.desktop_items.push(LauncherItem {
            id: "desktop-two".to_string(),
            name: "Two".to_string(),
            target: "C:\\Two.lnk".to_string(),
            arguments: String::new(),
            working_directory: "C:\\".to_string(),
            last_launched_at: None,
            source: "desktop".to_string(),
            icon_data_url: None,
        });
        assert_eq!(store.add_desktop_item_to_custom("desktop-two"), Ok(()));
        assert_eq!(store.sort_mode, "custom");
        assert_eq!(store.items.len(), 2);
        assert_eq!(store.desktop_items.len(), 1);
        assert_eq!(store.items[1].source, "custom");
        assert_eq!(store.custom_order.last(), Some(&store.items[1].id));
        let saved: StoreDocument = serde_json::from_slice(&fs::read(&store.path).unwrap()).unwrap();
        assert_eq!(saved.sort_mode.as_deref(), Some("custom"));
        assert_eq!(saved.items.unwrap().len(), 2);
        store.sort_mode = "desktop".to_string();
        assert_eq!(store.add_desktop_item_to_custom("desktop-two"), Ok(()));
        assert_eq!(
            store.items.len(),
            2,
            "repeated drop must not duplicate the app"
        );
        let _ = fs::remove_file(store.path);
    }

    #[test]
    fn desktop_drop_rejects_unknown_or_non_desktop_ids() {
        let mut store = test_store();
        store.sort_mode = "desktop".to_string();
        assert_eq!(
            store.add_desktop_item_to_custom("C:\\Untrusted.exe"),
            Err("desktop item not found")
        );
        assert_eq!(
            store.add_desktop_item_to_custom("one"),
            Err("desktop item not found")
        );
        assert_eq!(store.items.len(), 1);
        assert_eq!(store.sort_mode, "desktop");
        let _ = fs::remove_file(store.path);
    }

    #[test]
    fn deleting_custom_icon_keeps_desktop_entry_and_persists() {
        let mut store = test_store();
        let mut desktop = store.items[0].clone();
        desktop.id = "desktop-one".to_string();
        desktop.source = "desktop".to_string();
        store.desktop_items.push(desktop);
        store.sort_mode = "alphabetical".to_string();
        assert_eq!(
            store.remove_custom_item("desktop-one"),
            Err("custom item not found")
        );
        assert_eq!(store.remove_custom_item("one"), Ok(()));
        assert!(store.items.is_empty());
        assert!(store.custom_order.is_empty());
        assert_eq!(store.desktop_items.len(), 1);
        let saved: StoreDocument = serde_json::from_slice(&fs::read(&store.path).unwrap()).unwrap();
        assert!(saved.items.unwrap().is_empty());
        assert_eq!(saved.desktop_items.unwrap().len(), 1);
        let _ = fs::remove_file(store.path);
    }

    #[test]
    fn folders_keep_items_out_of_top_level_order_and_restore_them_on_delete() {
        let mut store = test_store();
        store.items.push(LauncherItem {
            id: "two".to_string(),
            name: "Two".to_string(),
            target: "C:\\Two.exe".to_string(),
            arguments: String::new(),
            working_directory: "C:\\".to_string(),
            last_launched_at: None,
            source: "custom".to_string(),
            icon_data_url: None,
        });
        store.custom_order.push("two".to_string());

        store.create_folder("工具").expect("folder created");
        let folder_id = store.folders[0].id.clone();
        store
            .move_item_to_folder("one", &folder_id)
            .expect("item moved into folder");
        assert!(!store.custom_order.iter().any(|id| id == "one"));
        assert!(store.move_item_to_folder("one", &folder_id).is_err());
        let state = store.state();
        assert_eq!(
            state
                .items
                .iter()
                .map(|item| item.id.as_str())
                .collect::<Vec<_>>(),
            ["two"]
        );
        assert_eq!(state.folders[0].items[0].id, "one");

        assert!(store.set_order(&["two".to_string(), folder_id.clone()]));
        store
            .move_item_out_of_folder("one", &folder_id)
            .expect("item moved out");
        assert_eq!(
            store.custom_order,
            vec!["two".to_string(), folder_id.clone(), "one".to_string()]
        );
        store.delete_folder(&folder_id).expect("folder deleted");
        assert!(store.folders.is_empty());
        assert_eq!(
            store.custom_order,
            vec!["two".to_string(), "one".to_string()]
        );
    }

    #[test]
    fn folder_order_rejects_unknown_or_duplicate_items() {
        let mut store = test_store();
        store.items.push(LauncherItem {
            id: "two".to_string(),
            name: "Two".to_string(),
            target: "C:\\Two.exe".to_string(),
            arguments: String::new(),
            working_directory: "C:\\".to_string(),
            last_launched_at: None,
            source: "custom".to_string(),
            icon_data_url: None,
        });
        store.create_folder("Folder").expect("folder created");
        let folder_id = store.folders[0].id.clone();
        store
            .move_item_to_folder("one", &folder_id)
            .expect("item moved into folder");
        assert!(store
            .set_folder_order(&folder_id, &["one".to_string(), "one".to_string()])
            .is_err());
        assert!(store
            .set_folder_order(&folder_id, &["two".to_string()])
            .is_err());
    }

    #[test]
    fn folder_normalization_drops_duplicate_ids_and_memberships() {
        let mut store = test_store();
        store.items.push(LauncherItem {
            id: "two".to_string(),
            name: "Two".to_string(),
            target: "C:\\Two.exe".to_string(),
            arguments: String::new(),
            working_directory: "C:\\".to_string(),
            last_launched_at: None,
            source: "custom".to_string(),
            icon_data_url: None,
        });
        store.folders = vec![
            LauncherFolder {
                id: "folder-a".to_string(),
                name: " A ".to_string(),
                item_ids: vec!["one".to_string(), "one".to_string(), "two".to_string()],
            },
            LauncherFolder {
                id: "folder-a".to_string(),
                name: "duplicate".to_string(),
                item_ids: vec!["two".to_string()],
            },
            LauncherFolder {
                id: "two".to_string(),
                name: "collides with item".to_string(),
                item_ids: Vec::new(),
            },
        ];
        store.normalize_folders();
        assert_eq!(store.folders.len(), 1);
        assert_eq!(store.folders[0].name, "A");
        assert_eq!(store.folders[0].item_ids, vec!["one", "two"]);
    }

    #[test]
    fn everything_failure_is_contained_and_does_not_change_launcher_state() {
        let mut store = test_store();
        let before = serde_json::to_value(store.state()).expect("state JSON");
        let mut desktop_loaded = true;
        let root = env::temp_dir().join(format!(
            "qing-launcher-everything-missing-{}",
            unique_id("test")
        ));
        let mut runtime = EverythingRuntime::new(root.clone(), root.join("data"));
        let response = handle_method(
            "searchEverything",
            json!({
                "mode": "everything-file",
                "query": "*.exe",
                "requestId": "test-1"
            }),
            &mut store,
            &mut desktop_loaded,
            &mut runtime,
        )
        .expect("contained Everything response");
        assert_eq!(response["status"], "unavailable");
        assert_eq!(response["results"].as_array().map(Vec::len), Some(0));
        assert_eq!(
            serde_json::to_value(store.state()).expect("state JSON"),
            before
        );
        assert!(runtime.open_result("C:\\arbitrary.txt").is_err());
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn raw_everything_prefix_and_declared_mode_cannot_disagree() {
        let mut store = test_store();
        let mut desktop_loaded = true;
        let root = env::temp_dir().join(format!(
            "qing-launcher-everything-mode-{}",
            unique_id("test")
        ));
        let mut runtime = EverythingRuntime::new(root.clone(), root.join("data"));
        let error = handle_method(
            "searchEverything",
            json!({
                "mode": "everything-file",
                "query": "/e:d folder",
                "requestId": "test-2"
            }),
            &mut store,
            &mut desktop_loaded,
            &mut runtime,
        )
        .expect_err("mode mismatch must be rejected");
        assert_eq!(error.0, "invalid_payload");
        let _ = fs::remove_dir_all(root);
    }
}
