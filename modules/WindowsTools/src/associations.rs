//! Persistent user-owned association table. Registration and consent are
//! separate states: preparing a candidate must never be reported as a default.
use crate::association;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{
    collections::{BTreeMap, BTreeSet},
    fs,
    path::{Path, PathBuf},
    sync::{mpsc, Arc, Mutex},
    time::{SystemTime, UNIX_EPOCH},
};

const MAX_ROWS: usize = 128;
const COMMON: &[(&str, &str)] = &[
    (".txt", "文本"),
    (".md", "Markdown"),
    (".pdf", "PDF 文档"),
    (".docx", "Word 文档"),
    (".xlsx", "Excel 表格"),
    (".pptx", "PowerPoint"),
    (".csv", "CSV 表格"),
    (".json", "JSON"),
    (".xml", "XML"),
    (".jpg", "JPEG 图片"),
    (".jpeg", "JPEG 图片"),
    (".png", "PNG 图片"),
    (".webp", "WebP 图片"),
    (".gif", "GIF 图片"),
    (".svg", "SVG 图片"),
    (".mp3", "MP3 音频"),
    (".wav", "WAV 音频"),
    (".mp4", "MP4 视频"),
    (".mkv", "MKV 视频"),
    (".zip", "ZIP 压缩包"),
    (".7z", "7-Zip 压缩包"),
    (".rar", "RAR 压缩包"),
];
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Entry {
    extension: String,
    label: String,
    executable: Option<String>,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Document {
    schema_version: u8,
    rows: Vec<Entry>,
    check_on_startup: bool,
    prepare_on_startup: bool,
}
impl Default for Document {
    fn default() -> Self {
        Self {
            schema_version: 1,
            rows: COMMON
                .iter()
                .map(|(extension, label)| Entry {
                    extension: (*extension).into(),
                    label: (*label).into(),
                    executable: None,
                })
                .collect(),
            check_on_startup: true,
            prepare_on_startup: false,
        }
    }
}
fn path_identity(path: &str) -> String {
    path.trim_start_matches(r"\\?\")
        .replace('/', "\\")
        .to_lowercase()
}
fn same_application(expected: &str, current: Option<&str>) -> bool {
    current.is_some_and(|p| path_identity(p) == path_identity(expected))
}
fn label(value: &str) -> Result<String, String> {
    let value = value.trim();
    if value.chars().count() > 48 || value.chars().any(char::is_control) {
        return Err("类型说明最多 48 个字符，不能包含控制字符。".into());
    }
    Ok(value.to_string())
}
fn saved_path(value: &str) -> bool {
    !value.starts_with("\\\\")
        && value.len() <= 1024
        && !value.chars().any(|ch| ch == '"' || ch.is_control())
        && Path::new(value).is_absolute()
        && Path::new(value)
            .extension()
            .is_some_and(|s| s.eq_ignore_ascii_case("exe"))
}
fn load(path: &Path) -> Result<Document, String> {
    if !path.exists() {
        let mut doc = Document::default();
        for (extension, executable) in association::registered_candidates() {
            if !saved_path(&executable) {
                continue;
            }
            if let Some(row) = doc.rows.iter_mut().find(|r| r.extension == extension) {
                row.executable = Some(executable);
            } else if doc.rows.len() < MAX_ROWS {
                doc.rows.push(Entry {
                    extension,
                    executable: Some(executable),
                    label: "自定义".into(),
                });
            }
        }
        return Ok(doc);
    }
    let bytes = fs::read(path).map_err(|e| format!("无法读取关联表：{e}"))?;
    if bytes.len() > 512 * 1024 {
        return Err("关联表超过大小限制，已保留原文件。".into());
    }
    let mut doc: Document = serde_json::from_slice(&bytes)
        .map_err(|_| "关联表格式无效，已保留原文件；为防止数据丢失，暂不允许覆盖。")?;
    if doc.schema_version != 1 || doc.rows.len() > MAX_ROWS {
        return Err("不支持此关联表版本或条目数量。".into());
    }
    let mut ids = BTreeSet::new();
    for row in &mut doc.rows {
        row.extension = association::extension(&row.extension)?;
        row.label = label(&row.label)?;
        if !ids.insert(row.extension.clone())
            || row.executable.as_deref().is_some_and(|p| !saved_path(p))
        {
            return Err("关联表包含重复后缀或无效程序路径，已保留原文件。".into());
        }
    }
    doc.prepare_on_startup &= doc.check_on_startup;
    Ok(doc)
}
fn save(path: &Path, document: &Document) -> Result<(), String> {
    use std::{io::Write, os::windows::ffi::OsStrExt};
    use windows_sys::Win32::Storage::FileSystem::{
        MoveFileExW, MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH,
    };
    let directory = path.parent().ok_or("模块数据目录无效。")?;
    fs::create_dir_all(directory).map_err(|e| e.to_string())?;
    let temp = directory.join(format!(
        "associations.tmp-{}-{}",
        std::process::id(),
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos()
    ));
    let result = (|| -> Result<(), String> {
        let mut file = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temp)
            .map_err(|e| e.to_string())?;
        file.write_all(&serde_json::to_vec_pretty(document).map_err(|e| e.to_string())?)
            .map_err(|e| e.to_string())?;
        file.sync_all().map_err(|e| e.to_string())?;
        drop(file);
        let from = temp
            .as_os_str()
            .encode_wide()
            .chain(Some(0))
            .collect::<Vec<_>>();
        let to = path
            .as_os_str()
            .encode_wide()
            .chain(Some(0))
            .collect::<Vec<_>>();
        if unsafe {
            MoveFileExW(
                from.as_ptr(),
                to.as_ptr(),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
            )
        } == 0
        {
            return Err(std::io::Error::last_os_error().to_string());
        }
        Ok(())
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temp);
    }
    result.map_err(|e| format!("保存关联表失败：{e}"))
}

#[derive(Clone, Default)]
struct Checked {
    application: Option<String>,
    executable: Option<String>,
    status: String,
    error: Option<String>,
}
#[derive(Clone)]
enum Command {
    Add(Vec<String>, String),
    Common,
    Remove(Vec<String>),
    Target(Vec<String>, String),
    Preferences(bool, bool),
    Check(Vec<String>),
    Register(Vec<String>),
}
struct State {
    document: Option<Document>,
    checked: BTreeMap<String, Checked>,
    busy: bool,
    revision: u64,
    checked_at: Option<u64>,
    message: String,
    error: Option<String>,
    pending: Vec<String>,
}
impl State {
    fn snapshot(&self) -> Value {
        let rows = self.document.as_ref().map(|d| d.rows.iter().map(|entry| {
            let checked = self.checked.get(&entry.extension).cloned().unwrap_or_else(|| Checked { status: if entry.executable.is_some() { "unchecked" } else { "unconfigured" }.into(), ..Checked::default() });
            json!({ "extension":entry.extension,"label":entry.label,"executable":entry.executable,"currentApplication":checked.application,"currentExecutable":checked.executable,"status":checked.status,"error":checked.error })
        }).collect::<Vec<_>>()).unwrap_or_default();
        json!({ "rows":rows,"checkOnStartup":self.document.as_ref().is_some_and(|d| d.check_on_startup),"prepareOnStartup":self.document.as_ref().is_some_and(|d| d.prepare_on_startup),"busy":self.busy,"revision":self.revision,"checkedAt":self.checked_at,"message":self.message,"error":self.error,"pendingConfirmation":self.pending })
    }
}
pub struct Manager {
    state: Arc<Mutex<State>>,
    sender: mpsc::SyncSender<Command>,
}
impl Manager {
    pub fn new(directory: PathBuf) -> Self {
        let state = Arc::new(Mutex::new(State {
            document: None,
            checked: BTreeMap::new(),
            busy: true,
            revision: 0,
            checked_at: None,
            message: String::new(),
            error: None,
            pending: vec![],
        }));
        let (sender, receiver) = mpsc::sync_channel(1);
        let shared = state.clone();
        std::thread::spawn(move || {
            let path = directory.join("associations.json");
            match load(&path) {
                Ok(doc) => {
                    let startup = doc.check_on_startup;
                    let prepare = doc.prepare_on_startup;
                    shared.lock().unwrap_or_else(|e| e.into_inner()).document = Some(doc);
                    if startup {
                        process(&shared, &path, Command::Check(vec![]), prepare);
                    }
                }
                Err(error) => {
                    shared.lock().unwrap_or_else(|e| e.into_inner()).error = Some(error);
                }
            }
            {
                let mut s = shared.lock().unwrap_or_else(|e| e.into_inner());
                s.busy = false;
                s.revision += 1;
            }
            while let Ok(command) = receiver.recv() {
                process(&shared, &path, command, false);
                let mut s = shared.lock().unwrap_or_else(|e| e.into_inner());
                s.busy = false;
                s.revision += 1;
            }
        });
        Self { state, sender }
    }
    pub fn snapshot(&self) -> Value {
        self.state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .snapshot()
    }
    pub fn invoke(&self, method: &str, payload: &Value) -> Result<Value, String> {
        fn strings(
            payload: &Value,
            name: &str,
            empty_allowed: bool,
        ) -> Result<Vec<String>, String> {
            let values = payload
                .get(name)
                .and_then(Value::as_array)
                .ok_or("缺少后缀列表。")?;
            if values.len() > MAX_ROWS || (!empty_allowed && values.is_empty()) {
                return Err("请选择 1–128 个后缀。".into());
            }
            values
                .iter()
                .map(|v| association::extension(v.as_str().ok_or("后缀必须是文本。")?))
                .collect::<Result<BTreeSet<_>, _>>()
                .map(|s| s.into_iter().collect())
        }
        let command = match method {
            "addAssociationRows" => Command::Add(
                strings(payload, "extensions", false)?,
                label(payload["label"].as_str().unwrap_or("自定义"))?,
            ),
            "addCommonAssociations" => Command::Common,
            "removeAssociationRows" => Command::Remove(strings(payload, "extensions", false)?),
            "setAssociationTarget" => Command::Target(
                strings(payload, "extensions", false)?,
                payload["executable"]
                    .as_str()
                    .ok_or("请选择程序。")?
                    .to_string(),
            ),
            "setAssociationPreferences" => {
                let check = payload["checkOnStartup"]
                    .as_bool()
                    .ok_or("启动检查选项无效。")?;
                let prepare = payload["prepareOnStartup"]
                    .as_bool()
                    .ok_or("自动准备选项无效。")?;
                Command::Preferences(check, check && prepare)
            }
            "checkAssociations" => Command::Check(strings(payload, "extensions", true)?),
            "registerAssociationBatch" => Command::Register(strings(payload, "extensions", false)?),
            _ => return Err("未知的关联表操作。".into()),
        };
        let mut state = self.state.lock().unwrap_or_else(|e| e.into_inner());
        if state.document.is_none() {
            return Err(state.error.clone().unwrap_or("正在载入关联表。".into()));
        }
        if state.busy {
            return Err("关联表正在处理，请稍后重试。".into());
        }
        state.busy = true;
        state.error = None;
        state.message.clear();
        if self.sender.try_send(command).is_err() {
            state.busy = false;
            return Err("关联表后台任务不可用。".into());
        }
        Ok(state.snapshot())
    }
}

fn selection(
    doc: &Document,
    extensions: &[String],
    all_if_empty: bool,
) -> Result<Vec<Entry>, String> {
    if all_if_empty && extensions.is_empty() {
        return Ok(doc.rows.clone());
    }
    extensions
        .iter()
        .map(|ext| {
            doc.rows
                .iter()
                .find(|e| &e.extension == ext)
                .cloned()
                .ok_or_else(|| format!("后缀 {ext} 已不存在，请刷新。"))
        })
        .collect()
}
fn check(entry: &Entry, inspect: &impl Fn(&str) -> Result<Value, String>) -> Checked {
    let value = match inspect(&entry.extension) {
        Ok(v) => v,
        Err(error) => {
            return Checked {
                status: "unknown".into(),
                error: Some(error),
                ..Checked::default()
            }
        }
    };
    let application = value["application"].as_str().map(str::to_string);
    let executable = value["executable"].as_str().map(str::to_string);
    let status = match &entry.executable {
        None => "unconfigured",
        Some(target) if !Path::new(target).is_file() => "missingTarget",
        Some(target) if same_application(target, executable.as_deref()) => "matched",
        Some(_) if executable.is_some() => "changed",
        Some(_) if application.is_none() => "unassigned",
        Some(_) => "unknown",
    }
    .to_string();
    Checked {
        application,
        executable,
        status,
        error: None,
    }
}
fn process(shared: &Arc<Mutex<State>>, path: &Path, command: Command, prepare: bool) {
    process_with(
        shared,
        path,
        command,
        prepare,
        &association::inspect,
        &association::register,
    );
}
fn process_with(
    shared: &Arc<Mutex<State>>,
    path: &Path,
    command: Command,
    prepare: bool,
    inspect: &impl Fn(&str) -> Result<Value, String>,
    register_candidate: &impl Fn(&str, &str) -> Result<Value, String>,
) {
    let result = (|| -> Result<(), String> {
        let mut doc = shared
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .document
            .clone()
            .ok_or("关联表不可用。")?;
        let mut register = false;
        let mut checking = false;
        let mut message = String::new();
        let entries = match command {
            Command::Check(ids) => {
                checking = true;
                register = prepare;
                selection(&doc, &ids, true)?
            }
            Command::Register(ids) => {
                checking = true;
                register = true;
                selection(&doc, &ids, false)?
            }
            Command::Target(ids, executable) => {
                association::validate_executable(&executable)?;
                selection(&doc, &ids, false)?;
                for row in &mut doc.rows {
                    if ids.contains(&row.extension) {
                        row.executable = Some(executable.clone());
                    }
                }
                message = format!("已为 {} 项保存目标程序，尚未修改系统默认应用。", ids.len());
                save(path, &doc)?;
                selection(&doc, &ids, false)?
            }
            Command::Add(ids, text) => {
                for id in ids {
                    if !doc.rows.iter().any(|e| e.extension == id) {
                        doc.rows.push(Entry {
                            extension: id,
                            label: text.clone(),
                            executable: None,
                        });
                    }
                }
                if doc.rows.len() > MAX_ROWS {
                    return Err("最多管理 128 个后缀。".into());
                }
                save(path, &doc)?;
                message = "后缀已添加，重复项已跳过。".into();
                vec![]
            }
            Command::Common => {
                for (ext, text) in COMMON {
                    if !doc.rows.iter().any(|e| e.extension == *ext) {
                        doc.rows.push(Entry {
                            extension: (*ext).into(),
                            label: (*text).into(),
                            executable: None,
                        });
                    }
                }
                if doc.rows.len() > MAX_ROWS {
                    return Err("加入常用格式后将超过 128 项，请先移除不需要的项。".into());
                }
                save(path, &doc)?;
                message = "常用格式已补充，原有目标程序未更改。".into();
                vec![]
            }
            Command::Remove(ids) => {
                selection(&doc, &ids, false)?;
                doc.rows.retain(|r| !ids.contains(&r.extension));
                save(path, &doc)?;
                message = "已从管理表移除，不改变现有默认应用或其打开能力。".into();
                vec![]
            }
            Command::Preferences(check, prepare) => {
                doc.check_on_startup = check;
                doc.prepare_on_startup = prepare;
                save(path, &doc)?;
                message = "启动检查设置已保存；不会静默抢占默认应用或反复弹出系统设置。".into();
                vec![]
            }
        };
        let mut checked = BTreeMap::new();
        let mut pending = Vec::new();
        let mut failures = 0;
        for entry in entries {
            let mut result = check(&entry, inspect);
            let should_register = register
                && entry.executable.is_some()
                && result.status != "matched"
                && (!prepare || matches!(result.status.as_str(), "changed" | "unassigned"));
            if should_register {
                match register_candidate(&entry.extension, entry.executable.as_ref().unwrap()) {
                    Ok(_) => {
                        result.status = "needsConfirmation".into();
                        pending.push(entry.extension.clone());
                    }
                    Err(error) => {
                        result.error = Some(error);
                        failures += 1;
                    }
                }
            } else if register && entry.executable.is_none() && !prepare {
                result.error = Some("尚未选择目标应用。".into());
                failures += 1;
            }
            checked.insert(entry.extension, result);
        }
        if register {
            message = if pending.is_empty() && failures == 0 {
                "检查完成：已有目标的条目无需恢复。".into()
            } else {
                format!(
                    "已为 {} 项准备恢复，{} 项未能处理。默认应用尚未改变，请在 Windows 中确认。",
                    pending.len(),
                    failures
                )
            };
        } else if checking {
            message = "检查完成。状态来自系统实际关联，保存目标不代表已成为默认。".into();
        }
        let mut state = shared.lock().unwrap_or_else(|e| e.into_inner());
        state.document = Some(doc);
        // Rechecking or changing one target only replaces that row's pending
        // state; unrelated prepared rows must not disappear from the reminder.
        state.pending.retain(|id| !checked.contains_key(id));
        state.pending.extend(pending);
        state.checked.extend(checked);
        state.message = message;
        if checking {
            state.checked_at = Some(
                SystemTime::now()
                    .duration_since(UNIX_EPOCH)
                    .unwrap_or_default()
                    .as_secs(),
            );
        }
        let existing = state
            .document
            .as_ref()
            .unwrap()
            .rows
            .iter()
            .map(|r| r.extension.clone())
            .collect::<BTreeSet<_>>();
        state.checked.retain(|key, _| existing.contains(key));
        state.pending.retain(|key| existing.contains(key));
        Ok(())
    })();
    if let Err(error) = result {
        shared.lock().unwrap_or_else(|e| e.into_inner()).error = Some(error);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn defaults_are_bounded_and_normalized() {
        let doc = Document::default();
        assert_eq!(doc.rows.len(), 22);
        assert!(!doc.prepare_on_startup);
        let ids = doc
            .rows
            .iter()
            .map(|e| association::extension(&e.extension).unwrap())
            .collect::<BTreeSet<_>>();
        assert_eq!(ids.len(), doc.rows.len());
    }
    #[test]
    fn matching_uses_full_executable_not_display_name() {
        assert!(same_application(
            r"C:\Apps\Reader.exe",
            Some(r"c:/apps/READER.exe")
        ));
        assert!(!same_application(
            r"C:\Apps\Reader.exe",
            Some(r"D:\Other\Reader.exe")
        ));
        assert!(!same_application(r"C:\Apps\Reader.exe", None));
    }
    #[test]
    fn selection_never_accepts_unknown_or_arbitrary_paths() {
        let doc = Document::default();
        assert!(selection(&doc, &[".not-in-table".into()], false).is_err());
        assert!(!saved_path(r"\\server\share\app.exe"));
        assert!(!saved_path("C:\\App.exe\" arg"));
        assert!(selection(&doc, &[], true).unwrap().len() == 22);
    }
    #[test]
    fn atomic_roundtrip_and_corruption_preserve_file() {
        let dir = std::env::temp_dir().join(format!(
            "qing-association-test-{}",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let path = dir.join("associations.json");
        let mut doc = Document::default();
        save(&path, &doc).unwrap();
        doc.rows[0].executable = Some(r"C:\Missing\Reader.exe".into());
        doc.prepare_on_startup = true;
        save(&path, &doc).unwrap();
        let next = load(&path).unwrap();
        assert!(next.prepare_on_startup);
        assert_eq!(next.rows[0].executable, doc.rows[0].executable);
        fs::write(&path, b"broken").unwrap();
        assert!(load(&path).is_err());
        assert_eq!(fs::read(&path).unwrap(), b"broken");
        fs::remove_dir_all(dir).unwrap();
    }

    fn fixture() -> (Arc<Mutex<State>>, PathBuf, String) {
        let directory = std::env::temp_dir().join(format!(
            "qing-table-test-{}",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let executable = std::env::current_exe()
            .unwrap()
            .to_string_lossy()
            .to_string();
        let rows = [".pdf", ".txt", ".md", ".docx"]
            .into_iter()
            .map(|extension| Entry {
                extension: extension.into(),
                label: "测试".into(),
                executable: Some(if extension == ".docx" {
                    directory.join("missing.exe").to_string_lossy().to_string()
                } else {
                    executable.clone()
                }),
            })
            .collect();
        let state = Arc::new(Mutex::new(State {
            document: Some(Document {
                rows,
                schema_version: 1,
                check_on_startup: true,
                prepare_on_startup: true,
            }),
            checked: BTreeMap::new(),
            busy: false,
            revision: 0,
            checked_at: None,
            message: String::new(),
            error: None,
            pending: vec![],
        }));
        (state, directory.join("associations.json"), executable)
    }

    #[test]
    fn startup_prepares_only_verified_drift_and_never_claims_default_changed() {
        let (state, path, executable) = fixture();
        let registered = std::cell::RefCell::new(Vec::new());
        process_with(
            &state,
            &path,
            Command::Check(vec![]),
            true,
            &|ext| match ext {
                ".txt" => Ok(json!({"application":"Reader", "executable":executable})),
                ".md" => Err("query unavailable".into()),
                _ => {
                    Ok(json!({"application":"Other Reader", "executable":"C:\\Other\\Reader.exe"}))
                }
            },
            &|ext, _| {
                registered.borrow_mut().push(ext.to_string());
                Ok(json!({}))
            },
        );
        assert_eq!(*registered.borrow(), vec![".pdf"]);
        let snapshot = state.lock().unwrap().snapshot();
        assert_eq!(snapshot["pendingConfirmation"], json!([".pdf"]));
        let statuses = snapshot["rows"]
            .as_array()
            .unwrap()
            .iter()
            .map(|r| {
                (
                    r["extension"].as_str().unwrap(),
                    r["status"].as_str().unwrap(),
                )
            })
            .collect::<BTreeMap<_, _>>();
        assert_eq!(statuses[".pdf"], "needsConfirmation");
        assert_eq!(statuses[".txt"], "matched");
        assert_eq!(statuses[".md"], "unknown");
        assert_eq!(statuses[".docx"], "missingTarget");
        assert!(snapshot["message"]
            .as_str()
            .unwrap()
            .contains("默认应用尚未改变"));
        assert!(!path.exists(), "startup check does not rewrite the table");
    }

    #[test]
    fn batch_tracks_partial_failures_and_preserves_other_pending_rows() {
        let (state, path, _) = fixture();
        state.lock().unwrap().pending = vec![".md".into()];
        process_with(
            &state,
            &path,
            Command::Register(vec![".pdf".into(), ".txt".into()]),
            false,
            &|_| Ok(json!({"application":"Other", "executable":"C:\\Other.exe"})),
            &|ext, _| {
                if ext == ".txt" {
                    Err("registration denied".into())
                } else {
                    Ok(json!({}))
                }
            },
        );
        let snapshot = state.lock().unwrap().snapshot();
        assert_eq!(snapshot["pendingConfirmation"], json!([".md", ".pdf"]));
        assert_eq!(snapshot["rows"][1]["error"], "registration denied");
        assert!(snapshot["message"]
            .as_str()
            .unwrap()
            .contains("1 项未能处理"));
    }

    #[test]
    fn changing_target_clears_only_that_rows_old_confirmation() {
        let (state, path, executable) = fixture();
        state.lock().unwrap().pending = vec![".pdf".into(), ".txt".into()];
        process_with(
            &state,
            &path,
            Command::Target(vec![".pdf".into()], executable.clone()),
            false,
            &|_| Ok(json!({"application":"Target", "executable":executable})),
            &|_, _| panic!("saving a target must not register or change associations"),
        );
        let snapshot = state.lock().unwrap().snapshot();
        assert!(snapshot["error"].is_null());
        assert_eq!(snapshot["pendingConfirmation"], json!([".txt"]));
        assert_eq!(load(&path).unwrap().rows[0].executable, Some(executable));
        fs::remove_dir_all(path.parent().unwrap()).unwrap();
    }
}
