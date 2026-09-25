use std::{
    fs::{self, File, OpenOptions},
    io::{self, Read, Write},
    path::{Path, PathBuf},
    time::{SystemTime, UNIX_EPOCH},
};

use serde::Serialize;
use sha2::{Digest, Sha256};

use crate::paths::user_data_root;

pub const MAX_FONT_BYTES: u64 = 32 * 1024 * 1024;
const IMPORTED_DIRECTORY_NAME: &str = "Fonts\\Imported";
const RESOURCE_PREFIX: &str = "/user-fonts/";
const SYSTEM_FAMILIES: &[&str] = &[
    "Segoe UI",
    "Microsoft YaHei UI",
    "Consolas",
    "Cascadia Mono",
];

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FontOption {
    pub id: String,
    pub source: String,
    pub display_name: String,
    pub family_name: Option<String>,
    pub resource_url: Option<String>,
}

#[derive(Debug)]
pub struct FontError {
    pub code: &'static str,
    pub message: String,
}

impl std::fmt::Display for FontError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl std::error::Error for FontError {}

/// Return the bounded catalog exposed to the Web UI. System entries are a
/// fixed, safe set of common Windows families; imported entries are discovered
/// only from the host-owned directory and are hash-addressed.
pub fn catalog() -> Vec<FontOption> {
    let mut options = vec![default_option()];
    options.extend(SYSTEM_FAMILIES.iter().map(|family| FontOption {
        id: format!("system:{family}"),
        source: "system".to_string(),
        display_name: (*family).to_string(),
        family_name: Some((*family).to_string()),
        resource_url: None,
    }));

    let Some(directory) = imported_directory() else {
        return options;
    };
    let Ok(entries) = fs::read_dir(directory) else {
        return options;
    };
    let mut imported = entries
        .flatten()
        .filter_map(|entry| imported_option(&entry.path()))
        .collect::<Vec<_>>();
    imported.sort_by(|left, right| left.id.cmp(&right.id));
    options.extend(imported);
    options
}

pub fn default_option() -> FontOption {
    FontOption {
        id: "Default".to_string(),
        source: "default".to_string(),
        display_name: "Default".to_string(),
        family_name: None,
        resource_url: None,
    }
}

pub fn option_for_id(id: &str) -> Option<FontOption> {
    catalog().into_iter().find(|option| option.id == id)
}

/// Normalize a legacy or newly selected font into a catalog entry. Missing or
/// tampered imported files intentionally fall back to the built-in font.
pub fn normalize_selection(
    id: Option<&str>,
    source: Option<&str>,
    family_name: Option<&str>,
) -> FontOption {
    let id = id.unwrap_or_default().trim();
    if id == "Default" || id.is_empty() {
        return default_option();
    }
    if let Some(family) = id.strip_prefix("system:") {
        if SYSTEM_FAMILIES.contains(&family) && source.unwrap_or("system") == "system" {
            return FontOption {
                id: id.to_string(),
                source: "system".to_string(),
                display_name: family.to_string(),
                family_name: Some(family.to_string()),
                resource_url: None,
            };
        }
    }
    if source.unwrap_or_default() == "imported"
        && is_imported_id(id)
        && imported_file_for_id(id).is_some()
    {
        return option_for_id(id).unwrap_or_else(default_option);
    }
    // Legacy settings sometimes only persisted FontId. Accept a syntactically
    // valid system id in that case, but never accept an imported file without
    // an explicit imported source and a verified file.
    if let Some(family) = id.strip_prefix("system:") {
        if SYSTEM_FAMILIES.contains(&family) {
            return FontOption {
                id: id.to_string(),
                source: "system".to_string(),
                display_name: family.to_string(),
                family_name: Some(family.to_string()),
                resource_url: None,
            };
        }
    }
    if let Some(family) = family_name.map(str::trim).filter(|value| !value.is_empty()) {
        if SYSTEM_FAMILIES.contains(&family) {
            return FontOption {
                id: format!("system:{family}"),
                source: "system".to_string(),
                display_name: family.to_string(),
                family_name: Some(family.to_string()),
                resource_url: None,
            };
        }
    }
    default_option()
}

