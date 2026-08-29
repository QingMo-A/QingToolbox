use std::{
    collections::BTreeSet,
    fs::{self, File, OpenOptions},
    io::{Read, Write},
    path::{Path, PathBuf},
    time::{SystemTime, UNIX_EPOCH},
};

use serde::{Deserialize, Serialize};
use zip::ZipArchive;

use crate::{
    modules::discover_modules,
    paths::{user_modules_root, ModuleRoot, ModuleSource},
    valid_module_id,
};

const MAX_ARCHIVE_BYTES: u64 = 256 * 1024 * 1024;
const MAX_ENTRIES: usize = 2048;
const MAX_FILE_BYTES: u64 = 128 * 1024 * 1024;
const MAX_TOTAL_UNCOMPRESSED: u64 = 256 * 1024 * 1024;
const MAX_PATH_LENGTH: usize = 240;
const MAX_PATH_DEPTH: usize = 16;
const MAX_COMPRESSION_RATIO: u64 = 200;
const MAX_MANIFEST_BYTES: usize = 64 * 1024;

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ModuleImportResult {
    pub id: String,
    pub name: String,
    pub version: String,
}

#[derive(Debug)]
pub struct ImportError {
    pub code: &'static str,
    pub message: String,
}

impl std::fmt::Display for ImportError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl std::error::Error for ImportError {}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ManifestIdentity {
    id: Option<String>,
}

#[derive(Debug)]
struct ArchivePlan {
    module_id: String,
    manifest: Vec<u8>,
}

/// Import a new Tauri process-profile module into the backend-owned user root.
/// The operation only extracts and validates files; it never starts the
/// resulting executable. Existing module directories are never overwritten.
pub fn import_qmod(source_path: &str) -> Result<ModuleImportResult, ImportError> {
    let modules_root = user_modules_root().ok_or_else(|| ImportError {
        code: "moduleRootUnavailable",
        message: "用户模块目录不可用。".to_string(),
    })?;
    import_qmod_into(source_path, &modules_root)
}

/// Import a package into an explicit module root.  Keeping the root as an
/// argument makes the filesystem boundary deterministic in tests and avoids
/// ever making the test suite mutate the user's real module directory.
fn import_qmod_into(
    source_path: &str,
    modules_root: &Path,
) -> Result<ModuleImportResult, ImportError> {
    let source = canonical_source(source_path)?;
    let plan = inspect_archive(&source)?;
    let manifest = parse_manifest(&plan.manifest)?;
    let module_id = manifest
        .id
        .as_deref()
        .map(str::trim)
        .filter(|value| valid_module_id(value))
        .ok_or_else(|| ImportError {
            code: "manifestInvalid",
            message: "模块 manifest 的 id 无效。".to_string(),
        })?
        .to_string();

    if module_id != plan.module_id {
        return Err(ImportError {
            code: "manifestInvalid",
            message: "模块 manifest 身份校验失败。".to_string(),
        });
    }

    fs::create_dir_all(modules_root).map_err(|error| ImportError {
        code: "moduleRootUnavailable",
        message: format!("无法创建用户模块目录：{error}"),
    })?;
    let canonical_root = fs::canonicalize(modules_root).map_err(|error| ImportError {
        code: "moduleRootUnavailable",
        message: format!("无法解析用户模块目录：{error}"),
    })?;
    let destination = canonical_root.join(&module_id);
    if fs::symlink_metadata(&destination).is_ok() {
        return Err(ImportError {
            code: "moduleAlreadyInstalled",
            message: "相同模块已存在，请先移除旧版本。".to_string(),
        });
    }

    let staging_parent = canonical_root.join(format!(".qmod-import-{}", unique_suffix()));
    fs::create_dir(&staging_parent).map_err(|error| ImportError {
        code: "moduleStagingUnavailable",
        message: format!("无法创建模块临时目录：{error}"),
    })?;
    let _cleanup = TempDirGuard::new(staging_parent.clone());
    let staging_module = staging_parent.join(&module_id);
    fs::create_dir(&staging_module).map_err(|error| ImportError {
        code: "moduleStagingUnavailable",
        message: format!("无法创建模块临时目录：{error}"),
    })?;

    extract_archive(&source, &staging_module)?;
    let discovered = discover_modules(&[ModuleRoot {
        source: ModuleSource::User,
        path: staging_parent.clone(),
    }]);
    let summary = discovered
        .payload
        .modules
        .into_iter()
        .find(|summary| summary.id == module_id)
        .ok_or_else(|| ImportError {
            code: "manifestInvalid",
            message: "模块清单未能通过新宿主校验。".to_string(),
        })?;
    if !summary.valid || !discovered.records.contains_key(&module_id) {
        let detail = summary
            .issues
            .first()
            .map(|issue| issue.message.clone())
            .unwrap_or_else(|| "模块清单未能通过新宿主校验。".to_string());
        return Err(ImportError {
            code: "manifestInvalid",
            message: detail,
        });
    }

    if fs::symlink_metadata(&destination).is_ok() {
        return Err(ImportError {
            code: "moduleAlreadyInstalled",
            message: "相同模块已存在，请先移除旧版本。".to_string(),
        });
    }
    fs::rename(&staging_module, &destination).map_err(|error| ImportError {
        code: "moduleInstallFailed",
        message: format!("无法发布模块：{error}"),
    })?;

    Ok(ModuleImportResult {
        id: summary.id,
        name: summary.name,
        version: summary.version,
    })
}

