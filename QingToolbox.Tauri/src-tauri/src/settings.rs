use std::{
    collections::BTreeMap,
    fs, io,
    path::{Path, PathBuf},
    time::{SystemTime, UNIX_EPOCH},
};

use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::{fonts, paths::settings_path};

const SETTINGS_SCHEMA_VERSION: u32 = 1;
const MAX_SETTINGS_BYTES: u64 = 1024 * 1024;
const MAX_RECENT_MODULES: usize = 10;
const MAX_STARTUP_MODULES: usize = 32;
const MAX_STRING_LENGTH: usize = 128;
const MAX_HOTKEY_LENGTH: usize = 64;
const MAX_CORRUPT_BACKUPS: usize = 3;

/// The small public settings surface shared by the Rust host and Vue shell.
/// It deliberately contains preferences, not filesystem paths or executable
/// commands. Additional host capabilities should be added as typed fields
/// rather than passing an unbounded JSON object through the bridge.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsSnapshot {
    pub settings_schema_version: u32,
    pub language: String,
    pub appearance_preset_id: String,
    pub font_id: String,
    pub font_source: String,
    pub font_family_name: Option<String>,
    pub fonts: Vec<fonts::FontOption>,
    pub close_behavior: String,
    pub startup_presentation: String,
    pub toggle_hotkey: String,
    pub launch_at_login: bool,
    pub show_logs_in_sidebar: bool,
    pub recent_module_ids: Vec<String>,
    pub startup_module_ids: Vec<String>,
}

/// A partial update accepted by the typed Tauri command. Unknown JSON fields
/// are rejected at the command boundary, while every known value is
/// normalized below.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SettingsUpdate {
    pub language: Option<String>,
    pub appearance_preset_id: Option<String>,
    pub font_id: Option<String>,
    pub close_behavior: Option<String>,
    pub startup_presentation: Option<String>,
    pub toggle_hotkey: Option<String>,
    pub launch_at_login: Option<bool>,
    pub show_logs_in_sidebar: Option<bool>,
    pub recent_module_ids: Option<Vec<String>>,
    pub startup_module_ids: Option<Vec<String>>,
}

#[derive(Debug, Clone)]
struct Settings {
    language: String,
    appearance_preset_id: String,
    font_id: String,
    font_source: String,
    font_family_name: Option<String>,
    close_behavior: String,
    startup_presentation: String,
    toggle_hotkey: String,
    launch_at_login: bool,
    show_logs_in_sidebar: bool,
    recent_module_ids: Vec<String>,
    startup_module_ids: Vec<String>,
    /// Preserve settings owned by a newer/legacy host when this migration
    /// writes the shared document. They are never exposed to Vue.
    extra: BTreeMap<String, Value>,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            language: "system".to_string(),
            appearance_preset_id: "qing-default".to_string(),
            font_id: "Default".to_string(),
            font_source: "default".to_string(),
            font_family_name: None,
            close_behavior: "ask".to_string(),
            // A first launch of the standalone Tauri host should be visible.
            // Existing legacy settings still map FloatingBadge to `tray`.
            startup_presentation: "main".to_string(),
            toggle_hotkey: "Ctrl+Alt+Space".to_string(),
            launch_at_login: false,
            show_logs_in_sidebar: false,
            recent_module_ids: Vec::new(),
            startup_module_ids: Vec::new(),
            extra: BTreeMap::new(),
        }
    }
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
struct SettingsDocument {
    #[serde(alias = "SettingsSchemaVersion")]
    settings_schema_version: Option<u32>,
    #[serde(alias = "Language")]
    language: Option<String>,
    #[serde(alias = "AppearancePresetId")]
    appearance_preset_id: Option<String>,
    font_id: Option<String>,
    font_source: Option<String>,
    font_family_name: Option<String>,
    #[serde(alias = "MainWindowCloseBehavior")]
    close_behavior: Option<Value>,
    #[serde(alias = "StartupPresentationMode")]
    startup_presentation: Option<Value>,
    #[serde(alias = "ToggleHotkey", alias = "GlobalHotkey")]
    toggle_hotkey: Option<String>,
    #[serde(alias = "LaunchAtLogin")]
    launch_at_login: Option<bool>,
    #[serde(alias = "ShowLogsInSidebar")]
    show_logs_in_sidebar: Option<bool>,
    #[serde(alias = "RecentModuleIds")]
    recent_module_ids: Option<Vec<String>>,
    #[serde(alias = "StartupModuleIds")]
    startup_module_ids: Option<Vec<String>>,
    #[serde(flatten)]
    extra: BTreeMap<String, Value>,
}

