//! Environment- and filesystem-derived locations.
//!
//! The host already tells the module where it may write. This module never
//! guesses a path from its own executable location, which is what allows the
//! package to be relocated by the updater without losing state.

use std::path::PathBuf;

/// Set by the host (`runtime.rs`) for every module process it starts.
pub const MODULE_DATA_DIR_ENV: &str = "QINGTOOLBOX_MODULE_DATA_DIR";
/// Also host-provided; used for the diagnostics banner only.
pub const MODULE_ID_ENV: &str = "QINGTOOLBOX_MODULE_ID";

pub const MODULE_ID: &str = "qing.liveactivity";

/// The host-supplied writable directory, or `None` when the module was started
/// outside the host (which is exactly what the smoke test does).
pub fn module_data_dir() -> Option<PathBuf> {
    let value = std::env::var_os(MODULE_DATA_DIR_ENV)?;
    if value.is_empty() {
        return None;
    }
    Some(PathBuf::from(value))
}

/// Where the module keeps its own state.
///
/// Falls back to the system temp directory when no host data root was given,
/// so a standalone run (smoke test, manual debugging) still works instead of
/// failing at startup.
pub fn state_dir() -> PathBuf {
    module_data_dir().unwrap_or_else(|| std::env::temp_dir().join(MODULE_ID))
}

pub fn settings_path() -> PathBuf {
    state_dir().join("settings.json")
}

/// The id the host handed us, for diagnostics. Falls back to the compiled-in id
/// so a standalone run cannot produce a log line with an empty scope.
pub fn module_id() -> String {
    std::env::var(MODULE_ID_ENV).unwrap_or_else(|_| MODULE_ID.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn an_absent_data_root_falls_back_instead_of_panicking() {
        // The smoke test runs the binary with no host environment at all.
        // `state_dir` must still return something usable.
        let dir = state_dir();
        assert!(!dir.as_os_str().is_empty());
    }

    #[test]
    fn settings_live_under_the_state_directory() {
        let path = settings_path();
        assert!(path.ends_with("settings.json"));
        assert_eq!(path.parent().map(PathBuf::from), Some(state_dir()));
    }

    #[test]
    fn module_id_has_a_compiled_in_default() {
        assert!(!module_id().is_empty());
    }
}