fn canonical_source(raw: &str) -> Result<PathBuf, ImportError> {
    let trimmed = raw.trim();
    if trimmed.is_empty() || trimmed.contains('\0') {
        return Err(ImportError {
            code: "packagePathInvalid",
            message: "模块包路径无效。".to_string(),
        });
    }
    let input = Path::new(trimmed);
    if !input
        .extension()
        .and_then(|extension| extension.to_str())
        .is_some_and(|extension| extension.eq_ignore_ascii_case("qmod"))
    {
        return Err(ImportError {
            code: "packageTypeUnsupported",
            message: "请选择 .qmod 模块包。".to_string(),
        });
    }
    let canonical = fs::canonicalize(input).map_err(|error| ImportError {
        code: "packageUnavailable",
        message: format!("无法读取模块包：{error}"),
    })?;
    let metadata = fs::metadata(&canonical).map_err(|error| ImportError {
        code: "packageUnavailable",
        message: format!("无法读取模块包：{error}"),
    })?;
    if !metadata.is_file() {
        return Err(ImportError {
            code: "packageUnavailable",
            message: "模块包不是普通文件。".to_string(),
        });
    }
    if metadata.len() > MAX_ARCHIVE_BYTES {
        return Err(ImportError {
            code: "packageTooLarge",
            message: "模块包超过 256 MiB 限制。".to_string(),
        });
    }
    Ok(canonical)
}