pub fn import_from_path(source_path: &str) -> Result<FontOption, FontError> {
    if source_path.trim().is_empty() {
        return Err(FontError {
            code: "fontPathInvalid",
            message: "字体文件路径为空。".to_string(),
        });
    }
    let source = fs::canonicalize(source_path).map_err(|error| FontError {
        code: "fontUnavailable",
        message: format!("无法读取字体文件：{error}"),
    })?;
    let metadata = fs::metadata(&source).map_err(|error| FontError {
        code: "fontUnavailable",
        message: format!("无法读取字体文件：{error}"),
    })?;
    if !metadata.is_file() || metadata.len() == 0 || metadata.len() > MAX_FONT_BYTES {
        return Err(FontError {
            code: "fontSizeInvalid",
            message: "字体文件大小不在支持范围内。".to_string(),
        });
    }
    let extension = source
        .extension()
        .and_then(|value| value.to_str())
        .map(|value| format!(".{}", value.to_ascii_lowercase()))
        .ok_or_else(|| FontError {
            code: "fontTypeUnsupported",
            message: "仅支持 .ttf、.otf 和 .ttc 字体文件。".to_string(),
        })?;
    if !matches!(extension.as_str(), ".ttf" | ".otf" | ".ttc") {
        return Err(FontError {
            code: "fontTypeUnsupported",
            message: "仅支持 .ttf、.otf 和 .ttc 字体文件。".to_string(),
        });
    }

    let directory = ensure_imported_directory().map_err(|error| FontError {
        code: "fontDirectoryUnavailable",
        message: format!("无法准备字体目录：{error}"),
    })?;
    let temporary = directory.join(format!(
        ".font-import-{}-{}.tmp",
        std::process::id(),
        unique_suffix()
    ));
    let mut input = File::open(&source).map_err(|error| FontError {
        code: "fontUnavailable",
        message: format!("无法打开字体文件：{error}"),
    })?;
    let mut output = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temporary)
        .map_err(|error| FontError {
            code: "fontImportFailed",
            message: format!("无法创建字体临时文件：{error}"),
        })?;
    let mut digest = Sha256::new();
    let mut first_bytes = [0_u8; 4];
    let mut first_count = 0_usize;
    let mut buffer = [0_u8; 64 * 1024];
    let mut total = 0_u64;
    let copy_result = (|| -> io::Result<()> {
        loop {
            let read = input.read(&mut buffer)?;
            if read == 0 {
                break;
            }
            if first_count < first_bytes.len() {
                let count = (first_bytes.len() - first_count).min(read);
                first_bytes[first_count..first_count + count].copy_from_slice(&buffer[..count]);
                first_count += count;
            }
            total = total.saturating_add(read as u64);
            if total > MAX_FONT_BYTES {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "font is too large",
                ));
            }
            digest.update(&buffer[..read]);
            output.write_all(&buffer[..read])?;
        }
        output.flush()
    })();
    drop(output);
    drop(input);
    if let Err(error) = copy_result {
        let _ = fs::remove_file(&temporary);
        return Err(FontError {
            code: "fontImportFailed",
            message: format!("复制字体文件失败：{error}"),
        });
    }
    if !valid_font_signature(&first_bytes, &extension) {
        let _ = fs::remove_file(&temporary);
        return Err(FontError {
            code: "fontFormatInvalid",
            message: "所选文件不是可识别的字体文件。".to_string(),
        });
    }
    let hash = format!("{:x}", digest.finalize());
    let destination = directory.join(format!("font-{hash}{extension}"));
    if destination.exists() {
        let _ = fs::remove_file(&temporary);
        if !verify_file_hash(&destination, &hash).unwrap_or(false) {
            return Err(FontError {
                code: "fontIntegrityFailed",
                message: "已存在的字体文件校验失败。".to_string(),
            });
        }
    } else if let Err(error) = fs::rename(&temporary, &destination) {
        let _ = fs::remove_file(&temporary);
        return Err(FontError {
            code: "fontImportFailed",
            message: format!("无法发布字体文件：{error}"),
        });
    }
    option_for_id(&format!("imported:{hash}")).ok_or_else(|| FontError {
        code: "fontImportFailed",
        message: "字体文件已保存，但无法建立安全字体记录。".to_string(),
    })
}

/// Read a route owned by the qfont custom protocol. The file is re-hashed on
/// every read so a replaced or tampered imported font is never served.
pub fn read_asset(route: &str) -> Option<(Vec<u8>, &'static str)> {
    let name = route.strip_prefix(RESOURCE_PREFIX)?;
    if name.contains('/') || name.contains('\\') {
        return None;
    }
    let extension = Path::new(name).extension()?.to_str()?.to_ascii_lowercase();
    if !matches!(extension.as_str(), "ttf" | "otf" | "ttc") {
        return None;
    }
    let stem = Path::new(name).file_stem()?.to_str()?;
    let hash = stem.strip_prefix("font-")?;
    if !is_sha256(hash) {
        return None;
    }
    let path = imported_file_for_hash(hash, &extension)?;
    let bytes = fs::read(&path).ok()?;
    if bytes.is_empty() || bytes.len() as u64 > MAX_FONT_BYTES || !verify_bytes(&bytes, hash) {
        return None;
    }
    let content_type = match extension.as_str() {
        "ttf" => "font/ttf",
        "otf" => "font/otf",
        "ttc" => "font/collection",
        _ => return None,
    };
    Some((bytes, content_type))
}

