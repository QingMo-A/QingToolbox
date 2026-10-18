use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default, deny_unknown_fields)]
pub struct Settings {
    pub ffmpeg_path: String,
    pub libre_office_path: String,
    pub output_directory: String,
    pub word_engine: String,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            ffmpeg_path: String::new(),
            libre_office_path: String::new(),
            output_directory: String::new(),
            word_engine: "auto".into(),
        }
    }
}

#[derive(Debug, Clone, Copy, Deserialize, Serialize, PartialEq)]
#[serde(rename_all = "kebab-case")]
pub enum Target {
    Png,
    Jpg,
    Webp,
    Bmp,
    Mp3,
    Wav,
    Flac,
    M4a,
    WordPdf,
    PdfWord,
}
impl Target {
    pub fn extension(self) -> &'static str {
        match self {
            Self::Png => "png",
            Self::Jpg => "jpg",
            Self::Webp => "webp",
            Self::Bmp => "bmp",
            Self::Mp3 => "mp3",
            Self::Wav => "wav",
            Self::Flac => "flac",
            Self::M4a => "m4a",
            Self::WordPdf => "pdf",
            Self::PdfWord => "docx",
        }
    }
    pub fn accepts(self, path: &Path) -> bool {
        let ext = path
            .extension()
            .and_then(|e| e.to_str())
            .unwrap_or("")
            .to_ascii_lowercase();
        match self {
            Self::Png | Self::Jpg | Self::Webp | Self::Bmp => {
                matches!(ext.as_str(), "png" | "jpg" | "jpeg" | "webp" | "bmp")
            }
            Self::Mp3 | Self::Wav | Self::Flac | Self::M4a => matches!(
                ext.as_str(),
                "mp3"
                    | "wav"
                    | "flac"
                    | "m4a"
                    | "aac"
                    | "ogg"
                    | "opus"
                    | "wma"
                    | "mp4"
                    | "mkv"
                    | "mov"
            ),
            Self::WordPdf => matches!(ext.as_str(), "doc" | "docx"),
            Self::PdfWord => ext == "pdf",
        }
    }
    pub fn is_audio(self) -> bool {
        matches!(self, Self::Mp3 | Self::Wav | Self::Flac | Self::M4a)
    }
    pub fn size_limit(self) -> u64 {
        if self.is_audio() {
            8 * 1024 * 1024 * 1024
        } else if matches!(self, Self::WordPdf | Self::PdfWord) {
            64 * 1024 * 1024
        } else {
            128 * 1024 * 1024
        }
    }
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default, deny_unknown_fields)]
pub struct Options {
    pub quality: u8,
    pub max_width: u32,
    pub background: String,
    pub audio_bitrate: u32,
}
impl Default for Options {
    fn default() -> Self {
        Self {
            quality: 90,
            max_width: 0,
            background: "#ffffff".into(),
            audio_bitrate: 192,
        }
    }
}
impl Options {
    pub fn validate(&self) -> Result<(), ConvertError> {
        if !(1..=100).contains(&self.quality)
            || self.max_width > 16384
            || ![96, 128, 192, 256, 320].contains(&self.audio_bitrate)
            || self.background_rgb().is_none()
        {
            return Err(ConvertError::new("optionsInvalid", "转换参数无效。"));
        }
        Ok(())
    }
    pub fn background_rgb(&self) -> Option<[u8; 3]> {
        let s = self.background.strip_prefix('#')?;
        if s.len() != 6 || !s.bytes().all(|v| v.is_ascii_hexdigit()) {
            return None;
        }
        Some([
            u8::from_str_radix(&s[0..2], 16).ok()?,
            u8::from_str_radix(&s[2..4], 16).ok()?,
            u8::from_str_radix(&s[4..6], 16).ok()?,
        ])
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FileView {
    pub id: String,
    pub name: String,
    pub size: u64,
    pub status: String,
    pub progress: Option<f64>,
    pub message: String,
    pub output_path: Option<String>,
    #[serde(skip)]
    pub path: PathBuf,
}
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct EngineView {
    pub word_available: bool,
    pub ffmpeg_path: Option<String>,
    pub libre_office_path: Option<String>,
}
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct State {
    pub files: Vec<FileView>,
    pub busy: bool,
    pub cancelling: bool,
    pub settings: Settings,
    pub engines: EngineView,
    pub message: String,
}
#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Task {
    pub input_path: PathBuf,
    pub output_path: PathBuf,
    pub target: Target,
    pub options: Options,
    pub settings: Settings,
    pub module_directory: PathBuf,
    pub job_name: String,
}
#[derive(Debug, Serialize)]
pub struct ConvertError {
    pub code: String,
    pub message: String,
}
impl ConvertError {
    pub fn new(code: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            code: code.into(),
            message: message.into(),
        }
    }
    pub fn cancelled() -> Self {
        Self::new("cancelled", "已取消转换。")
    }
}
impl From<std::io::Error> for ConvertError {
    fn from(e: std::io::Error) -> Self {
        eprintln!("[convert] file I/O failed: {:?}", e.kind());
        Self::new(
            "fileIoFailed",
            "文件读写失败，请检查权限、磁盘空间或文件是否被占用。",
        )
    }
}
pub fn valid_path(value: &str) -> bool {
    !value.is_empty() && value.len() <= 32767 && !value.contains('\0')
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn target_filters_are_case_insensitive_and_separate() {
        assert!(Target::WordPdf.accepts(Path::new("test.DOCX")));
        assert!(!Target::WordPdf.accepts(Path::new("test.pdf")));
        assert!(Target::PdfWord.accepts(Path::new("test.PDF")));
        assert!(!Target::Png.accepts(Path::new("test.svg")));
    }
    #[test]
    fn options_reject_unicode_colors_and_invalid_bounds() {
        let mut options = Options::default();
        options.background = "#中文颜色".into();
        assert!(options.validate().is_err());
        options.background = "#ffffff".into();
        options.max_width = 16385;
        assert!(options.validate().is_err());
    }
}