fn inspect_archive(path: &Path) -> Result<ArchivePlan, ImportError> {
    let file = File::open(path).map_err(|error| ImportError {
        code: "packageUnavailable",
        message: format!("无法打开模块包：{error}"),
    })?;
    let mut archive = ZipArchive::new(file).map_err(|error| ImportError {
        code: "packageInvalid",
        message: format!("模块包不是有效 ZIP：{error}"),
    })?;
    if archive.is_empty() || archive.len() > MAX_ENTRIES {
        return Err(ImportError {
            code: "packageEntriesInvalid",
            message: "模块包条目数量超出限制。".to_string(),
        });
    }

    let mut names = BTreeSet::new();
    let mut total_size = 0_u64;
    let mut manifest = None;
    for index in 0..archive.len() {
        let mut entry = archive.by_index(index).map_err(|error| ImportError {
            code: "packageInvalid",
            message: format!("无法读取模块包条目：{error}"),
        })?;
        let relative = validate_entry(&entry)?;
        let key = relative
            .to_string_lossy()
            .replace('\\', "/")
            .to_ascii_lowercase();
        if !names.insert(key) {
            return Err(ImportError {
                code: "packageDuplicateEntry",
                message: "模块包包含重复文件。".to_string(),
            });
        }
        if entry.is_dir() {
            continue;
        }
        validate_size(&entry)?;
        total_size = total_size
            .checked_add(entry.size())
            .ok_or_else(|| ImportError {
                code: "packageTooLarge",
                message: "模块包展开大小超出限制。".to_string(),
            })?;
        if total_size > MAX_TOTAL_UNCOMPRESSED {
            return Err(ImportError {
                code: "packageTooLarge",
                message: "模块包展开大小超过 256 MiB 限制。".to_string(),
            });
        }
        if relative == Path::new("module.json") {
            if entry.size() as usize > MAX_MANIFEST_BYTES {
                return Err(ImportError {
                    code: "manifestTooLarge",
                    message: "module.json 超过 64 KiB 限制。".to_string(),
                });
            }
            let mut bytes = Vec::with_capacity(entry.size() as usize);
            entry.read_to_end(&mut bytes).map_err(|error| ImportError {
                code: "manifestUnreadable",
                message: format!("无法读取 module.json：{error}"),
            })?;
            if manifest.replace(bytes).is_some() {
                return Err(ImportError {
                    code: "packageDuplicateEntry",
                    message: "模块包包含多个 module.json。".to_string(),
                });
            }
        }
    }
    let manifest = manifest.ok_or_else(|| ImportError {
        code: "manifestMissing",
        message: "模块包缺少根目录 module.json。".to_string(),
    })?;
    let identity = parse_manifest(&manifest)?;
    let module_id = identity
        .id
        .as_deref()
        .map(str::trim)
        .filter(|value| valid_module_id(value))
        .ok_or_else(|| ImportError {
            code: "manifestInvalid",
            message: "模块 manifest 的 id 无效。".to_string(),
        })?
        .to_string();
    Ok(ArchivePlan {
        module_id,
        manifest,
    })
}

fn parse_manifest(bytes: &[u8]) -> Result<ManifestIdentity, ImportError> {
    serde_json::from_slice(bytes).map_err(|error| ImportError {
        code: "manifestInvalid",
        message: format!("module.json 不是有效 JSON：{error}"),
    })
}

fn extract_archive(source: &Path, destination: &Path) -> Result<(), ImportError> {
    let file = File::open(source).map_err(|error| ImportError {
        code: "packageUnavailable",
        message: format!("无法打开模块包：{error}"),
    })?;
    let mut archive = ZipArchive::new(file).map_err(|error| ImportError {
        code: "packageInvalid",
        message: format!("模块包不是有效 ZIP：{error}"),
    })?;
    let mut names = BTreeSet::new();
    for index in 0..archive.len() {
        let mut entry = archive.by_index(index).map_err(|error| ImportError {
            code: "packageInvalid",
            message: format!("无法读取模块包条目：{error}"),
        })?;
        let relative = validate_entry(&entry)?;
        let key = relative
            .to_string_lossy()
            .replace('\\', "/")
            .to_ascii_lowercase();
        if !names.insert(key) {
            return Err(ImportError {
                code: "packageDuplicateEntry",
                message: "模块包包含重复文件。".to_string(),
            });
        }
        if entry.is_dir() {
            continue;
        }
        validate_size(&entry)?;
        let target = destination.join(&relative);
        if let Some(parent) = target.parent() {
            fs::create_dir_all(parent).map_err(|error| ImportError {
                code: "moduleStagingUnavailable",
                message: format!("无法创建模块资源目录：{error}"),
            })?;
        }
        let mut output = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&target)
            .map_err(|error| ImportError {
                code: "moduleStagingUnavailable",
                message: format!("无法写入模块资源：{error}"),
            })?;
        let expected_size = entry.size();
        copy_limited(&mut entry, &mut output, expected_size)?;
        output.flush().map_err(|error| ImportError {
            code: "moduleStagingUnavailable",
            message: format!("无法刷新模块资源：{error}"),
        })?;
    }
    Ok(())
}

