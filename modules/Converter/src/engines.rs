use crate::model::*;
use docx_rs::{BreakType, Docx, Paragraph, Run};
use image::{
    codecs::jpeg::JpegEncoder, DynamicImage, ImageFormat, ImageReader, Limits, Rgb, RgbImage,
};
use serde_json::{json, Value};
use std::{
    env, fs,
    io::{BufRead, BufReader, Read, Write},
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    thread,
};

pub fn hide_process(command: &mut Command) {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(0x0800_0000);
    }
    #[cfg(not(windows))]
    let _ = command;
}
fn path_from_env(name: &str) -> Option<PathBuf> {
    env::split_paths(&env::var_os("PATH")?)
        .filter(|v| v.is_absolute())
        .map(|p| p.join(name))
        .find(|p| p.is_file())
}
fn configured(value: &str, name: &str, common: &[PathBuf]) -> Option<String> {
    let candidate = if !value.is_empty() {
        Some(PathBuf::from(value))
    } else {
        path_from_env(name).or_else(|| common.iter().find(|p| p.is_file()).cloned())
    };
    candidate
        .filter(|p| p.is_absolute() && p.is_file())
        .map(|p| p.to_string_lossy().into_owned())
}
pub fn detect(settings: &Settings) -> EngineView {
    let roots: Vec<_> = ["ProgramFiles", "ProgramFiles(x86)"]
        .iter()
        .filter_map(env::var_os)
        .map(PathBuf::from)
        .collect();
    let word_available = roots.iter().any(|r| {
        [
            "Microsoft Office/root/Office16/WINWORD.EXE",
            "Microsoft Office/Office16/WINWORD.EXE",
        ]
        .iter()
        .any(|p| r.join(p).is_file())
    });
    let office: Vec<_> = roots
        .iter()
        .map(|r| r.join("LibreOffice/program/soffice.exe"))
        .collect();
    EngineView {
        word_available,
        ffmpeg_path: configured(&settings.ffmpeg_path, "ffmpeg.exe", &[]),
        libre_office_path: configured(&settings.libre_office_path, "soffice.exe", &office),
    }
}
pub fn require_word_engine(
    settings: &Settings,
    view: &EngineView,
) -> Result<&'static str, ConvertError> {
    match settings.word_engine.as_str() {
        "libreoffice" if view.libre_office_path.is_some() => Ok("libreoffice"),
        "word" if view.word_available => Ok("word"),
        "auto" if view.libre_office_path.is_some() => Ok("libreoffice"),
        "auto" if view.word_available => Ok("word"),
        _ => Err(ConvertError::new(
            "officeMissing",
            "Word 转 PDF 需要本机 Microsoft Word，或在引擎设置中配置 LibreOffice。",
        )),
    }
}
pub fn run_task_file(path: &Path) -> Result<String, ConvertError> {
    if fs::metadata(path)?.len() > 65536 {
        return Err(ConvertError::new("taskInvalid", "任务参数过大。"));
    }
    let task: Task = serde_json::from_slice(&fs::read(path)?)
        .map_err(|_| ConvertError::new("taskInvalid", "任务参数无效。"))?;
    task.options.validate()?;
    let metadata = fs::metadata(&task.input_path)?;
    if !metadata.is_file()
        || metadata.len() == 0
        || metadata.len() > task.target.size_limit()
        || !task.target.accepts(&task.input_path)
        || !task.input_path.is_absolute()
        || !task.output_path.is_absolute()
        || task.output_path.exists()
    {
        return Err(ConvertError::new(
            "inputInvalid",
            "输入文件无效、过大或不适合当前转换。",
        ));
    }
    match task.target {
        Target::Png | Target::Jpg | Target::Webp | Target::Bmp => {
            convert_image(&task)?;
            Ok(String::new())
        }
        Target::PdfWord => pdf_to_word(&task),
        Target::WordPdf => {
            word_to_pdf(&task)?;
            Ok(String::new())
        }
        _ => {
            convert_audio(&task)?;
            Ok(String::new())
        }
    }
}
fn convert_image(task: &Task) -> Result<(), ConvertError> {
    let mut reader = ImageReader::open(&task.input_path)?.with_guessed_format()?;
    let mut limits = Limits::default();
    limits.max_image_width = Some(16384);
    limits.max_image_height = Some(16384);
    limits.max_alloc = Some(384 * 1024 * 1024);
    reader.limits(limits);
    let mut image = reader.decode().map_err(|_| {
        ConvertError::new("imageInvalid", "无法解码图片，图片可能损坏或像素尺寸过大。")
    })?;
    if u64::from(image.width()) * u64::from(image.height()) > 80_000_000 {
        return Err(ConvertError::new(
            "imageTooLarge",
            "图片像素过多，首版最多处理 8000 万像素。",
        ));
    }
    let width = task.options.max_width;
    if width > 0 && image.width() > width {
        image = image.resize(width, 16384, image::imageops::FilterType::Lanczos3);
    }
    let mut output = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&task.output_path)?;
    let error = |_: image::ImageError| {
        ConvertError::new("imageEncodeFailed", "图片编码失败。请检查图片与输出格式。")
    };
    match task.target {
        Target::Jpg => {
            let rgb = flatten(&image, task.options.background_rgb().unwrap());
            JpegEncoder::new_with_quality(&mut output, task.options.quality)
                .encode_image(&DynamicImage::ImageRgb8(rgb))
                .map_err(error)?;
        }
        Target::Png => image
            .write_to(&mut output, ImageFormat::Png)
            .map_err(error)?,
        Target::Webp => image
            .write_to(&mut output, ImageFormat::WebP)
            .map_err(error)?,
        Target::Bmp => {
            DynamicImage::ImageRgb8(flatten(&image, task.options.background_rgb().unwrap()))
                .write_to(&mut output, ImageFormat::Bmp)
                .map_err(error)?
        }
        _ => unreachable!(),
    }
    output.sync_all()?;
    Ok(())
}
fn flatten(image: &DynamicImage, background: [u8; 3]) -> RgbImage {
    let rgba = image.to_rgba8();
    RgbImage::from_fn(rgba.width(), rgba.height(), |x, y| {
        let p = rgba.get_pixel(x, y);
        let a = u32::from(p[3]);
        Rgb(std::array::from_fn(|i| {
            ((u32::from(p[i]) * a + u32::from(background[i]) * (255 - a) + 127) / 255) as u8
        }))
    })
}
fn pdf_to_word(task: &Task) -> Result<String, ConvertError> {
    // Parsing is isolated in this short-lived process. A malformed font/parser
    // panic cannot take down the module's protocol or the toolbox host.
    let pages = std::panic::catch_unwind(|| pdf_extract::extract_text_by_pages(&task.input_path))
        .map_err(|_| {
            ConvertError::new("pdfInvalid", "PDF 解析失败，可能含不支持的字体或损坏结构。")
        })?
        .map_err(|_| {
            ConvertError::new(
                "pdfInvalid",
                "无法读取 PDF 文字，请检查文件是否损坏或有密码保护。",
            )
        })?;
    if pages.len() > 500 || pages.iter().map(String::len).sum::<usize>() > 8 * 1024 * 1024 {
        return Err(ConvertError::new(
            "pdfTooLarge",
            "首版最多转换 500 页、800 万字节文本的 PDF。",
        ));
    }
    if !pages.iter().any(|p| p.chars().any(|c| !c.is_whitespace())) {
        return Err(ConvertError::new(
            "pdfNoText",
            "未找到可提取文字。这可能是扫描件或图片型 PDF，首版尚不支持 OCR。",
        ));
    }
    let mut docx = Docx::new().default_size(22);
    let mut empty = 0;
    for (index, page) in pages.iter().enumerate() {
        if index > 0 {
            docx =
                docx.add_paragraph(Paragraph::new().add_run(Run::new().add_break(BreakType::Page)));
        }
        let text = clean_text(page);
        if text.trim().is_empty() {
            empty += 1;
        }
        for line in text.lines() {
            docx = docx.add_paragraph(Paragraph::new().add_run(Run::new().add_text(line)));
        }
    }
    let output = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&task.output_path)?;
    docx.build()
        .pack(output)
        .map_err(|_| ConvertError::new("docxWriteFailed", "无法生成 Word 文档。"))?;
    if empty > 0 {
        Ok(format!(
            "已提取文字；{empty} 页无可提取文字。图片、表格结构和原版式未还原。"
        ))
    } else {
        Ok("已提取可编辑文字；图片、表格结构和原版式未还原。".into())
    }
}
fn clean_text(value: &str) -> String {
    value
        .chars()
        .filter(|c| {
            matches!(c, '\n' | '\r' | '\t')
                || (*c >= '\u{20}' && !matches!(*c, '\u{fffe}' | '\u{ffff}'))
        })
        .collect()
}
fn native_path(path: &Path) -> String {
    let text = path.to_string_lossy();
    if let Some(rest) = text.strip_prefix("\\\\?\\UNC\\") {
        format!("\\\\{rest}")
    } else {
        text.strip_prefix("\\\\?\\").unwrap_or(&text).to_string()
    }
}
fn file_uri(path: &Path) -> String {
    let normal = native_path(path).replace('\\', "/");
    let escaped = normal
        .bytes()
        .map(|b| {
            if b.is_ascii_alphanumeric() || b"/:._-~".contains(&b) {
                char::from(b).to_string()
            } else {
                format!("%{b:02X}")
            }
        })
        .collect::<String>();
    if escaped.starts_with("//") {
        format!("file:{escaped}")
    } else {
        format!("file:///{escaped}")
    }
}
fn word_to_pdf(task: &Task) -> Result<(), ConvertError> {
    let view = detect(&task.settings);
    match require_word_engine(&task.settings, &view)? {
        "libreoffice" => {
            let folder = task.output_path.parent().unwrap();
            let profile = folder.join("office-profile");
            let output = folder.join("office-output");
            fs::create_dir_all(profile.join("user"))?;
            fs::create_dir(&output)?;
            // Isolated profile, no trusted macro locations or persistent user
            // settings. Do not let batch conversion attach to an open office UI.
            fs::write(
                profile.join("user/registrymodifications.xcu"),
                r#"<?xml version="1.0"?><oor:items xmlns:oor="http://openoffice.org/2001/registry"><item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop></item></oor:items>"#,
            )?;
            let mut command = Command::new(view.libre_office_path.unwrap());
            command
                .arg(format!("-env:UserInstallation={}", file_uri(&profile)))
                .args([
                    "--headless",
                    "--nologo",
                    "--nodefault",
                    "--nofirststartwizard",
                    "--convert-to",
                    "pdf:writer_pdf_Export",
                    "--outdir",
                ])
                .arg(native_path(&output))
                .arg(native_path(&task.input_path));
            hide_process(&mut command);
            let result = command.output()?;
            let expected = output.join(format!(
                "{}.pdf",
                task.input_path.file_stem().unwrap().to_string_lossy()
            ));
            if !result.status.success() || !expected.is_file() {
                return Err(ConvertError::new(
                    "officeConversionFailed",
                    "LibreOffice 未生成 PDF，请检查文档、字体或文档密码。",
                ));
            }
            fs::rename(expected, &task.output_path)?;
        }
        "word" => {
            let root = env::var_os("SystemRoot")
                .map(PathBuf::from)
                .unwrap_or_else(|| PathBuf::from("C:/Windows"));
            let script = task.module_directory.join("engine/word-to-pdf.ps1");
            if !script.is_file() {
                return Err(ConvertError::new(
                    "wordHelperMissing",
                    "模块的 Word 转换组件缺失，请重新安装模块。",
                ));
            }
            let config = task.output_path.parent().unwrap().join("word-task.json");
            fs::write(&config, serde_json::to_vec(&json!({"inputPath":native_path(&task.input_path),"outputPath":native_path(&task.output_path),"jobName":task.job_name})).unwrap())?;
            let mut command =
                Command::new(root.join("System32/WindowsPowerShell/v1.0/powershell.exe"));
            command
                .args([
                    "-NoProfile",
                    "-NonInteractive",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                ])
                .arg(script)
                .arg("-ConfigPath")
                .arg(config);
            hide_process(&mut command);
            let result = command.output()?;
            if !result.status.success() {
                let answer: Value = serde_json::from_slice(&result.stdout).unwrap_or(Value::Null);
                let code = answer
                    .get("code")
                    .and_then(Value::as_str)
                    .unwrap_or("wordConversionFailed");
                let message = if code == "wordAlreadyRunning" {
                    "Word 正在运行或未创建独立实例。请先结束当前 Word 会话，或改用 LibreOffice。"
                        .to_string()
                } else {
                    let stage = answer
                        .get("stage")
                        .and_then(Value::as_str)
                        .unwrap_or("unknown");
                    let status = answer
                        .get("hresult")
                        .and_then(Value::as_i64)
                        .unwrap_or_default() as u32;
                    format!("Word 导出失败（{stage} / 0x{status:08X}），请检查激活状态、文档密码或权限。")
                };
                return Err(ConvertError::new(code, message));
            }
        }
        _ => unreachable!(),
    }
    let mut header = [0; 5];
    fs::File::open(&task.output_path)?.read_exact(&mut header)?;
    if &header != b"%PDF-" {
        return Err(ConvertError::new(
            "outputInvalid",
            "文档引擎未生成有效的 PDF。",
        ));
    }
    Ok(())
}
fn audio_args(task: &Task) -> Vec<String> {
    let mut args = vec!["-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-i"]
        .into_iter()
        .map(str::to_string)
        .collect::<Vec<_>>();
    args.push(native_path(&task.input_path));
    args.extend(
        ["-map", "0:a:0", "-vn", "-progress", "pipe:1", "-nostats"]
            .into_iter()
            .map(str::to_string),
    );
    match task.target {
        Target::Mp3 => args.extend([
            "-c:a".into(),
            "libmp3lame".into(),
            "-b:a".into(),
            format!("{}k", task.options.audio_bitrate),
        ]),
        Target::M4a => args.extend([
            "-c:a".into(),
            "aac".into(),
            "-b:a".into(),
            format!("{}k", task.options.audio_bitrate),
        ]),
        Target::Wav => args.extend(["-c:a".into(), "pcm_s16le".into()]),
        Target::Flac => args.extend([
            "-c:a".into(),
            "flac".into(),
            "-compression_level".into(),
            "5".into(),
        ]),
        _ => unreachable!(),
    }
    args.push(native_path(&task.output_path));
    args
}
fn convert_audio(task: &Task) -> Result<(), ConvertError> {
    let executable = detect(&task.settings)
        .ffmpeg_path
        .ok_or_else(|| ConvertError::new("ffmpegMissing", "请先配置 ffmpeg.exe。"))?;
    let probe = Path::new(&executable).with_file_name("ffprobe.exe");
    let duration = if probe.is_file() {
        let mut command = Command::new(probe);
        command
            .args([
                "-v",
                "error",
                "-show_entries",
                "format=duration",
                "-of",
                "default=noprint_wrappers=1:nokey=1",
            ])
            .arg(native_path(&task.input_path));
        hide_process(&mut command);
        command
            .output()
            .ok()
            .and_then(|v| String::from_utf8(v.stdout).ok())
            .and_then(|s| s.trim().parse::<f64>().ok())
            .filter(|n| n.is_finite() && *n > 0.0)
    } else {
        None
    };
    let mut command = Command::new(executable);
    command
        .args(audio_args(task))
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    hide_process(&mut command);
    let mut process = command.spawn()?;
    let stderr = process.stderr.take().unwrap();
    let reader = thread::spawn(move || {
        let mut bytes = Vec::new();
        let mut source = BufReader::new(stderr);
        let _ = Read::by_ref(&mut source)
            .take(65536)
            .read_to_end(&mut bytes);
        let _ = std::io::copy(&mut source, &mut std::io::sink());
        bytes
    });
    for line in BufReader::new(process.stdout.take().unwrap()).lines() {
        let Ok(line) = line else {
            break;
        };
        if let (Some(seconds), Some(value)) = (duration, line.strip_prefix("out_time_us=")) {
            if let Ok(micros) = value.parse::<f64>() {
                println!(
                    "{}",
                    json!({"progress":(micros / 1_000_000.0 / seconds).clamp(0.0, 0.99)})
                );
                let _ = std::io::stdout().flush();
            }
        }
    }
    let status = process.wait()?;
    let details = reader.join().unwrap_or_default();
    if !status.success() {
        let error = String::from_utf8_lossy(&details);
        let message = if error.contains("Unknown encoder") {
            "当前 FFmpeg 构建缺少所选音频编码器，请换用完整的转换引擎。"
        } else if error.contains("matches no streams") {
            "这个文件不包含可转换的音轨。"
        } else {
            "音频转换失败，请检查源文件和 FFmpeg 引擎。"
        };
        return Err(ConvertError::new("audioConversionFailed", message));
    }
    Ok(())
}
pub fn open_directory(path: &Path) -> Result<(), ConvertError> {
    #[cfg(windows)]
    {
        let root = env::var_os("SystemRoot")
            .map(PathBuf::from)
            .unwrap_or_else(|| PathBuf::from("C:/Windows"));
        Command::new(root.join("explorer.exe"))
            .arg(native_path(path))
            .spawn()?;
        Ok(())
    }
    #[cfg(not(windows))]
    {
        let _ = path;
        Err(ConvertError::new("platformUnsupported", "仅支持 Windows。"))
    }
}

