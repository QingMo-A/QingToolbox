use crate::{engines, model::*};
use serde::Deserialize;
use serde_json::{json, Value};
use std::{
    env, fs,
    io::{BufRead, BufReader, Read},
    path::{Path, PathBuf},
    process::{Command, Stdio},
    sync::{
        atomic::{AtomicBool, AtomicU64, Ordering},
        mpsc, Arc, Mutex,
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

const MAX_FILES: usize = 100;
static NEXT: AtomicU64 = AtomicU64::new(1);
fn unique_id() -> String {
    format!(
        "{:x}-{}",
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    )
}
fn busy_error() -> ConvertError {
    ConvertError::new("busy", "请先完成或取消当前转换。")
}
fn payload<T: serde::de::DeserializeOwned>(value: Value) -> Result<T, ConvertError> {
    serde_json::from_value(value).map_err(|_| ConvertError::new("invalidPayload", "操作参数无效。"))
}

pub struct App {
    state: Arc<Mutex<State>>,
    cancel: Arc<AtomicBool>,
    module: PathBuf,
    data: Option<PathBuf>,
    worker: Option<JoinHandle<()>>,
}
impl App {
    pub fn new(module: PathBuf, data: Option<PathBuf>) -> Self {
        let settings = data
            .as_ref()
            .and_then(|p| fs::read(p.join("converter-settings.json")).ok())
            .filter(|bytes| bytes.len() <= 65536)
            .and_then(|v| serde_json::from_slice::<Settings>(&v).ok())
            .unwrap_or_default();
        let engines = engines::detect(&settings);
        Self {
            state: Arc::new(Mutex::new(State {
                files: Vec::new(),
                busy: false,
                cancelling: false,
                settings,
                engines,
                message: String::new(),
            })),
            cancel: Arc::new(AtomicBool::new(false)),
            module,
            data,
            worker: None,
        }
    }
    fn snapshot(&self) -> Result<Value, ConvertError> {
        serde_json::to_value(&*self.state.lock().unwrap())
            .map_err(|_| ConvertError::new("stateUnavailable", "无法读取转换状态。"))
    }
    fn save_settings(&self, settings: &Settings) -> Result<(), ConvertError> {
        if let Some(data) = &self.data {
            fs::create_dir_all(data)?;
            let target = data.join("converter-settings.json");
            let temporary = data.join(format!("converter-settings-{}.tmp", unique_id()));
            fs::write(&temporary, serde_json::to_vec_pretty(settings).unwrap())?;
            if let Err(e) = fs::rename(&temporary, target) {
                let _ = fs::remove_file(temporary);
                return Err(e.into());
            }
        }
        Ok(())
    }
    pub fn invoke(&mut self, method: &str, value: Value) -> Result<Value, ConvertError> {
        match method {
            "getState" => self.snapshot(),
            "addFiles" => {
                #[derive(Deserialize)]
                #[serde(deny_unknown_fields)]
                struct Add {
                    paths: Vec<String>,
                    target: Target,
                }
                let add: Add = payload(value)?;
                if add.paths.is_empty() || add.paths.len() > MAX_FILES {
                    return Err(ConvertError::new(
                        "tooManyFiles",
                        "一次最多添加 100 个文件。",
                    ));
                }
                let mut state = self.state.lock().unwrap();
                if state.busy {
                    return Err(busy_error());
                }
                let mut files = Vec::new();
                let mut skipped = 0;
                for path in add.paths {
                    if !valid_path(&path) {
                        skipped += 1;
                        continue;
                    }
                    let Ok(path) = fs::canonicalize(path) else {
                        skipped += 1;
                        continue;
                    };
                    let Ok(info) = fs::metadata(&path) else {
                        skipped += 1;
                        continue;
                    };
                    if !info.is_file()
                        || !add.target.accepts(&path)
                        || info.len() == 0
                        || info.len() > add.target.size_limit()
                    {
                        skipped += 1;
                        continue;
                    }
                    if state.files.iter().chain(files.iter()).any(|f: &FileView| {
                        f.path
                            .to_string_lossy()
                            .eq_ignore_ascii_case(&path.to_string_lossy())
                    }) {
                        skipped += 1;
                        continue;
                    }
                    files.push(FileView {
                        id: unique_id(),
                        name: path
                            .file_name()
                            .unwrap_or_default()
                            .to_string_lossy()
                            .into_owned(),
                        size: info.len(),
                        status: "queued".into(),
                        progress: None,
                        message: String::new(),
                        output_path: None,
                        path,
                    });
                }
                if state.files.len() + files.len() > MAX_FILES {
                    return Err(ConvertError::new(
                        "tooManyFiles",
                        "队列最多保留 100 个文件。",
                    ));
                }
                let count = files.len();
                state.files.extend(files);
                state.message = if skipped > 0 {
                    format!("已添加 {count} 个文件，跳过 {skipped} 个重复、不支持或过大的文件。")
                } else {
                    String::new()
                };
                drop(state);
                self.snapshot()
            }
            "removeFile" => {
                #[derive(Deserialize)]
                #[serde(deny_unknown_fields)]
                struct Remove {
                    id: String,
                }
                let arg: Remove = payload(value)?;
                let mut state = self.state.lock().unwrap();
                if state.busy {
                    return Err(busy_error());
                }
                state.files.retain(|v| v.id != arg.id);
                drop(state);
                self.snapshot()
            }
            "clearFiles" => {
                let mut state = self.state.lock().unwrap();
                if state.busy {
                    return Err(busy_error());
                }
                state.files.clear();
                state.message.clear();
                drop(state);
                self.snapshot()
            }
            "updateSettings" => {
                let settings: Settings = payload(value)?;
                if !["auto", "word", "libreoffice"].contains(&settings.word_engine.as_str()) {
                    return Err(ConvertError::new("engineInvalid", "文档引擎设置无效。"));
                }
                for (path, names) in [
                    (&settings.ffmpeg_path, &["ffmpeg.exe"][..]),
                    (
                        &settings.libre_office_path,
                        &["soffice.exe", "soffice.com"][..],
                    ),
                ] {
                    if !path.is_empty() {
                        let name = Path::new(path)
                            .file_name()
                            .unwrap_or_default()
                            .to_string_lossy()
                            .to_ascii_lowercase();
                        if !valid_path(path)
                            || !Path::new(path).is_absolute()
                            || !Path::new(path).is_file()
                            || !names.contains(&name.as_str())
                        {
                            return Err(ConvertError::new(
                                "engineInvalid",
                                "请选择对应的 ffmpeg.exe 或 soffice.exe 可执行文件。",
                            ));
                        }
                    }
                }
                if !settings.output_directory.is_empty()
                    && (!valid_path(&settings.output_directory)
                        || !Path::new(&settings.output_directory).is_absolute())
                {
                    return Err(ConvertError::new("outputInvalid", "输出目录无效。"));
                }
                if self.state.lock().unwrap().busy {
                    return Err(busy_error());
                }
                self.save_settings(&settings)?;
                let mut state = self.state.lock().unwrap();
                state.engines = engines::detect(&settings);
                state.settings = settings;
                drop(state);
                self.snapshot()
            }
            "startConversion" => self.start(value),
            "cancel" => {
                self.cancel.store(true, Ordering::Release);
                let mut state = self.state.lock().unwrap();
                state.cancelling = state.busy;
                drop(state);
                self.snapshot()
            }
            "openResult" => {
                #[derive(Deserialize)]
                #[serde(deny_unknown_fields)]
                struct Open {
                    id: String,
                }
                let arg: Open = payload(value)?;
                let output = self
                    .state
                    .lock()
                    .unwrap()
                    .files
                    .iter()
                    .find(|f| f.id == arg.id)
                    .and_then(|f| f.output_path.clone())
                    .ok_or_else(|| ConvertError::new("resultMissing", "结果文件尚未生成。"))?;
                if !Path::new(&output).is_file() {
                    return Err(ConvertError::new("resultMissing", "结果文件已移动或删除。"));
                }
                engines::open_directory(Path::new(&output).parent().unwrap())?;
                Ok(json!({"ok":true}))
            }
            _ => Err(ConvertError::new("methodUnsupported", "不支持此操作。")),
        }
    }
    fn start(&mut self, value: Value) -> Result<Value, ConvertError> {
        #[derive(Deserialize)]
        #[serde(rename_all = "camelCase", deny_unknown_fields)]
        struct Start {
            target: Target,
            #[serde(default)]
            options: Options,
            #[serde(default)]
            output_directory: String,
        }
        let arg: Start = payload(value)?;
        arg.options.validate()?;
        let mut state = self.state.lock().unwrap();
        if state.busy {
            return Err(busy_error());
        }
        if state.files.is_empty() {
            return Err(ConvertError::new("filesMissing", "请先添加文件。"));
        }
        if state.files.iter().any(|f| !arg.target.accepts(&f.path)) {
            return Err(ConvertError::new(
                "filesIncompatible",
                "队列中有不适合当前转换类型的文件，请先移除。",
            ));
        }
        state.engines = engines::detect(&state.settings);
        if arg.target.is_audio() && state.engines.ffmpeg_path.is_none() {
            return Err(ConvertError::new(
                "ffmpegMissing",
                "请在引擎设置中选择 ffmpeg.exe 后再转换音频。",
            ));
        }
        if arg.target == Target::WordPdf {
            engines::require_word_engine(&state.settings, &state.engines)?;
        }
        let output = if arg.output_directory.is_empty() {
            state.files[0].path.parent().unwrap().join("Converted")
        } else {
            if !valid_path(&arg.output_directory) || !Path::new(&arg.output_directory).is_absolute()
            {
                return Err(ConvertError::new("outputInvalid", "请选择有效的输出目录。"));
            }
            PathBuf::from(&arg.output_directory)
        };
        fs::create_dir_all(&output)?;
        let output = fs::canonicalize(output)?;
        if !output.is_dir() {
            return Err(ConvertError::new("outputInvalid", "输出位置不是文件夹。"));
        }
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
        self.cancel.store(false, Ordering::Release);
        state.busy = true;
        state.cancelling = false;
        state.message.clear();
        for file in &mut state.files {
            file.status = "queued".into();
            file.progress = None;
            file.message.clear();
            file.output_path = None;
        }
        let files = state.files.clone();
        let settings = state.settings.clone();
        let shared = self.state.clone();
        let cancel = self.cancel.clone();
        let module = self.module.clone();
        drop(state);
        self.worker = Some(thread::spawn(move || {
            for (index, file) in files.iter().enumerate() {
                if cancel.load(Ordering::Acquire) {
                    let mut state = shared.lock().unwrap();
                    for f in state.files.iter_mut().skip(index) {
                        f.status = "cancelled".into();
                        f.message = "已取消".into();
                    }
                    break;
                }
                {
                    let mut state = shared.lock().unwrap();
                    state.files[index].status = "running".into();
                }
                eprintln!(
                    "[convert] task started: target={:?}, index={}",
                    arg.target, index
                );
                let task = Task {
                    input_path: file.path.clone(),
                    output_path: PathBuf::new(),
                    target: arg.target,
                    options: arg.options.clone(),
                    settings: settings.clone(),
                    module_directory: module.clone(),
                    job_name: format!("Local\\QingConvert-{}-{}", std::process::id(), unique_id()),
                };
                let result = run_one(task, &output, &cancel, |progress| {
                    shared.lock().unwrap().files[index].progress = Some(progress);
                });
                let mut state = shared.lock().unwrap();
                let item = &mut state.files[index];
                match result {
                    Ok((path, note)) => {
                        item.status = "succeeded".into();
                        item.progress = Some(1.0);
                        item.output_path = Some(path.to_string_lossy().into_owned());
                        item.message = note;
                        eprintln!("[convert] task succeeded: index={index}");
                    }
                    Err(e) => {
                        item.status = if e.code == "cancelled" {
                            "cancelled"
                        } else {
                            "failed"
                        }
                        .into();
                        item.message = e.message;
                        eprintln!("[convert] task failed: index={index}, code={}", e.code);
                    }
                }
            }
            let mut state = shared.lock().unwrap();
            state.busy = false;
            state.cancelling = false;
        }));
        self.snapshot()
    }
    pub fn shutdown(&mut self) {
        self.cancel.store(true, Ordering::Release);
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

struct TemporaryDirectory(PathBuf);
impl Drop for TemporaryDirectory {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}
fn run_one(
    mut task: Task,
    output: &Path,
    cancel: &AtomicBool,
    mut progress: impl FnMut(f64),
) -> Result<(PathBuf, String), ConvertError> {
    let folder = output.join(format!(".qing-convert-{}", unique_id()));
    fs::create_dir(&folder)?;
    let _temporary = TemporaryDirectory(folder.clone());
    task.output_path = folder.join(format!("result.{}", task.target.extension()));
    let config = folder.join("task.json");
    fs::write(&config, serde_json::to_vec(&task).unwrap())?;
    let job = engines::ProcessJob::new(&task.job_name)?;
    let mut command = Command::new(env::current_exe()?);
    command
        .args(["--convert-task".as_ref(), config.as_os_str()])
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    engines::hide_process(&mut command);
    let mut child = command.spawn()?;
    if let Err(e) = job.assign(&child) {
        let _ = child.kill();
        let _ = child.wait();
        return Err(e);
    }
    let (tx, rx) = mpsc::channel();
    let stdout = child.stdout.take().unwrap();
    let reader = thread::spawn(move || {
        for line in BufReader::new(stdout).lines().take(100_000) {
            let Ok(line) = line else {
                break;
            };
            if line.len() > 65536 {
                break;
            }
            if let Ok(frame) = serde_json::from_str::<Value>(&line) {
                let _ = tx.send(frame);
            }
        }
    });
    let stderr = child.stderr.take().unwrap();
    let drain = thread::spawn(move || {
        let _ = std::io::copy(&mut stderr.take(1024 * 1024), &mut std::io::sink());
    });
    let start = Instant::now();
    let mut last = Value::Null;
    let status = loop {
        for frame in rx.try_iter() {
            if let Some(v) = frame
                .get("progress")
                .and_then(Value::as_f64)
                .filter(|v| v.is_finite() && *v >= 0.0 && *v <= 1.0)
            {
                progress(v);
            } else {
                last = frame;
            }
        }
        if cancel.load(Ordering::Acquire) {
            drop(job);
            let _ = child.kill();
            let _ = child.wait();
            let _ = reader.join();
            let _ = drain.join();
            return Err(ConvertError::cancelled());
        }
        let limit = if task.target.is_audio() { 3600 } else { 180 };
        if start.elapsed() > Duration::from_secs(limit) {
            drop(job);
            let _ = child.kill();
            let _ = child.wait();
            let _ = reader.join();
            let _ = drain.join();
            return Err(ConvertError::new(
                "conversionTimeout",
                "转换超时，已终止当前任务。请检查文档、引擎或 Office 的激活状态。",
            ));
        }
        if let Some(status) = child.try_wait()? {
            break status;
        }
        thread::sleep(Duration::from_millis(100));
    };
    drop(job);
    let _ = reader.join();
    let _ = drain.join();
    for frame in rx.try_iter() {
        if frame.get("progress").is_none() {
            last = frame;
        }
    }
    if !status.success() || last.get("done") != Some(&Value::Bool(true)) {
        let code = last
            .pointer("/error/code")
            .and_then(Value::as_str)
            .unwrap_or("engineFailed");
        let message = last
            .pointer("/error/message")
            .and_then(Value::as_str)
            .unwrap_or("转换引擎未能完成，请检查文件是否损坏或受密码保护。");
        return Err(ConvertError::new(code, message));
    }
    if cancel.load(Ordering::Acquire) {
        return Err(ConvertError::cancelled());
    }
    if fs::metadata(&task.output_path)?.len() == 0 {
        return Err(ConvertError::new("outputEmpty", "转换没有生成有效文件。"));
    }
    let final_path = publish_result(
        &task.output_path,
        output,
        task.input_path
            .file_stem()
            .unwrap_or_default()
            .to_string_lossy()
            .as_ref(),
        task.target.extension(),
    )?;
    Ok((
        final_path,
        last.get("note")
            .and_then(Value::as_str)
            .unwrap_or_default()
            .to_string(),
    ))
}

fn publish_result(
    temporary: &Path,
    directory: &Path,
    stem: &str,
    extension: &str,
) -> Result<PathBuf, ConvertError> {
    for index in 0..10_000 {
        let suffix = if index == 0 {
            String::new()
        } else {
            format!(" ({index})")
        };
        let path = directory.join(format!("{stem}{suffix}.{extension}"));
        match publish_without_replace(temporary, &path) {
            Ok(()) => return Ok(path),
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(e) => return Err(e.into()),
        }
    }
    Err(ConvertError::new(
        "outputConflict",
        "输出目录中有过多重名文件，请选择其他目录。",
    ))
}

#[cfg(windows)]
fn publish_without_replace(temporary: &Path, path: &Path) -> std::io::Result<()> {
    use std::os::windows::ffi::OsStrExt;
    let source: Vec<_> = temporary.as_os_str().encode_wide().chain(Some(0)).collect();
    let target: Vec<_> = path.as_os_str().encode_wide().chain(Some(0)).collect();
    // Same-directory-volume atomic move; unlike Rust's rename this never
    // replaces an existing file. Also works on FAT/exFAT and SMB targets.
    unsafe {
        if windows_sys::Win32::Storage::FileSystem::MoveFileW(source.as_ptr(), target.as_ptr()) == 0
        {
            return Err(std::io::Error::last_os_error());
        }
    }
    Ok(())
}
#[cfg(not(windows))]
fn publish_without_replace(temporary: &Path, path: &Path) -> std::io::Result<()> {
    fs::hard_link(temporary, path)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn output_never_overwrites_an_existing_file() {
        let root = env::temp_dir().join(format!("qing-convert-test-{}", unique_id()));
        fs::create_dir(&root).unwrap();
        let source = root.join("temp");
        fs::write(&source, b"new").unwrap();
        fs::write(root.join("result.png"), b"original").unwrap();
        let path = publish_result(&source, &root, "result", "png").unwrap();
        assert_eq!(path.file_name().unwrap(), "result (1).png");
        assert_eq!(fs::read(root.join("result.png")).unwrap(), b"original");
        fs::remove_dir_all(root).unwrap();
    }
}
