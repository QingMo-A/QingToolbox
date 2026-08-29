use std::{
    env, fmt, fs,
    path::{Component, Path, PathBuf},
};

use serde::Serialize;

const MAX_RELATIVE_PATH_LENGTH: usize = 512;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum ModuleSource {
    Bundled,
    User,
}

impl ModuleSource {
    pub fn label(self) -> &'static str {
        match self {
            Self::Bundled => "内置",
            Self::User => "用户",
        }
    }
}

#[derive(Debug, Clone)]
pub struct ModuleRoot {
    pub source: ModuleSource,
    pub path: PathBuf,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ModuleRootSummary {
    pub source: ModuleSource,
    pub label: &'static str,
    pub available: bool,
    pub error: Option<String>,
}

#[derive(Debug, Clone)]
pub enum PathError {
    Empty,
    Absolute,
    InvalidComponent,
    TooLong,
    OutsideRoot,
    Missing,
    Io(String),
}

impl fmt::Display for PathError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        let message = match self {
            Self::Empty => "path is empty",
            Self::Absolute => "absolute paths are not allowed",
            Self::InvalidComponent => "path contains an invalid component",
            Self::TooLong => "path is too long",
            Self::OutsideRoot => "path escapes its module root",
            Self::Missing => "path does not exist",
            Self::Io(message) => message,
        };
        formatter.write_str(message)
    }
}

impl std::error::Error for PathError {}

/// Resolve only roots chosen by the backend. The Vue process never supplies a
/// root or an arbitrary filesystem path.
pub fn resolve_module_roots() -> Vec<ModuleRoot> {
    let mut roots = Vec::new();

    if let Ok(executable) = env::current_exe() {
        if let Some(parent) = executable.parent() {
            push_unique_root(
                &mut roots,
                ModuleRoot {
                    source: ModuleSource::Bundled,
                    path: parent.join("resources").join("modules"),
                },
            );
        }
    }

    // Development and unpacked previews commonly run with the repository
    // directory as the current directory. These are still fixed backend roots.
    if let Ok(current) = env::current_dir() {
        for relative in [
            PathBuf::from("resources").join("modules"),
            PathBuf::from("src-tauri").join("resources").join("modules"),
        ] {
            push_unique_root(
                &mut roots,
                ModuleRoot {
                    source: ModuleSource::Bundled,
                    path: current.join(relative),
                },
            );
        }
    }

    if let Some(user_root) = user_modules_root() {
        push_unique_root(
            &mut roots,
            ModuleRoot {
                source: ModuleSource::User,
                path: user_root,
            },
        );
    }

    roots
}

fn user_modules_root() -> Option<PathBuf> {
    Some(user_data_root()?.join("Modules"))
}

/// Stable per-user location for host-owned state. The directory is not
/// created by this helper; callers decide which state they need to create.
pub fn user_data_root() -> Option<PathBuf> {
    let base = if cfg!(windows) {
        env::var_os("LOCALAPPDATA")
    } else {
        env::var_os("XDG_DATA_HOME").or_else(|| {
            env::var_os("HOME").map(|home| {
                PathBuf::from(home)
                    .join(".local")
                    .join("share")
                    .into_os_string()
            })
        })
    }?;
    Some(PathBuf::from(base).join("QingToolbox"))
}

fn push_unique_root(roots: &mut Vec<ModuleRoot>, candidate: ModuleRoot) {
    let key = normalize_for_compare(&candidate.path);
    if roots
        .iter()
        .all(|root| normalize_for_compare(&root.path) != key)
    {
        roots.push(candidate);
    }
}

pub fn summarize_root(root: &ModuleRoot) -> ModuleRootSummary {
    match fs::metadata(&root.path) {
        Ok(metadata) if metadata.is_dir() => ModuleRootSummary {
            source: root.source,
            label: root.source.label(),
            available: true,
            error: None,
        },
        Ok(_) => ModuleRootSummary {
            source: root.source,
            label: root.source.label(),
            available: false,
            error: Some("模块根路径不是目录。".to_string()),
        },
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => ModuleRootSummary {
            source: root.source,
            label: root.source.label(),
            available: false,
            error: None,
        },
        Err(error) => ModuleRootSummary {
            source: root.source,
            label: root.source.label(),
            available: false,
            error: Some(format!("无法读取模块根路径：{error}")),
        },
    }
}

