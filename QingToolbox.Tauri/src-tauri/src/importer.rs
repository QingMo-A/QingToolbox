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
    #[serde(default)]
    pub replaced: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub previous_version: Option<String>,
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
    version: Option<String>,
}

#[derive(Debug)]
struct ArchivePlan {
    module_id: String,
    manifest: Vec<u8>,
    metadata: Option<PackageMetadata>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
#[serde(deny_unknown_fields)]
struct PackageMetadata {
    schema_version: Option<u16>,
    module_id: Option<String>,
    version: Option<String>,
    module_api_version: Option<String>,
    entry_manifest: Option<String>,
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

/// Replace an already-installed user module with a validated package. The
/// destination must be a direct child of the backend-owned user module root;
/// bundled modules and reparse-point destinations are never replaced. The
/// staged tree is atomically swapped into place and the old tree is retained
/// only until the new rename succeeds, so a failed update can roll back.
pub fn update_qmod(
    source_path: &str,
    expected_module_id: &str,
) -> Result<ModuleImportResult, ImportError> {
    if !valid_module_id(expected_module_id) {
        return Err(ImportError {
            code: "moduleIdInvalid",
            message: "模块 id 无效。".to_string(),
        });
    }
    let modules_root = user_modules_root().ok_or_else(|| ImportError {
        code: "moduleRootUnavailable",
        message: "用户模块目录不可用。".to_string(),
    })?;
    update_qmod_into(source_path, expected_module_id, &modules_root)
}

fn update_qmod_into(
    source_path: &str,
    expected_module_id: &str,
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
    if module_id != plan.module_id || module_id != expected_module_id {
        return Err(ImportError {
            code: "manifestInvalid",
            message: "更新包的模块身份与目标不匹配。".to_string(),
        });
    }
    validate_package_metadata(
        plan.metadata.as_ref(),
        &module_id,
        manifest.version.as_deref(),
    )?;

    fs::create_dir_all(modules_root).map_err(|error| ImportError {
        code: "moduleRootUnavailable",
        message: format!("无法创建用户模块目录：{error}"),
    })?;
    let canonical_root = fs::canonicalize(modules_root).map_err(|error| ImportError {
        code: "moduleRootUnavailable",
        message: format!("无法解析用户模块目录：{error}"),
    })?;
    let destination = canonical_root.join(&module_id);
    let destination_metadata = fs::symlink_metadata(&destination).map_err(|error| {
        if error.kind() == std::io::ErrorKind::NotFound {
            ImportError {
                code: "moduleNotInstalled",
                message: "目标模块尚未安装，不能执行覆盖更新。".to_string(),
            }
        } else {
            ImportError {
                code: "moduleUnavailable",
                message: format!("无法读取目标模块：{error}"),
            }
        }
    })?;
    if !destination_metadata.is_dir() || is_reparse_point(&destination_metadata) {
        return Err(ImportError {
            code: "moduleDestinationInvalid",
            message: "目标模块目录不是受控的普通目录。".to_string(),
        });
    }
    let canonical_destination = fs::canonicalize(&destination).map_err(|error| ImportError {
        code: "moduleDestinationInvalid",
        message: format!("无法解析目标模块目录：{error}"),
    })?;
    if canonical_destination.parent() != Some(canonical_root.as_path())
        || canonical_destination
            .file_name()
            .and_then(|name| name.to_str())
            != Some(&module_id)
    {
        return Err(ImportError {
            code: "moduleDestinationInvalid",
            message: "目标模块目录越过了用户模块根。".to_string(),
        });
    }
    let previous_version = read_existing_version(&canonical_destination);

    let staging_parent = canonical_root.join(format!(".qmod-update-{}", unique_suffix()));
    fs::create_dir(&staging_parent).map_err(|error| ImportError {
        code: "moduleStagingUnavailable",
        message: format!("无法创建模块更新临时目录：{error}"),
    })?;
    let _cleanup = TempDirGuard::new(staging_parent.clone());
    let staging_module = staging_parent.join(&module_id);
    fs::create_dir(&staging_module).map_err(|error| ImportError {
        code: "moduleStagingUnavailable",
        message: format!("无法创建模块更新临时目录：{error}"),
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

    let backup = canonical_root.join(format!(".qmod-backup-{}", unique_suffix()));
    fs::rename(&canonical_destination, &backup).map_err(|error| ImportError {
        code: "moduleUpdateFailed",
        message: format!("无法暂存旧模块：{error}"),
    })?;
    if let Err(error) = fs::rename(&staging_module, &destination) {
        let _ = fs::rename(&backup, &destination);
        return Err(ImportError {
            code: "moduleUpdateFailed",
            message: format!("无法发布更新后的模块：{error}"),
        });
    }
    if let Err(error) = fs::remove_dir_all(&backup) {
        // The new module is already atomically visible. Keep it installed and
        // report the cleanup issue instead of silently deleting the new tree.
        return Err(ImportError {
            code: "moduleUpdateCleanupFailed",
            message: format!("模块已更新，但旧版本清理失败：{error}"),
        });
    }

    Ok(ModuleImportResult {
        id: summary.id,
        name: summary.name,
        version: summary.version,
        replaced: true,
        previous_version,
    })
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
    validate_package_metadata(
        plan.metadata.as_ref(),
        &module_id,
        manifest.version.as_deref(),
    )?;

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
        replaced: false,
        previous_version: None,
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
    let mut metadata = None;
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
        } else if relative == Path::new("qmod.json") {
            if entry.size() > 16 * 1024 {
                return Err(ImportError {
                    code: "packageMetadataTooLarge",
                    message: "qmod.json 超过 16 KiB 限制。".to_string(),
                });
            }
            let mut bytes = Vec::with_capacity(entry.size() as usize);
            entry.read_to_end(&mut bytes).map_err(|error| ImportError {
                code: "packageMetadataInvalid",
                message: format!("无法读取 qmod.json：{error}"),
            })?;
            let parsed =
                serde_json::from_slice::<PackageMetadata>(&bytes).map_err(|error| ImportError {
                    code: "packageMetadataInvalid",
                    message: format!("qmod.json 不是有效 JSON：{error}"),
                })?;
            if metadata.replace(parsed).is_some() {
                return Err(ImportError {
                    code: "packageDuplicateEntry",
                    message: "模块包包含多个 qmod.json。".to_string(),
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
        metadata,
    })
}

fn validate_package_metadata(
    metadata: Option<&PackageMetadata>,
    module_id: &str,
    manifest_version: Option<&str>,
) -> Result<(), ImportError> {
    let Some(metadata) = metadata else {
        // Older process-profile packages did not carry this envelope. The
        // manifest remains the minimum identity contract, so they can still
        // be imported; packages produced by the current packer always include
        // and are checked against it.
        return Ok(());
    };
    let valid = metadata.schema_version == Some(1)
        && metadata.module_id.as_deref() == Some(module_id)
        && metadata
            .version
            .as_deref()
            .zip(manifest_version)
            .is_some_and(|(left, right)| left.trim() == right.trim())
        && metadata.module_api_version.as_deref() == Some("tauri-process-v1")
        && metadata.entry_manifest.as_deref() == Some("module.json");
    if valid {
        Ok(())
    } else {
        Err(ImportError {
            code: "packageMetadataInvalid",
            message: "qmod.json 与 Tauri 模块清单不匹配。".to_string(),
        })
    }
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

fn is_reparse_point(metadata: &fs::Metadata) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        // FILE_ATTRIBUTE_REPARSE_POINT. This catches directory junctions as
        // well as symbolic links, which `FileType::is_symlink` alone misses.
        metadata.file_attributes() & 0x400 != 0
    }
    #[cfg(not(windows))]
    {
        metadata.file_type().is_symlink()
    }
}

fn read_existing_version(directory: &Path) -> Option<String> {
    let bytes = fs::read(directory.join("module.json")).ok()?;
    let value = serde_json::from_slice::<serde_json::Value>(&bytes).ok()?;
    value
        .get("version")
        .and_then(serde_json::Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_string)
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
    fn update_replaces_only_the_user_module_and_reports_previous_version() {
        let sandbox = TestDir::new("qmod-update");
        let modules_root = sandbox.path().join("modules");
        let installed = modules_root.join("qing.test");
        fs::create_dir_all(installed.join("bin")).expect("installed module");
        fs::write(
            installed.join("module.json"),
            br#"{"id":"qing.test","name":"Old","version":"1.0.0","entry":"bin/test.exe","runtimeType":"Process","runtimeIsolation":"OutOfProcess","loadMode":"Manual"}"#,
        )
        .expect("old manifest");
        fs::write(installed.join("bin/test.exe"), b"old").expect("old entry");

        let package = sandbox.path().join("new.qmod");
        let manifest = br#"{"id":"qing.test","name":"New","version":"2.0.0","entry":"bin/test.exe","runtimeType":"Process","runtimeIsolation":"OutOfProcess","loadMode":"Manual"}"#;
        let metadata = br#"{"schemaVersion":1,"moduleId":"qing.test","version":"2.0.0","moduleApiVersion":"tauri-process-v1","entryManifest":"module.json"}"#;
        write_qmod(
            &package,
            &[
                ("qmod.json", metadata),
                ("module.json", manifest),
                ("bin/test.exe", b"new"),
            ],
        );

        let result = update_qmod_into(&package.to_string_lossy(), "qing.test", &modules_root)
            .expect("update should succeed");
        assert!(result.replaced);
        assert_eq!(result.previous_version.as_deref(), Some("1.0.0"));
        assert_eq!(result.version, "2.0.0");
        assert_eq!(fs::read(installed.join("bin/test.exe")).unwrap(), b"new");
        assert!(!modules_root.join(".qmod-backup-").exists());
    }

    #[test]
    fn package_metadata_must_match_the_process_manifest_when_present() {
        let sandbox = TestDir::new("qmod-metadata");
        let package = sandbox.path().join("mismatch.qmod");
        let manifest = br#"{"id":"qing.test","name":"Test","version":"1.0.0","entry":"bin/test.exe","runtimeType":"Process","runtimeIsolation":"OutOfProcess","loadMode":"Manual"}"#;
        let metadata = br#"{"schemaVersion":1,"moduleId":"qing.other","version":"1.0.0","moduleApiVersion":"tauri-process-v1","entryManifest":"module.json"}"#;
        write_qmod(
            &package,
            &[
                ("qmod.json", metadata),
                ("module.json", manifest),
                ("bin/test.exe", b"x"),
            ],
        );
        let error = import_qmod_into(&package.to_string_lossy(), &sandbox.path().join("modules"))
            .expect_err("mismatched qmod metadata must fail");
        assert_eq!(error.code, "packageMetadataInvalid");
    }

    #[test]
    fn package_metadata_rejects_unknown_fields() {
        let sandbox = TestDir::new("qmod-metadata-unknown");
        let package = sandbox.path().join("unknown.qmod");
        let manifest = br#"{"id":"qing.test","name":"Test","version":"1.0.0","entry":"bin/test.exe","runtimeType":"Process","runtimeIsolation":"OutOfProcess","loadMode":"Manual"}"#;
        let metadata = br#"{"schemaVersion":1,"moduleId":"qing.test","version":"1.0.0","moduleApiVersion":"tauri-process-v1","entryManifest":"module.json","unexpected":true}"#;
        write_qmod(
            &package,
            &[
                ("qmod.json", metadata),
                ("module.json", manifest),
                ("bin/test.exe", b"x"),
            ],
        );
        let error = import_qmod_into(&package.to_string_lossy(), &sandbox.path().join("modules"))
            .expect_err("unknown qmod metadata must fail closed");
        assert_eq!(error.code, "packageMetadataInvalid");
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