impl From<&Settings> for SettingsDocument {
    fn from(settings: &Settings) -> Self {
        Self {
            settings_schema_version: Some(SETTINGS_SCHEMA_VERSION),
            language: Some(settings.language.clone()),
            appearance_preset_id: Some(settings.appearance_preset_id.clone()),
            font_id: Some(settings.font_id.clone()),
            font_source: Some(settings.font_source.clone()),
            font_family_name: settings.font_family_name.clone(),
            close_behavior: Some(Value::String(settings.close_behavior.clone())),
            startup_presentation: Some(Value::String(settings.startup_presentation.clone())),
            toggle_hotkey: Some(settings.toggle_hotkey.clone()),
            launch_at_login: Some(settings.launch_at_login),
            show_logs_in_sidebar: Some(settings.show_logs_in_sidebar),
            recent_module_ids: Some(settings.recent_module_ids.clone()),
            startup_module_ids: Some(settings.startup_module_ids.clone()),
            extra: settings.extra.clone(),
        }
    }
}

#[derive(Debug)]
pub struct SettingsError {
    pub code: &'static str,
    pub message: String,
}

impl std::fmt::Display for SettingsError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl std::error::Error for SettingsError {}

/// Owns the settings document and serializes updates through the HostState
/// mutex. The path is resolved by Rust; the frontend never chooses it.
pub struct SettingsStore {
    path: Option<PathBuf>,
    settings: Settings,
}

impl SettingsStore {
    pub fn new() -> Self {
        Self::from_path(settings_path())
    }

    fn from_path(path: Option<PathBuf>) -> Self {
        let settings = path
            .as_deref()
            .and_then(read_document)
            .map(|document| settings_from_document(&document))
            .unwrap_or_default();
        Self { path, settings }
    }

    pub fn snapshot(&self) -> SettingsSnapshot {
        SettingsSnapshot {
            settings_schema_version: SETTINGS_SCHEMA_VERSION,
            language: self.settings.language.clone(),
            appearance_preset_id: self.settings.appearance_preset_id.clone(),
            font_id: self.settings.font_id.clone(),
            font_source: self.settings.font_source.clone(),
            font_family_name: self.settings.font_family_name.clone(),
            fonts: fonts::catalog(),
            close_behavior: self.settings.close_behavior.clone(),
            startup_presentation: self.settings.startup_presentation.clone(),
            toggle_hotkey: self.settings.toggle_hotkey.clone(),
            launch_at_login: self.settings.launch_at_login,
            show_logs_in_sidebar: self.settings.show_logs_in_sidebar,
            recent_module_ids: self.settings.recent_module_ids.clone(),
            startup_module_ids: self.settings.startup_module_ids.clone(),
        }
    }

    pub fn update(&mut self, update: SettingsUpdate) -> Result<SettingsSnapshot, SettingsError> {
        let mut candidate = self.settings.clone();
        if let Some(value) = update.language {
            candidate.language = normalize_language(&value);
        }
        if let Some(value) = update.appearance_preset_id {
            candidate.appearance_preset_id = normalize_appearance(&value);
        }
        if let Some(value) = update.font_id {
            let selection = fonts::normalize_selection(Some(&value), None, None);
            candidate.font_id = selection.id;
            candidate.font_source = selection.source;
            candidate.font_family_name = selection.family_name;
        }
        if let Some(value) = update.close_behavior {
            candidate.close_behavior = normalize_close_behavior(Some(&Value::String(value)));
        }
        if let Some(value) = update.startup_presentation {
            candidate.startup_presentation =
                normalize_startup_presentation(Some(&Value::String(value)));
        }
        if let Some(value) = update.toggle_hotkey {
            candidate.toggle_hotkey = normalize_hotkey(&value);
        }
        if let Some(value) = update.launch_at_login {
            candidate.launch_at_login = value;
        }
        if let Some(value) = update.show_logs_in_sidebar {
            candidate.show_logs_in_sidebar = value;
        }
        if let Some(values) = update.recent_module_ids {
            candidate.recent_module_ids = normalize_recent(values);
        }
        if let Some(values) = update.startup_module_ids {
            candidate.startup_module_ids = normalize_module_ids(values);
        }

        if let Some(path) = self.path.as_deref() {
            write_document(path, &SettingsDocument::from(&candidate)).map_err(|error| {
                SettingsError {
                    code: "settingsWriteFailed",
                    message: format!("无法保存工具箱设置：{error}"),
                }
            })?;
        } else {
            return Err(SettingsError {
                code: "settingsPathUnavailable",
                message: "工具箱设置路径不可用。".to_string(),
            });
        }
        self.settings = candidate;
        Ok(self.snapshot())
    }
}

