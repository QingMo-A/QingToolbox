#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

//! Native Qing PDF module.
//!
//! The process is intentionally small: the host owns the process boundary,
//! this module owns validated PDF state, and the official qpdf executable does
//! the structural PDF work.  No PDF parser or filesystem bridge is embedded
//! in the Tauri host.

use std::{
    collections::{HashMap, HashSet},
    env, fs,
    io::{self, BufRead, BufWriter, Write},
    path::{Path, PathBuf},
    process::{Command, Stdio},
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc,
    },
    time::{SystemTime, UNIX_EPOCH},
};

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};

const PROTOCOL_VERSION: u16 = 1;
const MAX_FRAME_BYTES: usize = 1024 * 1024;
const MAX_FILES: usize = 64;
const MAX_PATH_CHARS: usize = 32_767;
const MAX_PAGE_COUNT: u32 = 1_000_000;
const MAX_FILE_BYTES: u64 = 8 * 1024 * 1024 * 1024;
const QPDF_VERSION: &str = "12.4.1";

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
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

#[derive(Debug, Clone)]
struct PdfFileEntry {
    id: String,
    path: PathBuf,
    pages: u32,
    size: u64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct PdfFileView {
    id: String,
    name: String,
    directory: String,
    pages: u32,
    size: u64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct PdfResultView {
    id: String,
    operation: String,
    output_directory: String,
    files: Vec<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct PdfStateView {
    merge_files: Vec<PdfFileView>,
    source: Option<PdfFileView>,
    busy: bool,
    active_operation: Option<String>,
    runtime_status: String,
    message: Option<String>,
    result: Option<PdfResultView>,
}

#[derive(Debug, Clone, Copy)]
struct PageRange {
    start: u32,
    end: u32,
}

impl PageRange {
    #[cfg(test)]
    fn count(self) -> u32 {
        self.end - self.start + 1
    }
}

impl std::fmt::Display for PageRange {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        if self.start == self.end {
            write!(formatter, "{}", self.start)
        } else {
            write!(formatter, "{}-{}", self.start, self.end)
        }
    }
}

#[derive(Debug)]
struct QpdfEngine {
    executable: PathBuf,
    integrity_valid: bool,
    cancel_requested: Arc<AtomicBool>,
}

impl QpdfEngine {
    fn new(module_directory: PathBuf) -> Self {
        let runtime = module_directory.join("third-party").join("qpdf");
        Self {
            executable: runtime.join("qpdf.exe"),
            integrity_valid: verify_runtime(&runtime),
            cancel_requested: Arc::new(AtomicBool::new(false)),
        }
    }

    fn available(&self) -> bool {
        self.integrity_valid && self.executable.is_file()
    }

    fn status(&self) -> String {
        if self.available() {
            format!("Ready · qpdf {QPDF_VERSION}")
        } else {
            "Unavailable".to_string()
        }
    }

    fn begin_operation(&self) {
        self.cancel_requested.store(false, Ordering::Release);
    }

    fn cancel(&self) {
        self.cancel_requested.store(true, Ordering::Release);
    }

    fn check_cancelled(&self) -> Result<(), PdfError> {
        if self.cancel_requested.load(Ordering::Acquire) {
            Err(PdfError::cancelled())
        } else {
            Ok(())
        }
    }

    fn page_count(&self, path: &Path) -> Result<u32, PdfError> {
        require_pdf_input(path)?;
        let result = self.run(&["--show-npages".to_string(), path_string(path)])?;
        let pages = result
            .stdout
            .trim()
            .parse::<u32>()
            .map_err(|_| PdfError::new("pdf_invalid", "无法读取 PDF 页数。"))?;
        if pages == 0 || pages > MAX_PAGE_COUNT {
            return Err(PdfError::new("pdf_invalid", "PDF 页数超出支持范围。"));
        }
        Ok(pages)
    }

    fn merge(&self, inputs: &[PathBuf], output: &Path) -> Result<(), PdfError> {
        if inputs.len() < 2 {
            return Err(PdfError::new("invalid_payload", "至少选择两个 PDF 文件。"));
        }
        for input in inputs {
            require_pdf_input(input)?;
        }
        reject_overwrite(inputs, output)?;
        self.atomic_output(output, |temporary| {
            let mut args = vec!["--empty".to_string(), "--pages".to_string()];
            for input in inputs {
                args.push(format!("--file={}", path_string(input)));
            }
            args.extend(["--".to_string(), path_string(temporary)]);
            self.run(&args).map(|_| ())
        })
    }

    fn extract(&self, input: &Path, pages: &str, output: &Path) -> Result<(), PdfError> {
        require_pdf_input(input)?;
        if pages.is_empty() {
            return Err(PdfError::new("invalid_pages", "页码范围不能为空。"));
        }
        reject_overwrite(&[input.to_path_buf()], output)?;
        self.atomic_output(output, |temporary| {
            self.run(&[
                "--empty".to_string(),
                "--pages".to_string(),
                format!("--file={}", path_string(input)),
                format!("--range={pages}"),
                "--".to_string(),
                path_string(temporary),
            ])
            .map(|_| ())
        })
    }

    fn rotate(
        &self,
        input: &Path,
        degrees: u32,
        pages: &str,
        output: &Path,
    ) -> Result<(), PdfError> {
        require_pdf_input(input)?;
        if !matches!(degrees, 90 | 180 | 270) {
            return Err(PdfError::new(
                "invalid_degrees",
                "旋转角度必须是 90、180 或 270。",
            ));
        }
        reject_overwrite(&[input.to_path_buf()], output)?;
        let rotate = if pages.is_empty() {
            format!("--rotate=+{degrees}")
        } else {
            format!("--rotate=+{degrees}:{pages}")
        };
        self.atomic_output(output, |temporary| {
            self.run(&[path_string(input), rotate, path_string(temporary)])
                .map(|_| ())
        })
    }

    fn atomic_output<F>(&self, output: &Path, action: F) -> Result<(), PdfError>
    where
        F: FnOnce(&Path) -> Result<(), PdfError>,
    {
        let output = validated_output_path(&path_string(output))?;
        let parent = output
            .parent()
            .ok_or_else(|| PdfError::new("output_invalid", "输出路径缺少目录。"))?;
        fs::create_dir_all(parent)
            .map_err(|error| PdfError::io("output_unavailable", "无法创建输出目录。", error))?;
        let temporary = parent.join(format!(
            ".{}.qpdf-{}.tmp.pdf",
            output
                .file_name()
                .and_then(|name| name.to_str())
                .unwrap_or("output.pdf"),
            unique_id()
        ));
        let result = action(&temporary);
        if result.is_ok() {
            if !temporary.is_file() {
                return Err(PdfError::new("pdf_failed", "qpdf 没有生成输出文件。"));
            }
            replace_file(&temporary, &output)?;
        } else {
            let _ = fs::remove_file(&temporary);
        }
        result
    }

    fn run(&self, arguments: &[String]) -> Result<ProcessResult, PdfError> {
        if !self.available() {
            return Err(PdfError::new(
                "runtime_unavailable",
                "内置 qpdf 运行时不可用。",
            ));
        }
        self.check_cancelled()?;
        let mut command = Command::new(&self.executable);
        command
            .args(arguments)
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .current_dir(self.executable.parent().unwrap_or_else(|| Path::new(".")));
        hide_process(&mut command);
        let output = command
            .output()
            .map_err(|error| PdfError::io("runtime_start_failed", "无法启动 qpdf。", error))?;
        self.check_cancelled()?;
        let stdout = String::from_utf8_lossy(&output.stdout).to_string();
        let stderr = String::from_utf8_lossy(&output.stderr).to_string();
        // qpdf uses exit code 3 for warnings while still producing a valid
        // output. Treat only other non-zero codes as operation failures.
        if !output.status.success() && output.status.code() != Some(3) {
            return Err(PdfError::new(
                "pdf_failed",
                useful_error(&stderr).unwrap_or("qpdf 无法处理这个 PDF。"),
            ));
        }
        Ok(ProcessResult { stdout, stderr })
    }
}

#[derive(Debug)]
struct ProcessResult {
    stdout: String,
    #[allow(dead_code)]
    stderr: String,
}

#[derive(Debug)]
struct PdfError {
    code: &'static str,
    message: String,
}

impl PdfError {
    fn new(code: &'static str, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }

    fn io(code: &'static str, prefix: &'static str, error: io::Error) -> Self {
        Self::new(code, format!("{prefix} ({error})"))
    }

    fn cancelled() -> Self {
        Self::new("canceled", "操作已取消。")
    }
}

#[derive(Debug)]
struct PdfApp {
    module_id: String,
    expected_nonce: String,
    merge_files: Vec<PdfFileEntry>,
    source: Option<PdfFileEntry>,
    busy: bool,
    active_operation: Option<String>,
    message: Option<String>,
    result: Option<PdfResultView>,
    results: HashMap<String, Vec<PathBuf>>,
    engine: QpdfEngine,
}

impl PdfApp {
    fn new(module_id: String, expected_nonce: String, module_directory: PathBuf) -> Self {
        Self {
            module_id,
            expected_nonce,
            merge_files: Vec::new(),
            source: None,
            busy: false,
            active_operation: None,
            message: None,
            result: None,
            results: HashMap::new(),
            engine: QpdfEngine::new(module_directory),
        }
    }

    fn state(&self) -> PdfStateView {
        PdfStateView {
            merge_files: self.merge_files.iter().map(file_view).collect(),
            source: self.source.as_ref().map(file_view),
            busy: self.busy,
            active_operation: self.active_operation.clone(),
            runtime_status: self.engine.status(),
            message: self.message.clone(),
            result: self.result.clone(),
        }
    }

    fn invoke(&mut self, method: &str, payload: Value) -> Result<Value, PdfError> {
        if self.busy && !matches!(method, "cancel" | "getState") {
            return Err(PdfError::new("busy", "另一个 PDF 操作正在进行。"));
        }
        let value = match method {
            "getState" => serde_json::to_value(self.state())
                .map_err(|_| PdfError::new("serialization_failed", "PDF 状态无法序列化。"))?,
            "addMergeFiles" => {
                self.add_merge_files(required_string_array(&payload, "paths")?)?;
                serde_json::to_value(self.state())
                    .map_err(|_| PdfError::new("serialization_failed", "PDF 状态无法序列化。"))?
            }
            "setSource" => {
                let path = validated_pdf_path(&required_string(&payload, "path")?)?;
                self.source = Some(self.inspect(path)?);
                serde_json::to_value(self.state())
                    .map_err(|_| PdfError::new("serialization_failed", "PDF 状态无法序列化。"))?
            }
            "setMergeOrder" => {
                self.set_merge_order(required_string_array(&payload, "ids")?)?;
                serde_json::to_value(self.state())
                    .map_err(|_| PdfError::new("serialization_failed", "PDF 状态无法序列化。"))?
            }
            "removeMergeFile" => {
                let id = required_string(&payload, "id")?;
                let before = self.merge_files.len();
                self.merge_files.retain(|file| file.id != id);
                if self.merge_files.len() == before {
                    return Err(PdfError::new("not_found", "选中的 PDF 已不在拼接列表中。"));
                }
                serde_json::to_value(self.state())
                    .map_err(|_| PdfError::new("serialization_failed", "PDF 状态无法序列化。"))?
            }
            "clearMergeFiles" => {
                self.merge_files.clear();
                serde_json::to_value(self.state())
                    .map_err(|_| PdfError::new("serialization_failed", "PDF 状态无法序列化。"))?
            }
            "runMerge" => {
                let output = validated_output_path(&required_string(&payload, "outputPath")?)?;
                let inputs = self
                    .merge_files
                    .iter()
                    .map(|file| file.path.clone())
                    .collect::<Vec<_>>();
                self.run_operation("merge", vec![output.clone()], |engine| {
                    engine.merge(&inputs, &output)
                })?;
                serde_json::to_value(self.state())
                    .map_err(|_| PdfError::new("serialization_failed", "PDF 状态无法序列化。"))?
            }
            "runSplit" => {
                let parts = required_u32(&payload, "parts")?;
                let directory =
                    validated_output_directory(&required_string(&payload, "outputDirectory")?)?;
                let source = self.require_source()?.clone();
                let ranges = split_evenly(source.pages, parts)?;
                let outputs = ranges
                    .iter()
                    .enumerate()
                    .map(|(index, _)| {
                        unique_path(
                            &directory,
                            &format!(
                                "{}_part-{:02}-of-{:02}.pdf",
                                source
                                    .path
                                    .file_stem()
                                    .and_then(|v| v.to_str())
                                    .unwrap_or("document"),
                                index + 1,
                                parts
                            ),
                        )
                    })
                    .collect::<Vec<_>>();
                let input = source.path.clone();
                let outputs_for_job = outputs.clone();
                self.run_operation("split", outputs.clone(), |engine| {
                    for (range, output) in ranges.iter().zip(outputs_for_job.iter()) {
                        engine.extract(&input, &range.to_string(), output)?;
                    }
                    Ok(())
                })?;
                serde_json::to_value(self.state())
                    .map_err(|_| PdfError::new("serialization_failed", "PDF 状态无法序列化。"))?
            }
            "runExtract" => {
                let source = self.require_source()?.clone();
                let pages = parse_page_selection(
                    &required_string(&payload, "pages")?,
                    source.pages,
                    false,
                )?;
                let output = validated_output_path(&required_string(&payload, "outputPath")?)?;
                let input = source.path.clone();
                self.run_operation("extract", vec![output.clone()], |engine| {
                    engine.extract(&input, &pages, &output)
                })?;
                serde_json::to_value(self.state())
                    .map_err(|_| PdfError::new("serialization_failed", "PDF 状态无法序列化。"))?
            }
            "runRotate" => {
                let source = self.require_source()?.clone();
                let degrees = required_u32(&payload, "degrees")?;
                let pages_value = optional_string(&payload, "pages");
                let pages = parse_page_selection(&pages_value, source.pages, true)?;
                let output = validated_output_path(&required_string(&payload, "outputPath")?)?;
                let input = source.path.clone();
                self.run_operation("rotate", vec![output.clone()], |engine| {
                    engine.rotate(&input, degrees, &pages, &output)
                })?;
                serde_json::to_value(self.state())
                    .map_err(|_| PdfError::new("serialization_failed", "PDF 状态无法序列化。"))?
            }
            "cancel" => {
                self.engine.cancel();
                self.message = Some("canceled".to_string());
                serde_json::to_value(self.state())
                    .map_err(|_| PdfError::new("serialization_failed", "PDF 状态无法序列化。"))?
            }
            "openResult" => {
                let id = required_string(&payload, "resultId")?;
                let paths = self
                    .results
                    .get(&id)
                    .ok_or_else(|| PdfError::new("not_found", "处理结果已不可用。"))?;
                let first = paths
                    .iter()
                    .find(|path| path.is_file())
                    .ok_or_else(|| PdfError::new("not_found", "处理结果已不可用。"))?;
                open_result(first)?;
                json!({"ok": true})
            }
            "clearResult" => {
                self.result = None;
                self.results.clear();
                self.message = None;
                serde_json::to_value(self.state())
                    .map_err(|_| PdfError::new("serialization_failed", "PDF 状态无法序列化。"))?
            }
            _ => return Err(PdfError::new("unknown_method", "未知的 Qing PDF 操作。")),
        };
        Ok(value)
    }

    fn add_merge_files(&mut self, paths: Vec<String>) -> Result<(), PdfError> {
        if paths.is_empty() {
            return Ok(());
        }
        if paths.len() > MAX_FILES {
            return Err(PdfError::new(
                "invalid_payload",
                "一次添加的 PDF 文件过多。",
            ));
        }
        let existing = self
            .merge_files
            .iter()
            .map(|file| normalize_compare(&file.path))
            .collect::<HashSet<_>>();
        let mut seen = existing;
        for raw in paths {
            let path = validated_pdf_path(&raw)?;
            if !seen.insert(normalize_compare(&path)) {
                continue;
            }
            self.merge_files.push(self.inspect(path)?);
            if self.merge_files.len() >= MAX_FILES {
                break;
            }
        }
        Ok(())
    }

    fn inspect(&self, path: PathBuf) -> Result<PdfFileEntry, PdfError> {
        let pages = self.engine.page_count(&path)?;
        let size = fs::metadata(&path)
            .map_err(|error| PdfError::io("pdf_unavailable", "无法读取 PDF 文件。", error))?
            .len();
        Ok(PdfFileEntry {
            id: format!("pdf-{}", unique_id()),
            path,
            pages,
            size,
        })
    }

    fn set_merge_order(&mut self, ids: Vec<String>) -> Result<(), PdfError> {
        if ids.len() != self.merge_files.len() {
            return Err(PdfError::new(
                "invalid_payload",
                "拼接顺序必须包含全部 PDF。",
            ));
        }
        let mut by_id = self
            .merge_files
            .drain(..)
            .map(|file| (file.id.clone(), file))
            .collect::<HashMap<_, _>>();
        let mut reordered = Vec::with_capacity(ids.len());
        for id in ids {
            let file = by_id
                .remove(&id)
                .ok_or_else(|| PdfError::new("invalid_payload", "拼接列表已经发生变化。"))?;
            reordered.push(file);
        }
        if !by_id.is_empty() {
            return Err(PdfError::new("invalid_payload", "拼接顺序包含未知 PDF。"));
        }
        self.merge_files = reordered;
        Ok(())
    }

    fn require_source(&self) -> Result<&PdfFileEntry, PdfError> {
        let source = self
            .source
            .as_ref()
            .ok_or_else(|| PdfError::new("missing_source", "请先选择一个源 PDF。"))?;
        require_pdf_input(&source.path)?;
        Ok(source)
    }

    fn run_operation<F>(
        &mut self,
        operation: &str,
        outputs: Vec<PathBuf>,
        action: F,
    ) -> Result<(), PdfError>
    where
        F: FnOnce(&QpdfEngine) -> Result<(), PdfError>,
    {
        if self.busy {
            return Err(PdfError::new("busy", "另一个 PDF 操作正在进行。"));
        }
        self.busy = true;
        self.active_operation = Some(operation.to_string());
        self.message = None;
        self.result = None;
        self.engine.begin_operation();
        let result = action(&self.engine);
        self.busy = false;
        self.active_operation = None;
        match result {
            Ok(()) => {
                let result_id = format!("result-{}", unique_id());
                let normalized = outputs
                    .iter()
                    .map(|path| {
                        strip_windows_extended_prefix(
                            fs::canonicalize(path).unwrap_or_else(|_| path.clone()),
                        )
                    })
                    .collect::<Vec<_>>();
                let output_directory = normalized
                    .first()
                    .and_then(|path| path.parent())
                    .map(path_string)
                    .unwrap_or_default();
                let files = normalized
                    .iter()
                    .filter_map(|path| path.file_name().and_then(|name| name.to_str()))
                    .map(str::to_string)
                    .collect::<Vec<_>>();
                self.results.clear();
                self.results.insert(result_id.clone(), normalized);
                self.result = Some(PdfResultView {
                    id: result_id,
                    operation: operation.to_string(),
                    output_directory,
                    files,
                });
                self.message = Some("completed".to_string());
                Ok(())
            }
            Err(error) => {
                self.message = Some(if error.code == "canceled" {
                    "canceled".to_string()
                } else {
                    "failed".to_string()
                });
                for output in outputs {
                    let _ = fs::remove_file(output);
                }
                Err(error)
            }
        }
    }
}

fn main() {
    let module_id = env::var("QINGTOOLBOX_MODULE_ID").unwrap_or_default();
    let expected_nonce = env::var("QINGTOOLBOX_MODULE_NONCE").unwrap_or_default();
    let module_directory = env::var_os("QINGTOOLBOX_MODULE_DIRECTORY")
        .map(PathBuf::from)
        .or_else(|| env::current_dir().ok())
        .unwrap_or_else(|| PathBuf::from("."));
    let mut app = PdfApp::new(module_id, expected_nonce, module_directory);
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
                if !valid_hello(&envelope.payload, &app.module_id, &app.expected_nonce) {
                    write_error(
                        &mut stdout,
                        "module.hello.response",
                        &envelope.request_id,
                        "hello_invalid",
                        "模块 hello 身份与宿主环境不匹配。",
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
                            "moduleId": app.module_id,
                            "nonce": app.expected_nonce,
                            "lifecycleVersion": 1,
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
                        "invoke payload 必须是对象。",
                    );
                    continue;
                };
                let Some(method) = object.get("method").and_then(Value::as_str) else {
                    write_error(
                        &mut stdout,
                        "module.invoke.response",
                        &envelope.request_id,
                        "invalid_method",
                        "invoke method 不能为空。",
                    );
                    continue;
                };
                let payload = object.get("payload").cloned().unwrap_or(Value::Null);
                match app.invoke(method, payload) {
                    Ok(payload) => write_response(
                        &mut stdout,
                        Response {
                            protocol_version: PROTOCOL_VERSION,
                            message_type: "module.invoke.response",
                            request_id: &envelope.request_id,
                            payload,
                            error: None,
                        },
                    ),
                    Err(error) => write_error(
                        &mut stdout,
                        "module.invoke.response",
                        &envelope.request_id,
                        error.code,
                        error.message,
                    ),
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
                "Qing PDF 不支持这个消息类型。",
            ),
        }
    }
}

fn file_view(file: &PdfFileEntry) -> PdfFileView {
    PdfFileView {
        id: file.id.clone(),
        name: file
            .path
            .file_name()
            .and_then(|value| value.to_str())
            .unwrap_or("document.pdf")
            .to_string(),
        directory: file.path.parent().map(path_string).unwrap_or_default(),
        pages: file.pages,
        size: file.size,
    }
}

fn split_evenly(page_count: u32, parts: u32) -> Result<Vec<PageRange>, PdfError> {
    if page_count < 1 || parts < 2 || parts > page_count {
        return Err(PdfError::new(
            "invalid_parts",
            "均分份数必须在 2 到总页数之间。",
        ));
    }
    let base = page_count / parts;
    let remainder = page_count % parts;
    let mut start = 1;
    let mut ranges = Vec::with_capacity(parts as usize);
    for index in 0..parts {
        let count = base + u32::from(index < remainder);
        ranges.push(PageRange {
            start,
            end: start + count - 1,
        });
        start += count;
    }
    Ok(ranges)
}

fn parse_page_selection(value: &str, page_count: u32, allow_all: bool) -> Result<String, PdfError> {
    let trimmed = value.trim();
    if allow_all && (trimmed.is_empty() || trimmed.eq_ignore_ascii_case("all")) {
        return Ok(String::new());
    }
    if trimmed.is_empty() {
        return Err(PdfError::new("invalid_pages", "请填写页码范围。"));
    }
    let mut ranges = Vec::new();
    for part in trimmed.split(',') {
        let part = part.trim();
        if part.is_empty() {
            return Err(PdfError::new("invalid_pages", "页码范围格式无效。"));
        }
        let mut sides = part.split('-');
        let start = parse_page(sides.next().unwrap_or_default(), page_count)?;
        let end = match sides.next() {
            Some(value) => parse_page(value, page_count)?,
            None => start,
        };
        if sides.next().is_some() || end < start {
            return Err(PdfError::new("invalid_pages", "页码范围格式无效。"));
        }
        ranges.push(PageRange { start, end });
    }
    Ok(ranges
        .iter()
        .map(ToString::to_string)
        .collect::<Vec<_>>()
        .join(","))
}

fn parse_page(value: &str, page_count: u32) -> Result<u32, PdfError> {
    let page = value
        .trim()
        .parse::<u32>()
        .map_err(|_| PdfError::new("invalid_pages", "页码范围格式无效。"))?;
    if page == 0 || page > page_count {
        return Err(PdfError::new("invalid_pages", "页码超出 PDF 范围。"));
    }
    Ok(page)
}

fn required_string(payload: &Value, name: &str) -> Result<String, PdfError> {
    payload
        .get(name)
        .and_then(Value::as_str)
        .filter(|value| !value.trim().is_empty())
        .map(str::to_string)
        .ok_or_else(|| PdfError::new("invalid_payload", format!("缺少 {name}。")))
}

fn required_string_array(payload: &Value, name: &str) -> Result<Vec<String>, PdfError> {
    let values = payload
        .get(name)
        .and_then(Value::as_array)
        .ok_or_else(|| PdfError::new("invalid_payload", format!("缺少 {name}。")))?;
    if values.len() > MAX_FILES {
        return Err(PdfError::new("invalid_payload", format!("{name} 太大。")));
    }
    values
        .iter()
        .map(|value| {
            value
                .as_str()
                .filter(|item| !item.trim().is_empty())
                .map(str::to_string)
                .ok_or_else(|| {
                    PdfError::new("invalid_payload", format!("{name} 必须是字符串数组。"))
                })
        })
        .collect()
}

fn required_u32(payload: &Value, name: &str) -> Result<u32, PdfError> {
    let value = payload
        .get(name)
        .and_then(Value::as_u64)
        .ok_or_else(|| PdfError::new("invalid_payload", format!("缺少 {name}。")))?;
    u32::try_from(value).map_err(|_| PdfError::new("invalid_payload", format!("{name} 超出范围。")))
}

fn optional_string(payload: &Value, name: &str) -> String {
    payload
        .get(name)
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_string()
}

fn validated_pdf_path(raw: &str) -> Result<PathBuf, PdfError> {
    if raw.chars().count() > MAX_PATH_CHARS
        || raw.contains('\0')
        || raw.chars().any(char::is_control)
    {
        return Err(PdfError::new("path_invalid", "文件路径无效。"));
    }
    let path = PathBuf::from(raw);
    if !path.is_absolute() {
        return Err(PdfError::new("path_invalid", "必须使用绝对文件路径。"));
    }
    let canonical = fs::canonicalize(&path)
        .map_err(|error| PdfError::io("pdf_unavailable", "选中的 PDF 不可用。", error))?;
    let canonical = strip_windows_extended_prefix(canonical);
    require_pdf_input(&canonical)?;
    Ok(canonical)
}

fn require_pdf_input(path: &Path) -> Result<(), PdfError> {
    let metadata = fs::metadata(path)
        .map_err(|error| PdfError::io("pdf_unavailable", "选中的 PDF 不可用。", error))?;
    if !metadata.is_file() || metadata.len() > MAX_FILE_BYTES {
        return Err(PdfError::new("pdf_invalid", "PDF 文件大小超出支持范围。"));
    }
    if !path
        .extension()
        .and_then(|value| value.to_str())
        .is_some_and(|value| value.eq_ignore_ascii_case("pdf"))
    {
        return Err(PdfError::new("pdf_invalid", "只支持 .pdf 文件。"));
    }
    Ok(())
}

fn validated_output_path(raw: &str) -> Result<PathBuf, PdfError> {
    if raw.chars().count() > MAX_PATH_CHARS
        || raw.contains('\0')
        || raw.chars().any(char::is_control)
    {
        return Err(PdfError::new("output_invalid", "输出路径无效。"));
    }
    let path = PathBuf::from(raw);
    if !path.is_absolute()
        || !path
            .extension()
            .and_then(|value| value.to_str())
            .is_some_and(|value| value.eq_ignore_ascii_case("pdf"))
    {
        return Err(PdfError::new(
            "output_invalid",
            "输出路径必须是绝对 .pdf 路径。",
        ));
    }
    Ok(path)
}

fn validated_output_directory(raw: &str) -> Result<PathBuf, PdfError> {
    if raw.chars().count() > MAX_PATH_CHARS
        || raw.contains('\0')
        || raw.chars().any(char::is_control)
    {
        return Err(PdfError::new("output_invalid", "输出目录无效。"));
    }
    let path = PathBuf::from(raw);
    if !path.is_absolute() {
        return Err(PdfError::new("output_invalid", "输出目录必须是绝对路径。"));
    }
    fs::create_dir_all(&path)
        .map_err(|error| PdfError::io("output_unavailable", "无法创建输出目录。", error))?;
    fs::canonicalize(&path)
        .map_err(|error| PdfError::io("output_unavailable", "无法读取输出目录。", error))
}

fn reject_overwrite(inputs: &[PathBuf], output: &Path) -> Result<(), PdfError> {
    let normalized = normalize_compare(output);
    if inputs
        .iter()
        .any(|input| normalize_compare(input) == normalized)
    {
        Err(PdfError::new(
            "output_invalid",
            "输出文件不能覆盖输入 PDF。",
        ))
    } else {
        Ok(())
    }
}

fn unique_path(directory: &Path, file_name: &str) -> PathBuf {
    let candidate = directory.join(file_name);
    if !candidate.exists() {
        return candidate;
    }
    let stem = Path::new(file_name)
        .file_stem()
        .and_then(|value| value.to_str())
        .unwrap_or("document");
    for index in 2..=10_000u32 {
        let candidate = directory.join(format!("{stem}_{index}.pdf"));
        if !candidate.exists() {
            return candidate;
        }
    }
    directory.join(format!("{stem}_{}.pdf", unique_id()))
}

fn replace_file(temporary: &Path, destination: &Path) -> Result<(), PdfError> {
    if destination.exists() {
        let backup = destination.with_extension(format!("pdf.bak-{}", unique_id()));
        fs::rename(destination, &backup)
            .map_err(|error| PdfError::io("output_write_failed", "无法替换输出文件。", error))?;
        if let Err(error) = fs::rename(temporary, destination) {
            let _ = fs::rename(&backup, destination);
            return Err(PdfError::io(
                "output_write_failed",
                "无法写入输出文件。",
                error,
            ));
        }
        let _ = fs::remove_file(backup);
    } else {
        fs::rename(temporary, destination)
            .map_err(|error| PdfError::io("output_write_failed", "无法写入输出文件。", error))?;
    }
    Ok(())
}

fn open_result(path: &Path) -> Result<(), PdfError> {
    #[cfg(windows)]
    {
        let argument = format!("/select,\"{}\"", path_string(path));
        Command::new("explorer.exe")
            .arg(argument)
            .spawn()
            .map(|_| ())
            .map_err(|error| PdfError::io("open_failed", "无法打开输出文件夹。", error))
    }
    #[cfg(not(windows))]
    {
        Command::new("xdg-open")
            .arg(path)
            .spawn()
            .map(|_| ())
            .map_err(|error| PdfError::io("open_failed", "无法打开输出文件。", error))
    }
}

fn verify_runtime(root: &Path) -> bool {
    let manifest = root.join("SHA256SUMS");
    let Ok(content) = fs::read_to_string(&manifest) else {
        return false;
    };
    for line in content.lines().filter(|line| !line.trim().is_empty()) {
        let Some((expected, relative)) = line.split_once("  ") else {
            return false;
        };
        if expected.len() != 64 || relative.is_empty() || relative.contains(['/', '\\', ':']) {
            return false;
        }
        let path = root.join(relative);
        let Ok(bytes) = fs::read(&path) else {
            return false;
        };
        let actual = Sha256::digest(bytes);
        if hex_lower(&actual) != expected.to_ascii_lowercase() {
            return false;
        }
    }
    root.join("qpdf.exe").is_file()
}

fn hex_lower(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

fn useful_error(value: &str) -> Option<&str> {
    value.lines().map(str::trim).rfind(|line| !line.is_empty())
}

fn normalize_compare(path: &Path) -> String {
    path.to_string_lossy()
        .replace('/', "\\")
        .to_ascii_lowercase()
}

fn path_string(path: &Path) -> String {
    path.to_string_lossy().to_string()
}

// Rust's Windows canonicalize may return an extended-length `\\?\` path.
// qpdf's portable command-line parser does not consistently accept that
// spelling, so keep the canonical validation but hand the normal DOS/UNC
// spelling to the sidecar process.
#[cfg(windows)]
fn strip_windows_extended_prefix(path: PathBuf) -> PathBuf {
    let value = path.to_string_lossy();
    if let Some(rest) = value.strip_prefix(r"\\?\UNC\") {
        PathBuf::from(format!(r"\\{rest}"))
    } else if let Some(rest) = value.strip_prefix(r"\\?\") {
        PathBuf::from(rest)
    } else {
        path
    }
}

#[cfg(not(windows))]
fn strip_windows_extended_prefix(path: PathBuf) -> PathBuf {
    path
}

fn unique_id() -> String {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|value| value.as_nanos())
        .unwrap_or_default();
    format!("{nanos:x}")
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

#[cfg(windows)]
fn hide_process(command: &mut Command) {
    use std::os::windows::process::CommandExt;
    command.creation_flags(0x0800_0000);
}

#[cfg(not(windows))]
fn hide_process(_command: &mut Command) {}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn split_ranges_are_non_empty_and_balanced() {
        let ranges = split_evenly(10, 3).expect("ranges");
        assert_eq!(
            ranges.iter().map(|range| range.count()).collect::<Vec<_>>(),
            vec![4, 3, 3]
        );
        assert_eq!(ranges[0].to_string(), "1-4");
        assert_eq!(ranges[2].to_string(), "8-10");
    }

    #[test]
    fn page_selection_is_bounded_and_canonicalized() {
        assert_eq!(
            parse_page_selection("1-3, 5,8-10", 10, false).unwrap(),
            "1-3,5,8-10"
        );
        assert!(parse_page_selection("0", 10, false).is_err());
        assert!(parse_page_selection("1-11", 10, false).is_err());
        assert_eq!(parse_page_selection("all", 10, true).unwrap(), "");
    }

    #[test]
    fn output_must_be_absolute_pdf_and_cannot_overwrite_input() {
        assert!(validated_output_path("relative.pdf").is_err());
        assert!(validated_output_path("C:\\out\\file.txt").is_err());
        let input = PathBuf::from("C:\\docs\\a.pdf");
        assert!(reject_overwrite(std::slice::from_ref(&input), &input).is_err());
    }

    #[test]
    fn envelope_rejects_unknown_fields() {
        let parsed = serde_json::from_str::<Envelope>(
            r#"{"protocolVersion":1,"messageType":"x","requestId":"1","payload":{},"extra":true}"#,
        );
        assert!(parsed.is_err());
    }
}