fn validate_entry(entry: &zip::read::ZipFile<'_>) -> Result<PathBuf, ImportError> {
    let name = entry.name();
    if name.is_empty()
        || name.len() > MAX_PATH_LENGTH
        || name.contains(['\\', '\0'])
        || name.starts_with('/')
        || name.contains(':')
    {
        return Err(ImportError {
            code: "packagePathInvalid",
            message: "模块包包含无效路径。".to_string(),
        });
    }
    if entry.is_symlink() || entry.encrypted() {
        return Err(ImportError {
            code: "packageEntryUnsupported",
            message: "模块包不允许符号链接或加密条目。".to_string(),
        });
    }
    let trimmed = name.strip_suffix('/').unwrap_or(name);
    if trimmed.is_empty() {
        return Err(ImportError {
            code: "packagePathInvalid",
            message: "模块包包含空目录名。".to_string(),
        });
    }
    let components = trimmed.split('/').collect::<Vec<_>>();
    if components.len() > MAX_PATH_DEPTH
        || components.iter().any(|component| {
            component.is_empty()
                || *component == "."
                || *component == ".."
                || component.ends_with('.')
                || component.ends_with(' ')
                || is_reserved_device(component)
        })
    {
        return Err(ImportError {
            code: "packagePathInvalid",
            message: "模块包包含不安全路径。".to_string(),
        });
    }
    let mut relative = PathBuf::new();
    for component in components {
        relative.push(component);
    }
    Ok(relative)
}

fn validate_size(entry: &zip::read::ZipFile<'_>) -> Result<(), ImportError> {
    let size = entry.size();
    if size > MAX_FILE_BYTES {
        return Err(ImportError {
            code: "packageFileTooLarge",
            message: "模块包中的文件超过 128 MiB 限制。".to_string(),
        });
    }
    let compressed = entry.compressed_size();
    if size > 0 && (compressed == 0 || size / compressed.max(1) > MAX_COMPRESSION_RATIO) {
        return Err(ImportError {
            code: "packageCompressionRatioInvalid",
            message: "模块包压缩比超过限制。".to_string(),
        });
    }
    Ok(())
}

fn copy_limited(
    reader: &mut impl Read,
    writer: &mut impl Write,
    expected: u64,
) -> Result<(), ImportError> {
    let mut buffer = [0_u8; 64 * 1024];
    let mut written = 0_u64;
    loop {
        let count = reader.read(&mut buffer).map_err(|error| ImportError {
            code: "moduleStagingUnavailable",
            message: format!("读取模块资源失败：{error}"),
        })?;
        if count == 0 {
            break;
        }
        written = written
            .checked_add(count as u64)
            .ok_or_else(|| ImportError {
                code: "packageTooLarge",
                message: "模块资源大小超出限制。".to_string(),
            })?;
        if written > expected || written > MAX_FILE_BYTES {
            return Err(ImportError {
                code: "packageFileTooLarge",
                message: "模块包展开内容超过限制。".to_string(),
            });
        }
        writer
            .write_all(&buffer[..count])
            .map_err(|error| ImportError {
                code: "moduleStagingUnavailable",
                message: format!("写入模块资源失败：{error}"),
            })?;
    }
    if written != expected {
        return Err(ImportError {
            code: "packageInvalid",
            message: "模块包条目大小校验失败。".to_string(),
        });
    }
    Ok(())
}