impl Default for SettingsStore {
    fn default() -> Self {
        Self::new()
    }
}

fn read_document(path: &Path) -> Option<SettingsDocument> {
    let metadata = fs::metadata(path).ok()?;
    if metadata.len() == 0 || metadata.len() > MAX_SETTINGS_BYTES {
        preserve_corrupt(path);
        return None;
    }
    let bytes = fs::read(path).ok()?;
    match serde_json::from_slice::<SettingsDocument>(&bytes) {
        Ok(document) => Some(document),
        Err(_) => {
            preserve_corrupt(path);
            None
        }
    }
}

fn settings_from_document(document: &SettingsDocument) -> Settings {
    let legacy_font_id = document.extra.get("FontId").and_then(Value::as_str);
    let legacy_font_source = document.extra.get("FontSource").and_then(Value::as_str);
    let legacy_font_family = document.extra.get("FontFamilyName").and_then(Value::as_str);
    let font = fonts::normalize_selection(
        document.font_id.as_deref().or(legacy_font_id),
        document.font_source.as_deref().or(legacy_font_source),
        document.font_family_name.as_deref().or(legacy_font_family),
    );
    let mut settings = Settings {
        language: normalize_language(document.language.as_deref().unwrap_or_default()),
        appearance_preset_id: normalize_appearance(
            document.appearance_preset_id.as_deref().unwrap_or_default(),
        ),
        font_id: font.id,
        font_source: font.source,
        font_family_name: font.family_name,
        close_behavior: normalize_close_behavior(document.close_behavior.as_ref()),
        startup_presentation: normalize_startup_presentation(
            document.startup_presentation.as_ref(),
        ),
        toggle_hotkey: normalize_hotkey(document.toggle_hotkey.as_deref().unwrap_or_default()),
        launch_at_login: document.launch_at_login.unwrap_or(false),
        show_logs_in_sidebar: document.show_logs_in_sidebar.unwrap_or(false),
        recent_module_ids: normalize_recent(document.recent_module_ids.clone().unwrap_or_default()),
        startup_module_ids: normalize_module_ids(
            document.startup_module_ids.clone().unwrap_or_default(),
        ),
        extra: document.extra.clone(),
    };
    // A future schema may add fields, but old settings remain readable. Keep
    // the version internal until a migration needs to expose it explicitly.
    let _ = document.settings_schema_version;
    settings.language = truncate(settings.language);
    settings.appearance_preset_id = truncate(settings.appearance_preset_id);
    settings
}

fn normalize_language(value: &str) -> String {
    match value.trim() {
        "zh-CN" | "en-US" | "system" => value.trim().to_string(),
        _ => "system".to_string(),
    }
}

fn normalize_appearance(value: &str) -> String {
    match value.trim() {
        "qing-default" | "neon-circuit" | "greenline" | "aurora-flow" | "qing-nova" | "system"
        | "light" | "dark" => value.trim().to_string(),
        _ => "qing-default".to_string(),
    }
}

fn normalize_close_behavior(value: Option<&Value>) -> String {
    match value_string_or_number(value).as_deref() {
        Some("ask") | Some("Ask") | Some("0") => "ask".to_string(),
        Some("tray") | Some("MinimizeToNotificationArea") | Some("1") => "tray".to_string(),
        Some("exit") | Some("ExitApplication") | Some("2") => "exit".to_string(),
        _ => "ask".to_string(),
    }
}

fn normalize_startup_presentation(value: Option<&Value>) -> String {
    match value_string_or_number(value).as_deref() {
        Some("main") | Some("MainWindow") | Some("0") => "main".to_string(),
        Some("minimized") | Some("Minimized") | Some("1") => "minimized".to_string(),
        Some("tray") | Some("FloatingBadge") | Some("2") => "tray".to_string(),
        _ => "main".to_string(),
    }
}

fn normalize_hotkey(value: &str) -> String {
    let normalized = value.trim();
    if !valid_hotkey_shape(normalized) {
        return "Ctrl+Alt+Space".to_string();
    }
    normalized.chars().take(MAX_HOTKEY_LENGTH).collect()
}

