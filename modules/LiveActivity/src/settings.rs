//! Persisted module preferences.
//!
//! The file lives in the host-provided module data directory. Every field is
//! validated on load and clamped on write, because a settings file is
//! user-editable text and a hand-edited `overlayScale: 9000` must degrade to
//! something sane rather than producing a 9000-pixel island.

use std::path::Path;

use serde::{Deserialize, Serialize};

/// Current settings schema. Bump when a field's meaning changes, not when a
/// field is added, so that additive changes stay forward-compatible.
pub const SETTINGS_VERSION: u32 = 2;
pub const MAX_CUSTOM_TEXT_CHARS: usize = 256;
pub const MAX_FALLBACK_CHARS: usize = 48;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum DataPosition {
    Header,
    Expanded,
    CustomText,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct RgbColor {
    pub r: i32,
    pub g: i32,
    pub b: i32,
}
impl Default for RgbColor {
    fn default() -> Self {
        Self {
            r: 21,
            g: 26,
            b: 37,
        }
    }
}
impl RgbColor {
    fn normalize(&mut self) {
        self.r = self.r.clamp(0, 255);
        self.g = self.g.clamp(0, 255);
        self.b = self.b.clamp(0, 255);
    }
    pub fn is_light(self) -> bool {
        self.r as f64 * 0.2126 + self.g as f64 * 0.7152 + self.b as f64 * 0.0722 > 150.0
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum SurfaceStyle {
    Solid,
    Translucent,
    Frosted,
}
impl SurfaceStyle {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Solid => "solid",
            Self::Translucent => "translucent",
            Self::Frosted => "frosted",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum MonitorStrategy {
    /// Always the Windows primary monitor. Predictable and DPI-simple.
    Primary,
    /// Whichever monitor currently holds the cursor.
    Active,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum FullscreenPolicy {
    /// The island never hides.
    Always,
    /// Hide while any monitor is showing a fullscreen application.
    Hide,
    /// Hide, but let a user-blocking activity through briefly.
    Important,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Settings {
    pub version: u32,
    pub enabled: bool,
    /// Screen edge the island docks to.
    pub anchor: Anchor,
    pub monitor_strategy: MonitorStrategy,
    pub fullscreen_policy: FullscreenPolicy,
    /// User scale multiplier applied on top of the monitor DPI.
    pub scale: f64,
    pub compact_width: u32,
    pub offset_x: i32,
    pub offset_y: i32,
    /// Whether hovering the compact island reveals the peek row.
    pub peek_on_hover: bool,
    pub click_through: bool,
    pub show_stopwatch: bool,
    pub show_countdown: bool,
    pub countdown_seconds: u64,
    pub show_clock: bool,
    pub show_seconds: bool,
    pub clock_24_hour: bool,
    pub custom_text: String,
    pub placeholder_fallback: String,
    pub surface_style: SurfaceStyle,
    pub background_opacity: f64,
    pub background_color: RgbColor,
    /// v1's connection switch migrates to visibility, never acquisition policy.
    #[serde(alias = "codexEnabled")]
    pub show_codex_data: bool,
    pub codex_data_position: DataPosition,
    /// Retained for v1 profile/API compatibility; automatic mode ignores it.
    pub codex_idle_shutdown_seconds: u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum Anchor {
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

impl Anchor {
    /// Docked to the top edge means the stack grows downwards; docked to the
    /// bottom means it grows upwards. Used by the overlay for both placement
    /// and the direction the expansion animates in.
    pub fn grows_downward(self) -> bool {
        matches!(self, Self::TopLeft | Self::TopCenter | Self::TopRight)
    }
    pub fn horizontal_fraction(self) -> f64 {
        match self {
            Self::TopLeft | Self::BottomLeft => 0.0,
            Self::TopCenter | Self::BottomCenter => 0.5,
            Self::TopRight | Self::BottomRight => 1.0,
        }
    }
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            version: SETTINGS_VERSION,
            // Off by default. A module that paints an always-on-top surface
            // before the user has asked for one is hostile, and the module
            // page already requires explicit startup authorization.
            enabled: false,
            anchor: Anchor::TopCenter,
            monitor_strategy: MonitorStrategy::Primary,
            fullscreen_policy: FullscreenPolicy::Hide,
            scale: 1.0,
            compact_width: 232,
            offset_x: 0,
            offset_y: 0,
            peek_on_hover: true,
            click_through: false,
            show_stopwatch: true,
            show_countdown: true,
            countdown_seconds: 300,
            show_clock: true,
            show_seconds: false,
            clock_24_hour: true,
            custom_text: String::new(),
            placeholder_fallback: "暂无数据".into(),
            surface_style: SurfaceStyle::Translucent,
            background_opacity: 0.72,
            background_color: RgbColor::default(),
            show_codex_data: true,
            codex_data_position: DataPosition::Header,
            codex_idle_shutdown_seconds: 300,
        }
    }
}

impl Settings {
    /// Clamp every field into its supported range.
    ///
    /// Applied both after load and before save, so the in-memory value and the
    /// persisted value can never disagree about what the overlay will do.
    pub fn normalize(&mut self) {
        if !self.scale.is_finite() {
            self.scale = 1.0;
        }
        self.scale = self.scale.clamp(0.75, 1.5);
        self.compact_width = self.compact_width.clamp(200, 480);
        self.countdown_seconds = self
            .countdown_seconds
            .clamp(1, crate::timers::MAX_COUNTDOWN_SECONDS);
        self.offset_x = self.offset_x.clamp(-4096, 4096);
        self.offset_y = self.offset_y.clamp(-4096, 4096);
        if !self.background_opacity.is_finite() {
            self.background_opacity = 0.72;
        }
        self.background_opacity = self.background_opacity.clamp(0.35, 1.0);
        self.background_color.normalize();
        self.custom_text = self
            .custom_text
            .chars()
            .filter_map(|c| {
                if c.is_whitespace() {
                    Some(' ')
                } else if c.is_control() {
                    None
                } else {
                    Some(c)
                }
            })
            .take(MAX_CUSTOM_TEXT_CHARS)
            .collect::<String>()
            .trim()
            .to_string();
        self.placeholder_fallback = self
            .placeholder_fallback
            .chars()
            .filter_map(|c| {
                if c.is_whitespace() {
                    Some(' ')
                } else if c.is_control() {
                    None
                } else {
                    Some(c)
                }
            })
            .take(MAX_FALLBACK_CHARS)
            .collect::<String>()
            .trim()
            .to_string();
        if self.codex_idle_shutdown_seconds > 3600 {
            self.codex_idle_shutdown_seconds = 3600;
        }
        // Migrate both older and forward-compatible profiles without discarding choices.
        self.version = SETTINGS_VERSION;
    }

    pub fn load(path: &Path) -> Self {
        let mut settings = match std::fs::read(path) {
            Ok(bytes) => serde_json::from_slice::<Self>(&bytes).unwrap_or_else(|_| {
                crate::diagnostics::warning(
                    "settings",
                    "settings file was unreadable and has been reset to defaults",
                );
                Self::default()
            }),
            // First run: absent is normal, not an error worth reporting.
            Err(_) => Self::default(),
        };
        settings.normalize();
        settings
    }

    /// Write atomically via a temporary file, so an interrupted save cannot
    /// leave a truncated settings file that resets the user's preferences.
    pub fn save(&self, path: &Path) -> Result<(), String> {
        let mut normalized = self.clone();
        normalized.normalize();
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
        }
        let bytes = serde_json::to_vec_pretty(&normalized).map_err(|error| error.to_string())?;
        let temporary = path.with_extension("json.tmp");
        std::fs::write(&temporary, &bytes).map_err(|error| error.to_string())?;
        std::fs::rename(&temporary, path).map_err(|error| error.to_string())
    }
}

/// Merge a partial patch from the settings UI into current settings.
///
/// `None` fields are left alone, so the UI can send only what changed and a
/// field the UI does not know about cannot be clobbered by an older page.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SettingsPatch {
    pub enabled: Option<bool>,
    pub anchor: Option<Anchor>,
    pub monitor_strategy: Option<MonitorStrategy>,
    pub fullscreen_policy: Option<FullscreenPolicy>,
    pub scale: Option<f64>,
    pub compact_width: Option<u32>,
    pub offset_x: Option<i32>,
    pub offset_y: Option<i32>,
    pub peek_on_hover: Option<bool>,
    pub click_through: Option<bool>,
    pub show_stopwatch: Option<bool>,
    pub show_countdown: Option<bool>,
    pub countdown_seconds: Option<u64>,
    pub show_clock: Option<bool>,
    pub show_seconds: Option<bool>,
    pub clock_24_hour: Option<bool>,
    pub custom_text: Option<String>,
    pub placeholder_fallback: Option<String>,
    pub surface_style: Option<SurfaceStyle>,
    pub background_opacity: Option<f64>,
    pub background_color: Option<RgbColor>,
    #[serde(alias = "codexEnabled")]
    pub show_codex_data: Option<bool>,
    pub codex_data_position: Option<DataPosition>,
    pub codex_idle_shutdown_seconds: Option<u64>,
}

impl Settings {
    pub fn apply(&mut self, patch: SettingsPatch) {
        if let Some(value) = patch.enabled {
            self.enabled = value;
        }
        if let Some(value) = patch.anchor {
            self.anchor = value;
        }
        if let Some(value) = patch.monitor_strategy {
            self.monitor_strategy = value;
        }
        if let Some(value) = patch.fullscreen_policy {
            self.fullscreen_policy = value;
        }
        if let Some(value) = patch.scale {
            self.scale = value;
        }
        if let Some(value) = patch.compact_width {
            self.compact_width = value;
        }
        if let Some(value) = patch.offset_x {
            self.offset_x = value;
        }
        if let Some(value) = patch.offset_y {
            self.offset_y = value;
        }
        if let Some(value) = patch.peek_on_hover {
            self.peek_on_hover = value;
        }
        if let Some(value) = patch.click_through {
            self.click_through = value;
        }
        if let Some(value) = patch.show_stopwatch {
            self.show_stopwatch = value;
        }
        if let Some(value) = patch.show_countdown {
            self.show_countdown = value;
        }
        if let Some(value) = patch.countdown_seconds {
            self.countdown_seconds = value;
        }
        if let Some(value) = patch.show_clock {
            self.show_clock = value;
        }
        if let Some(value) = patch.show_seconds {
            self.show_seconds = value;
        }
        if let Some(value) = patch.clock_24_hour {
            self.clock_24_hour = value;
        }
        if let Some(value) = patch.custom_text {
            self.custom_text = value;
        }
        if let Some(value) = patch.placeholder_fallback {
            self.placeholder_fallback = value;
        }
        if let Some(value) = patch.surface_style {
            self.surface_style = value;
        }
        if let Some(value) = patch.background_opacity {
            self.background_opacity = value;
        }
        if let Some(value) = patch.background_color {
            self.background_color = value;
        }
        if let Some(value) = patch.show_codex_data {
            self.show_codex_data = value;
        }
        if let Some(value) = patch.codex_data_position {
            self.codex_data_position = value;
        }
        if let Some(value) = patch.codex_idle_shutdown_seconds {
            self.codex_idle_shutdown_seconds = value;
        }
        self.normalize();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    #[test]
    fn additive_clock_and_style_settings_migrate_old_profiles() {
        let settings: Settings =
            serde_json::from_value(serde_json::json!({"enabled":true,"anchor":"bottomCenter"}))
                .unwrap();
        assert!(settings.show_clock && settings.clock_24_hour);
        assert!(!settings.show_seconds);
        assert_eq!(settings.surface_style, SurfaceStyle::Translucent);
        assert_eq!(settings.anchor, Anchor::BottomCenter);
        assert_eq!(settings.background_color, RgbColor::default());
        assert_eq!(settings.compact_width, 232);
        assert_eq!((settings.offset_x, settings.offset_y), (0, 0));
    }

    #[test]
    fn placement_and_width_patch_are_bounded_and_persisted() {
        let path = temp_path("placement");
        let mut settings = Settings::default();
        settings.apply(
            serde_json::from_value(serde_json::json!({
                "anchor":"bottomRight", "compactWidth":9999, "offsetX":9999, "offsetY":-9999
            }))
            .unwrap(),
        );
        assert_eq!(settings.anchor, Anchor::BottomRight);
        assert_eq!(settings.compact_width, 480);
        assert_eq!((settings.offset_x, settings.offset_y), (4096, -4096));
        settings.save(&path).unwrap();
        assert_eq!(Settings::load(&path), settings);
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn rgb_patch_is_bounded_persistent_and_independent_of_material() {
        let path = temp_path("rgb");
        let mut settings = Settings::default();
        settings.apply(
            serde_json::from_value(serde_json::json!({
                "backgroundColor": {"r":-3,"g":400,"b":128}
            }))
            .unwrap(),
        );
        assert_eq!(
            settings.background_color,
            RgbColor {
                r: 0,
                g: 255,
                b: 128
            }
        );
        settings.apply(SettingsPatch {
            surface_style: Some(SurfaceStyle::Solid),
            ..Default::default()
        });
        settings.save(&path).unwrap();
        assert_eq!(Settings::load(&path), settings);
        let _ = std::fs::remove_file(path);
        assert!(RgbColor {
            r: 255,
            g: 255,
            b: 255
        }
        .is_light());
        assert!(!RgbColor::default().is_light());
    }

    #[test]
    fn custom_text_and_opacity_are_bounded_and_unicode_safe() {
        let mut settings = Settings {
            custom_text: format!("\0\t{}", "🌟中".repeat(100)),
            background_opacity: f64::NAN,
            ..Settings::default()
        };
        settings.normalize();
        assert!(settings.custom_text.chars().count() <= MAX_CUSTOM_TEXT_CHARS);
        assert!(!settings.custom_text.contains('\0'));
        assert_eq!(settings.background_opacity, 0.72);
        settings.apply(
            serde_json::from_value(
                serde_json::json!({"customText":"   ","backgroundOpacity":0.01,"showClock":false}),
            )
            .unwrap(),
        );
        assert!(settings.custom_text.is_empty());
        assert_eq!(settings.background_opacity, 0.35);
        assert!(!settings.show_clock);
    }

    fn temp_path(name: &str) -> PathBuf {
        let nonce = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        std::env::temp_dir().join(format!(
            "qing-liveactivity-settings-{}-{nonce}-{name}.json",
            std::process::id()
        ))
    }

    #[test]
    fn defaults_are_quiet_and_do_not_start_children() {
        let settings = Settings::default();
        assert!(!settings.enabled, "the island must not paint until asked");
        assert!(settings.show_codex_data);
        assert_eq!(settings.scale, 1.0);
        assert_eq!(settings.anchor, Anchor::TopCenter);
        assert_eq!(settings.fullscreen_policy, FullscreenPolicy::Hide);
    }

    #[test]
    fn v1_connection_choice_migrates_to_visibility_without_losing_style() {
        let mut settings: Settings = serde_json::from_value(serde_json::json!({"version":1,"codexEnabled":false,"surfaceStyle":"frosted","customText":"剩余 {codex.remaining|未知}"})).unwrap();
        settings.normalize();
        assert_eq!(settings.version, 2);
        assert!(!settings.show_codex_data);
        assert_eq!(settings.surface_style, SurfaceStyle::Frosted);
        assert_eq!(settings.codex_data_position, DataPosition::Header);
        assert_eq!(settings.placeholder_fallback, "暂无数据");
        assert!(serde_json::to_value(&settings)
            .unwrap()
            .get("codexEnabled")
            .is_none());
        settings.apply(
            serde_json::from_value(
                serde_json::json!({"showCodexData":true,"placeholderFallback":"\u{0}\t未知"}),
            )
            .unwrap(),
        );
        assert!(settings.show_codex_data);
        assert_eq!(settings.placeholder_fallback, "未知");
    }

    #[test]
    fn round_trips_through_disk() {
        let path = temp_path("round-trip");
        let settings = Settings {
            enabled: true,
            anchor: Anchor::BottomCenter,
            scale: 1.25,
            codex_idle_shutdown_seconds: 120,
            ..Settings::default()
        };
        settings.save(&path).expect("save");

        let loaded = Settings::load(&path);
        assert_eq!(loaded, settings);
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn a_missing_file_is_first_run_not_an_error() {
        let path = temp_path("absent");
        let _ = std::fs::remove_file(&path);
        let loaded = Settings::load(&path);
        assert_eq!(loaded, Settings::default());
    }

    #[test]
    fn corrupt_json_falls_back_to_defaults_instead_of_failing_to_start() {
        let path = temp_path("corrupt");
        std::fs::write(&path, b"{ this is not json").expect("write");
        let loaded = Settings::load(&path);
        assert_eq!(loaded, Settings::default());
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn hand_edited_out_of_range_values_are_clamped() {
        let mut settings = Settings {
            scale: 9000.0,
            codex_idle_shutdown_seconds: 999_999,
            ..Settings::default()
        };
        settings.normalize();
        assert_eq!(settings.scale, 1.5);
        assert_eq!(settings.codex_idle_shutdown_seconds, 3600);

        let mut settings = Settings {
            scale: -3.0,
            ..Settings::default()
        };
        settings.normalize();
        assert_eq!(settings.scale, 0.75);

        let mut settings = Settings {
            scale: f64::NAN,
            ..Settings::default()
        };
        settings.normalize();
        assert_eq!(
            settings.scale, 1.0,
            "NaN must not survive into window geometry"
        );
    }

    #[test]
    fn a_newer_schema_version_keeps_known_fields() {
        let path = temp_path("future");
        std::fs::write(
            &path,
            br#"{"version":99,"enabled":true,"anchor":"bottomCenter","monitorStrategy":"active",
                 "fullscreenPolicy":"important","scale":1.1,"peekOnHover":false,
                 "codexEnabled":true,"codexIdleShutdownSeconds":60}"#,
        )
        .expect("write");
        let loaded = Settings::load(&path);
        assert_eq!(loaded.version, SETTINGS_VERSION);
        assert!(
            loaded.enabled,
            "a forward-versioned file must not lose the user's choices"
        );
        assert_eq!(loaded.anchor, Anchor::BottomCenter);
        assert_eq!(loaded.monitor_strategy, MonitorStrategy::Active);
        assert_eq!(loaded.fullscreen_policy, FullscreenPolicy::Important);
        assert!(loaded.show_codex_data);
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn patch_leaves_unmentioned_fields_alone() {
        let mut settings = Settings {
            anchor: Anchor::BottomCenter,
            show_codex_data: true,
            ..Settings::default()
        };

        settings.apply(SettingsPatch {
            scale: Some(1.2),
            ..SettingsPatch::default()
        });
        assert_eq!(settings.scale, 1.2);
        assert_eq!(
            settings.anchor,
            Anchor::BottomCenter,
            "an unrelated field must survive"
        );
        assert!(settings.show_codex_data);
    }

    #[test]
    fn patch_defaults_every_optional_field() {
        let patch = SettingsPatch::default();
        assert!(patch.enabled.is_none());
        assert!(patch.scale.is_none());

        let mut settings = Settings::default();
        let before = settings.clone();
        settings.apply(SettingsPatch::default());
        assert_eq!(settings, before, "an empty patch must be a no-op");
    }

    #[test]
    fn patch_of_a_corrupt_full_document_reports_which_field_broke() {
        // deny_unknown_fields plus typed fields means a bad enum value is a hard
        // error the operation can report, instead of a silent fallback.
        let error = serde_json::from_str::<SettingsPatch>(r#"{"anchor":"leftEdge"}"#);
        assert!(error.is_err());
        let error = serde_json::from_str::<SettingsPatch>(r#"{"unknownField":1}"#);
        assert!(
            error.is_err(),
            "an unknown field must not be silently ignored"
        );
    }

    #[test]
    fn anchor_direction_drives_the_stack() {
        assert!(Anchor::TopCenter.grows_downward());
        assert!(!Anchor::BottomCenter.grows_downward());
    }

    #[test]
    fn save_is_atomic_and_leaves_no_temporary_behind() {
        let path = temp_path("atomic");
        Settings::default().save(&path).expect("save");
        assert!(path.is_file());
        let leftovers = std::fs::read_dir(path.parent().expect("parent"))
            .expect("read dir")
            .filter_map(Result::ok)
            .filter(|entry| entry.file_name().to_string_lossy().contains("atomic"))
            .filter(|entry| entry.file_name().to_string_lossy().ends_with(".tmp"))
            .count();
        assert_eq!(leftovers, 0);
        let _ = std::fs::remove_file(&path);
    }
}