fn imported_option(path: &Path) -> Option<FontOption> {
    let extension = path.extension()?.to_str()?.to_ascii_lowercase();
    if !matches!(extension.as_str(), "ttf" | "otf" | "ttc") {
        return None;
    }
    let stem = path.file_stem()?.to_str()?.strip_prefix("font-")?;
    if !is_sha256(stem) || !path.is_file() || !verify_file_hash(path, stem).ok()? {
        return None;
    }
    let hash = stem.to_ascii_lowercase();
    Some(FontOption {
        id: format!("imported:{hash}"),
        source: "imported".to_string(),
        display_name: format!("Imported Font · {}", &hash[..8]),
        family_name: None,
        resource_url: Some(format!(
            "{}/user-fonts/font-{hash}.{extension}",
            if cfg!(windows) {
                "http://qfont.localhost"
            } else {
                "qfont://localhost"
            }
        )),
    })
}

fn imported_file_for_id(id: &str) -> Option<PathBuf> {
    let hash = id.strip_prefix("imported:")?;
    imported_file_for_hash_any_extension(hash)
}

fn imported_file_for_hash_any_extension(hash: &str) -> Option<PathBuf> {
    let directory = imported_directory()?;
    ["ttf", "otf", "ttc"].iter().find_map(|extension| {
        let path = directory.join(format!("font-{hash}.{extension}"));
        (path.is_file() && verify_file_hash(&path, hash).ok()?).then_some(path)
    })
}

fn imported_file_for_hash(hash: &str, extension: &str) -> Option<PathBuf> {
    if !is_sha256(hash) {
        return None;
    }
    let path =
        imported_directory()?.join(format!("font-{}.{}", hash.to_ascii_lowercase(), extension));
    (path.is_file() && verify_file_hash(&path, hash).ok()?).then_some(path)
}

fn imported_directory() -> Option<PathBuf> {
    Some(user_data_root()?.join(IMPORTED_DIRECTORY_NAME))
}

fn ensure_imported_directory() -> io::Result<PathBuf> {
    let directory = imported_directory()
        .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "user data root unavailable"))?;
    fs::create_dir_all(&directory)?;
    let root = user_data_root()
        .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "user data root unavailable"))?;
    let canonical_root = fs::canonicalize(root)?;
    let canonical_directory = fs::canonicalize(&directory)?;
    if !canonical_directory.starts_with(&canonical_root) {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "font directory escaped the user data root",
        ));
    }
    Ok(directory)
}

fn verify_file_hash(path: &Path, expected: &str) -> io::Result<bool> {
    if !is_safe_imported_path(path) {
        return Ok(false);
    }
    let metadata = fs::metadata(path)?;
    if !metadata.is_file() || metadata.len() == 0 || metadata.len() > MAX_FONT_BYTES {
        return Ok(false);
    }
    let mut file = File::open(path)?;
    let mut digest = Sha256::new();
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let read = file.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        digest.update(&buffer[..read]);
    }
    Ok(format!("{:x}", digest.finalize()).eq_ignore_ascii_case(expected))
}

fn is_safe_imported_path(path: &Path) -> bool {
    let Some(directory) = imported_directory() else {
        return false;
    };
    let Ok(root) = fs::canonicalize(directory) else {
        return false;
    };
    let Ok(candidate) = fs::canonicalize(path) else {
        return false;
    };
    candidate.starts_with(root)
}

fn verify_bytes(bytes: &[u8], expected: &str) -> bool {
    let mut digest = Sha256::new();
    digest.update(bytes);
    format!("{:x}", digest.finalize()).eq_ignore_ascii_case(expected)
}

fn valid_font_signature(bytes: &[u8; 4], extension: &str) -> bool {
    matches!(
        (extension, bytes),
        (".ttf", [0, 1, 0, 0])
            | (".ttf", [b't', b'r', b'u', b'e'])
            | (".otf", [b'O', b'T', b'T', b'O'])
            | (".ttc", [b't', b't', b'c', b'f'])
    )
}

fn is_imported_id(value: &str) -> bool {
    value.strip_prefix("imported:").is_some_and(is_sha256)
}

fn is_sha256(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
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

    #[test]
    fn catalog_keeps_default_and_fixed_system_choices_safe() {
        let options = catalog();
        assert_eq!(
            options.first().map(|option| option.id.as_str()),
            Some("Default")
        );
        assert!(options.iter().any(|option| option.id == "system:Segoe UI"));
        assert!(options
            .iter()
            .all(|option| !option.id.contains(['/', '\\']) && option.display_name.len() <= 128));
    }

    #[test]
    fn selection_falls_back_for_unknown_or_missing_imported_files() {
        assert_eq!(
            normalize_selection(Some("system:Unknown"), None, None).id,
            "Default"
        );
        assert_eq!(
            normalize_selection(
                Some("imported:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                Some("imported"),
                None,
            )
            .id,
            "Default"
        );
        assert_eq!(
            normalize_selection(Some("system:Consolas"), Some("system"), None).source,
            "system"
        );
    }

    #[test]
    fn font_signatures_are_extension_bound() {
        assert!(valid_font_signature(&[0, 1, 0, 0], ".ttf"));
        assert!(valid_font_signature(b"OTTO", ".otf"));
        assert!(valid_font_signature(b"ttcf", ".ttc"));
        assert!(!valid_font_signature(b"OTTO", ".ttf"));
    }
}