fn valid_hotkey_shape(value: &str) -> bool {
    let tokens = value.split('+').map(str::trim).collect::<Vec<_>>();
    if tokens.len() < 2 || tokens.iter().any(|token| token.is_empty()) {
        return false;
    }
    let mut has_modifier = false;
    for token in &tokens[..tokens.len() - 1] {
        if matches!(
            token.to_ascii_lowercase().as_str(),
            "ctrl"
                | "control"
                | "alt"
                | "option"
                | "shift"
                | "win"
                | "super"
                | "command"
                | "cmd"
                | "commandorcontrol"
                | "cmdorcontrol"
        ) {
            has_modifier = true;
        } else {
            return false;
        }
    }
    has_modifier && tokens[tokens.len() - 1].chars().count() <= 32
}

fn value_string_or_number(value: Option<&Value>) -> Option<String> {
    match value? {
        Value::String(value) => Some(value.trim().chars().take(MAX_STRING_LENGTH).collect()),
        Value::Number(value) => Some(value.to_string()),
        _ => None,
    }
}

fn normalize_recent(values: Vec<String>) -> Vec<String> {
    let mut seen = std::collections::BTreeSet::new();
    values
        .into_iter()
        .map(|value| {
            value
                .trim()
                .chars()
                .take(MAX_STRING_LENGTH)
                .collect::<String>()
        })
        .filter(|value| !value.is_empty())
        .filter(|value| seen.insert(value.clone()))
        .take(MAX_RECENT_MODULES)
        .collect()
}

fn normalize_module_ids(values: Vec<String>) -> Vec<String> {
    let mut seen = std::collections::BTreeSet::new();
    values
        .into_iter()
        .map(|value| {
            value
                .trim()
                .chars()
                .take(MAX_STRING_LENGTH)
                .collect::<String>()
        })
        .filter(|value| {
            !value.is_empty()
                && value
                    .bytes()
                    .next()
                    .is_some_and(|byte| byte.is_ascii_alphanumeric())
                && value
                    .bytes()
                    .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-' | b'_'))
        })
        .filter(|value| seen.insert(value.clone()))
        .take(MAX_STARTUP_MODULES)
        .collect()
}

fn truncate(value: String) -> String {
    value.chars().take(MAX_STRING_LENGTH).collect()
}

fn write_document(path: &Path, document: &SettingsDocument) -> io::Result<()> {
    let directory = path.parent().ok_or_else(|| {
        io::Error::new(io::ErrorKind::InvalidInput, "settings path has no parent")
    })?;
    fs::create_dir_all(directory)?;
    let bytes =
        serde_json::to_vec_pretty(document).map_err(|error| io::Error::other(error.to_string()))?;
    if bytes.len() as u64 > MAX_SETTINGS_BYTES {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "settings document is too large",
        ));
    }
    let temporary = directory.join(format!(".settings.json.tmp-{}", unique_suffix()));
    fs::write(&temporary, bytes)?;
    replace_file(&temporary, path)
}

fn replace_file(temporary: &Path, destination: &Path) -> io::Result<()> {
    if destination.exists() {
        let backup = destination.with_extension("json.bak");
        let _ = fs::remove_file(&backup);
        fs::rename(destination, &backup)?;
        if let Err(error) = fs::rename(temporary, destination) {
            let _ = fs::rename(&backup, destination);
            let _ = fs::remove_file(temporary);
            return Err(error);
        }
        let _ = fs::remove_file(backup);
    } else if let Err(error) = fs::rename(temporary, destination) {
        let _ = fs::remove_file(temporary);
        return Err(error);
    }
    Ok(())
}

fn preserve_corrupt(path: &Path) {
    if !path.is_file() {
        return;
    }
    let Some(directory) = path.parent() else {
        return;
    };
    let backup = directory.join(format!(
        "settings.corrupt-{}-{}.json",
        unique_suffix(),
        std::process::id()
    ));
    if fs::rename(path, backup).is_err() {
        return;
    }
    let mut backups = match fs::read_dir(directory) {
        Ok(entries) => entries
            .flatten()
            .filter_map(|entry| {
                let name = entry.file_name().to_string_lossy().to_string();
                name.starts_with("settings.corrupt-")
                    .then_some((entry.path(), entry.metadata().ok()?.modified().ok()?))
            })
            .collect::<Vec<_>>(),
        Err(_) => return,
    };
    backups.sort_by_key(|left| std::cmp::Reverse(left.1));
    for (stale, _) in backups.into_iter().skip(MAX_CORRUPT_BACKUPS) {
        let _ = fs::remove_file(stale);
    }
}