fn is_reserved_device(component: &str) -> bool {
    let stem = component
        .split('.')
        .next()
        .unwrap_or_default()
        .to_ascii_lowercase();
    matches!(
        stem.as_str(),
        "con"
            | "prn"
            | "aux"
            | "nul"
            | "com1"
            | "com2"
            | "com3"
            | "com4"
            | "com5"
            | "com6"
            | "com7"
            | "com8"
            | "com9"
            | "lpt1"
            | "lpt2"
            | "lpt3"
            | "lpt4"
            | "lpt5"
            | "lpt6"
            | "lpt7"
            | "lpt8"
            | "lpt9"
    )
}

struct TempDirGuard(Option<PathBuf>);

impl TempDirGuard {
    fn new(path: PathBuf) -> Self {
        Self(Some(path))
    }
}

impl Drop for TempDirGuard {
    fn drop(&mut self) {
        if let Some(path) = self.0.take() {
            let _ = fs::remove_dir_all(path);
        }
    }
}

fn unique_suffix() -> String {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|value| format!("{:x}-{}", value.as_nanos(), std::process::id()))
        .unwrap_or_else(|_| format!("{}", std::process::id()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    use zip::{write::SimpleFileOptions, ZipWriter};

    struct TestDir(PathBuf);

    impl TestDir {
        fn new(label: &str) -> Self {
            let path =
                std::env::temp_dir().join(format!("qingtoolbox-{label}-{}", unique_suffix()));
            fs::create_dir_all(&path).expect("test directory");
            Self(path)
        }

        fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Drop for TestDir {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn write_qmod(path: &Path, entries: &[(&str, &[u8])]) {
        let file = File::create(path).expect("package file");
        let mut writer = ZipWriter::new(file);
        let options = SimpleFileOptions::default();
        for (name, bytes) in entries {
            writer.start_file(name, options).expect("zip entry");
            writer.write_all(bytes).expect("zip bytes");
        }
        writer.finish().expect("finish zip");
    }

    #[test]
    fn archive_paths_fail_closed() {
        assert!(is_reserved_device("CON.txt"));
        assert!(is_reserved_device("lpt9"));
        assert!(!is_reserved_device("module.json"));
    }

    #[test]
    fn compression_ratio_and_size_limits_are_explicit() {
        assert_eq!(MAX_ENTRIES, 2048);
        assert_eq!(MAX_TOTAL_UNCOMPRESSED, 256 * 1024 * 1024);
        assert_eq!(MAX_COMPRESSION_RATIO, 200);
    }

    #[test]
    fn valid_process_package_is_published_without_execution() {
        let sandbox = TestDir::new("qmod-valid");
        let package = sandbox.path().join("demo.qmod");
        let manifest = br#"{"id":"qing.test","name":"Test Module","version":"1.0.0","entry":"bin/test.exe","runtimeType":"Process","runtimeIsolation":"OutOfProcess","uiKind":"None","loadMode":"Manual","operations":[]}"#;
        write_qmod(
            &package,
            &[
                ("module.json", manifest),
                ("bin/test.exe", b"not-an-executable"),
            ],
        );

        let modules_root = sandbox.path().join("modules");
        let result = import_qmod_into(&package.to_string_lossy(), &modules_root)
            .expect("valid package should import");

        assert_eq!(result.id, "qing.test");
        assert_eq!(result.name, "Test Module");
        assert_eq!(result.version, "1.0.0");
        assert!(modules_root.join("qing.test/module.json").is_file());
        assert!(modules_root.join("qing.test/bin/test.exe").is_file());
        assert!(!modules_root.join(".qmod-import-").exists());
    }

    #[test]
    fn traversal_package_is_rejected_before_publish() {
        let sandbox = TestDir::new("qmod-traversal");
        let package = sandbox.path().join("unsafe.qmod");
        write_qmod(&package, &[("../escape.txt", b"escape")]);
        let modules_root = sandbox.path().join("modules");

        let error = import_qmod_into(&package.to_string_lossy(), &modules_root)
            .expect_err("traversal package must fail");
        assert_eq!(error.code, "packagePathInvalid");
        assert!(!sandbox.path().join("escape.txt").exists());
        assert!(!modules_root.exists());
    }
}