/// Parse a manifest-owned asset path. Dot, parent, drive and rooted
/// components are rejected before any filesystem operation occurs.
pub fn parse_relative_path(raw: &str) -> Result<PathBuf, PathError> {
    if raw.is_empty() {
        return Err(PathError::Empty);
    }
    if raw.chars().count() > MAX_RELATIVE_PATH_LENGTH {
        return Err(PathError::TooLong);
    }
    if raw.contains('\0') || raw.contains(':') {
        return Err(PathError::InvalidComponent);
    }

    // Windows normalizes `.` while iterating Path::components(), so inspect
    // the lexical spelling first and reject traversal/ambiguous separators.
    if raw
        .split(['/', '\\'])
        .any(|component| component.is_empty() || component == "." || component == "..")
    {
        return Err(PathError::InvalidComponent);
    }

    let path = Path::new(raw);
    if path.is_absolute() {
        return Err(PathError::Absolute);
    }

    let mut parsed = PathBuf::new();
    for component in path.components() {
        match component {
            Component::Normal(value) if value != "." && value != ".." => parsed.push(value),
            _ => return Err(PathError::InvalidComponent),
        }
    }

    if parsed.as_os_str().is_empty() {
        Err(PathError::Empty)
    } else {
        Ok(parsed)
    }
}

pub fn canonical_module_directory(root: &Path, candidate: &Path) -> Result<PathBuf, PathError> {
    let canonical_root =
        fs::canonicalize(root).map_err(|error| PathError::Io(error.to_string()))?;
    let canonical_candidate =
        fs::canonicalize(candidate).map_err(|error| PathError::Io(error.to_string()))?;
    if !canonical_candidate.is_dir()
        || !is_within(&canonical_root, &canonical_candidate)
        || canonical_candidate
            .parent()
            .map(|parent| normalize_for_compare(parent) != normalize_for_compare(&canonical_root))
            .unwrap_or(true)
    {
        return Err(PathError::OutsideRoot);
    }
    Ok(canonical_candidate)
}

pub fn canonical_manifest_path(module_directory: &Path) -> Result<PathBuf, PathError> {
    let candidate = module_directory.join("module.json");
    let canonical = fs::canonicalize(&candidate).map_err(|error| {
        if error.kind() == std::io::ErrorKind::NotFound {
            PathError::Missing
        } else {
            PathError::Io(error.to_string())
        }
    })?;
    if !canonical.is_file() || !is_within(module_directory, &canonical) {
        return Err(PathError::OutsideRoot);
    }
    Ok(canonical)
}

pub fn resolve_existing_asset(module_directory: &Path, raw: &str) -> Result<PathBuf, PathError> {
    let relative = parse_relative_path(raw)?;
    let canonical_directory =
        fs::canonicalize(module_directory).map_err(|error| PathError::Io(error.to_string()))?;
    let candidate = canonical_directory.join(relative);
    let canonical = fs::canonicalize(&candidate).map_err(|error| {
        if error.kind() == std::io::ErrorKind::NotFound {
            PathError::Missing
        } else {
            PathError::Io(error.to_string())
        }
    })?;
    if !canonical.is_file() || !is_within(&canonical_directory, &canonical) {
        return Err(PathError::OutsideRoot);
    }
    Ok(canonical)
}

fn is_within(root: &Path, candidate: &Path) -> bool {
    candidate.starts_with(root)
        || normalize_for_compare(candidate).starts_with(&format!(
            "{}{}",
            normalize_for_compare(root),
            std::path::MAIN_SEPARATOR
        ))
}

fn normalize_for_compare(path: &Path) -> String {
    let mut value = path.to_string_lossy().replace('/', "\\");
    while value.ends_with('\\') && value.len() > 1 {
        value.pop();
    }
    value.to_ascii_lowercase()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn relative_path_parser_rejects_escape_forms() {
        for value in [
            "",
            "../outside",
            r"..\outside",
            r"C:\outside",
            "/outside",
            "a/./b",
        ] {
            assert!(parse_relative_path(value).is_err(), "{value} should fail");
        }
        assert_eq!(
            parse_relative_path("ui/index.html").unwrap(),
            PathBuf::from("ui/index.html")
        );
    }
}