fn unique_suffix() -> String {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|value| format!("{:x}", value.as_nanos()))
        .unwrap_or_else(|_| "0".to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn test_path(label: &str) -> (PathBuf, PathBuf) {
        let directory =
            std::env::temp_dir().join(format!("qing-settings-{label}-{}", unique_suffix()));
        (directory.clone(), directory.join("settings.json"))
    }

    #[test]
    fn legacy_enum_values_are_normalized_without_losing_preferences() {
        let document = SettingsDocument {
            settings_schema_version: Some(9),
            language: Some("zh-CN".to_string()),
            appearance_preset_id: Some("neon-circuit".to_string()),
            font_id: None,
            font_source: None,
            font_family_name: None,
            close_behavior: Some(Value::Number(1.into())),
            startup_presentation: Some(Value::String("MainWindow".to_string())),
            toggle_hotkey: Some("Ctrl+Alt+Space".to_string()),
            launch_at_login: Some(true),
            show_logs_in_sidebar: Some(true),
            recent_module_ids: Some(vec!["a".to_string(), "a".to_string(), "b".to_string()]),
            startup_module_ids: Some(vec![
                "qing.launcher".to_string(),
                "qing.launcher".to_string(),
                "bad/id".to_string(),
            ]),
            extra: BTreeMap::new(),
        };
        let settings = settings_from_document(&document);
        assert_eq!(settings.language, "zh-CN");
        assert_eq!(settings.appearance_preset_id, "neon-circuit");
        assert_eq!(settings.close_behavior, "tray");
        assert_eq!(settings.startup_presentation, "main");
        assert_eq!(settings.recent_module_ids, vec!["a", "b"]);
        assert_eq!(settings.startup_module_ids, vec!["qing.launcher"]);
    }

    #[test]
    fn update_is_atomic_and_readable_by_a_new_store() {
        let (directory, path) = test_path("atomic");
        let mut store = SettingsStore::from_path(Some(path.clone()));
        let snapshot = store
            .update(SettingsUpdate {
                language: Some("en-US".to_string()),
                appearance_preset_id: Some("dark".to_string()),
                ..SettingsUpdate::default()
            })
            .expect("settings update");
        assert_eq!(snapshot.language, "en-US");
        let reloaded = SettingsStore::from_path(Some(path));
        assert_eq!(reloaded.snapshot().appearance_preset_id, "dark");
        let _ = fs::remove_dir_all(directory);
    }

    #[test]
    fn corrupt_settings_are_moved_as_a_bounded_backup() {
        let (directory, path) = test_path("corrupt");
        fs::create_dir_all(&directory).expect("settings directory");
        fs::write(&path, b"not-json").expect("corrupt settings");
        let store = SettingsStore::from_path(Some(path.clone()));
        assert_eq!(store.snapshot().language, "system");
        assert!(!path.exists());
        assert_eq!(
            fs::read_dir(&directory).expect("backup directory").count(),
            1
        );
        let _ = fs::remove_dir_all(directory);
    }

    #[test]
    fn updates_preserve_fields_owned_by_the_legacy_host() {
        let (directory, path) = test_path("preserve");
        fs::create_dir_all(&directory).expect("settings directory");
        fs::write(
            &path,
            br#"{"Language":"zh-CN","FontId":"system:Segoe UI","Unrelated":{"keep":true}}"#,
        )
        .expect("legacy settings");
        let mut store = SettingsStore::from_path(Some(path.clone()));
        store
            .update(SettingsUpdate {
                close_behavior: Some("tray".to_string()),
                ..SettingsUpdate::default()
            })
            .expect("settings update");
        let bytes = fs::read_to_string(&path).expect("saved settings");
        assert!(bytes.contains("FontId"));
        assert!(bytes.contains("Unrelated"));
        assert!(bytes.contains("\"language\": \"zh-CN\""));
        let _ = fs::remove_dir_all(directory);
    }

    #[test]
    fn hotkey_preference_is_bounded_and_defaults_when_empty() {
        assert_eq!(normalize_hotkey(""), "Ctrl+Alt+Space");
        assert_eq!(normalize_hotkey("Space"), "Ctrl+Alt+Space");
        assert_eq!(normalize_hotkey("Ctrl++Space"), "Ctrl+Alt+Space");
        let long = "Ctrl+Alt+".to_string() + &"K".repeat(200);
        assert_eq!(normalize_hotkey(&long), "Ctrl+Alt+Space");
    }
}