#[cfg(windows)]
pub struct ProcessJob(windows_sys::Win32::Foundation::HANDLE);
#[cfg(windows)]
impl ProcessJob {
    pub fn new(name: &str) -> Result<Self, ConvertError> {
        use windows_sys::Win32::System::JobObjects::*;
        let name: Vec<_> = name.encode_utf16().chain(Some(0)).collect();
        unsafe {
            let handle = CreateJobObjectW(std::ptr::null(), name.as_ptr());
            if handle.is_null() {
                return Err(ConvertError::new(
                    "jobUnavailable",
                    "无法建立转换进程隔离。",
                ));
            }
            let result = Self(handle);
            let mut limits: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = std::mem::zeroed();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                | JOB_OBJECT_LIMIT_JOB_MEMORY
                | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;
            limits.JobMemoryLimit = 2 * 1024 * 1024 * 1024;
            if SetInformationJobObject(
                handle,
                JobObjectExtendedLimitInformation,
                &limits as *const _ as *const _,
                std::mem::size_of_val(&limits) as u32,
            ) == 0
            {
                return Err(ConvertError::new(
                    "jobUnavailable",
                    "无法设置转换进程隔离。",
                ));
            }
            Ok(result)
        }
    }
    pub fn assign(&self, child: &Child) -> Result<(), ConvertError> {
        use std::os::windows::io::AsRawHandle;
        unsafe {
            if windows_sys::Win32::System::JobObjects::AssignProcessToJobObject(
                self.0,
                child.as_raw_handle() as _,
            ) == 0
            {
                return Err(ConvertError::new("jobUnavailable", "无法隔离转换进程。"));
            }
        }
        Ok(())
    }
}
#[cfg(windows)]
impl Drop for ProcessJob {
    fn drop(&mut self) {
        unsafe {
            windows_sys::Win32::Foundation::CloseHandle(self.0);
        }
    }
}
#[cfg(not(windows))]
pub struct ProcessJob;
#[cfg(not(windows))]
impl ProcessJob {
    pub fn new(_: &str) -> Result<Self, ConvertError> {
        Ok(Self)
    }
    pub fn assign(&self, _: &Child) -> Result<(), ConvertError> {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn transparent_pixels_are_composited_not_blackened() {
        let image = DynamicImage::ImageRgba8(image::RgbaImage::from_pixel(
            1,
            1,
            image::Rgba([255, 0, 0, 0]),
        ));
        assert_eq!(
            flatten(&image, [255, 255, 255]).get_pixel(0, 0).0,
            [255, 255, 255]
        );
    }
    #[test]
    fn xml_unsafe_controls_are_removed_but_chinese_is_kept() {
        assert_eq!(clean_text("中\0文\u{1}字\n"), "中文字\n");
    }
    #[test]
    fn audio_command_is_fixed_and_paths_are_single_arguments() {
        let task = Task {
            input_path: PathBuf::from("C:/test & file.wav"),
            output_path: PathBuf::from("C:/out.m4a"),
            target: Target::M4a,
            options: Options::default(),
            settings: Settings::default(),
            module_directory: PathBuf::new(),
            job_name: String::new(),
        };
        let args = audio_args(&task);
        assert!(args.iter().any(|s| s.contains("test & file.wav")));
        assert!(!args.contains(&"-y".to_string()));
        assert!(args.contains(&"-n".to_string()));
    }
}
