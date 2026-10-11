//! Follow the host profile without depending on an open settings WebView.
//! Imported files are bounded, hash-addressed and registered privately in this
//! process. They are never installed globally or read from a user-supplied path.
use serde::Serialize;
use sha2::{Digest, Sha256};
use std::{
    path::{Path, PathBuf},
    time::SystemTime,
};

const MAX_FONT_BYTES: u64 = 32 * 1024 * 1024;

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Selection {
    pub family: String,
    pub resource_url: Option<String>,
    #[serde(skip)]
    pub file: Option<PathBuf>,
    #[serde(skip)]
    pub hash: Option<String>,
}
impl Default for Selection {
    fn default() -> Self {
        Self {
            family: "Segoe UI Variable".into(),
            resource_url: None,
            file: None,
            hash: None,
        }
    }
}

pub struct Watcher {
    settings: Option<PathBuf>,
    fonts: Option<PathBuf>,
    stamp: Option<(SystemTime, u64)>,
    pub selection: Selection,
}
impl Watcher {
    pub fn new(module_settings: &Path) -> Self {
        // Host-provided .../<profile>/Data/<module-id>/settings.json only.
        // Standalone tests and relocated packages don't guess the live profile.
        let profile = module_settings
            .parent()
            .and_then(Path::parent)
            .and_then(Path::parent)
            .filter(|p| {
                matches!(
                    p.file_name().and_then(|v| v.to_str()),
                    Some("QingToolbox" | "QingToolbox.Dev")
                )
            })
            .filter(|_| {
                module_settings
                    .parent()
                    .and_then(Path::parent)
                    .and_then(Path::file_name)
                    .is_some_and(|v| v == "Data")
            });
        let settings = profile.map(|p| p.join("settings.json"));
        let fonts = profile.and_then(|p| {
            Some(
                PathBuf::from(std::env::var_os("LOCALAPPDATA")?)
                    .join(p.file_name()?)
                    .join("Fonts")
                    .join("Imported"),
            )
        });
        let mut watcher = Self {
            settings,
            fonts,
            stamp: None,
            selection: Default::default(),
        };
        watcher.refresh();
        watcher
    }
    pub fn refresh(&mut self) -> bool {
        let stamp = self
            .settings
            .as_ref()
            .and_then(|p| std::fs::metadata(p).ok())
            .and_then(|m| Some((m.modified().ok()?, m.len())));
        if stamp == self.stamp {
            return false;
        }
        // An atomic host save can temporarily leave no file. Retain the last
        // valid selection instead of flashing the default font.
        let parsed = self
            .settings
            .as_ref()
            .and_then(|p| read_bounded(p, 1024 * 1024))
            .and_then(|b| serde_json::from_slice::<serde_json::Value>(&b).ok());
        let Some(value) = parsed else {
            return false;
        };
        let next = selection(&value, self.fonts.as_deref());
        self.stamp = stamp;
        let requested = value
            .get("fontId")
            .or_else(|| value.get("FontId"))
            .and_then(|v| v.as_str())
            .unwrap_or("Default");
        if requested != "Default" && !requested.is_empty() && next == Selection::default() {
            crate::diagnostics::warning("host/font", "host font is missing, unsupported or failed integrity checks; using the default font");
        }
        if next == self.selection {
            return false;
        }
        self.selection = next;
        crate::diagnostics::information("host/font", "host font selection synchronized");
        true
    }
}

fn selection(value: &serde_json::Value, fonts: Option<&Path>) -> Selection {
    let get = |key: &str, legacy: &str| {
        value
            .get(key)
            .or_else(|| value.get(legacy))
            .and_then(|v| v.as_str())
    };
    let id = get("fontId", "FontId").unwrap_or("Default");
    if let Some(family) = id.strip_prefix("system:").filter(|v| {
        matches!(
            *v,
            "Segoe UI" | "Microsoft YaHei UI" | "Consolas" | "Cascadia Mono"
        )
    }) {
        return Selection {
            family: family.into(),
            ..Default::default()
        };
    }
    let Some(hash) = id
        .strip_prefix("imported:")
        .filter(|s| s.len() == 64 && s.bytes().all(|b| b.is_ascii_hexdigit()))
    else {
        return Default::default();
    };
    if get("fontSource", "FontSource") != Some("imported") {
        return Default::default();
    }
    let Some(fonts) = fonts else {
        return Default::default();
    };
    let root = fonts
        .parent()
        .and_then(Path::parent)
        .and_then(|p| p.canonicalize().ok());
    let Some(directory) = fonts
        .canonicalize()
        .ok()
        .filter(|p| root.as_ref().is_some_and(|root| p.starts_with(root)))
    else {
        return Default::default();
    };
    for ext in ["ttf", "otf", "ttc"] {
        let name = format!("font-{}.{}", hash.to_ascii_lowercase(), ext);
        let Some(file) = directory
            .join(&name)
            .canonicalize()
            .ok()
            .filter(|p| p.parent() == Some(directory.as_path()))
        else {
            continue;
        };
        let Some(bytes) = read_bounded(&file, MAX_FONT_BYTES) else {
            continue;
        };
        if format!("{:x}", Sha256::digest(&bytes)) != hash.to_ascii_lowercase() {
            continue;
        }
        let Some(family) = font_family(&bytes) else {
            continue;
        };
        return Selection {
            family,
            resource_url: Some(format!("http://qfont.localhost/user-fonts/{name}")),
            file: Some(file),
            hash: Some(hash.to_ascii_lowercase()),
        };
    }
    Default::default()
}

fn read_bounded(path: &Path, limit: u64) -> Option<Vec<u8>> {
    use std::io::Read;
    let mut file = std::fs::File::open(path).ok()?;
    let len = file.metadata().ok()?.len();
    if len == 0 || len > limit {
        return None;
    }
    let mut bytes = Vec::with_capacity(len as usize);
    file.by_ref().take(limit + 1).read_to_end(&mut bytes).ok()?;
    (bytes.len() as u64 <= limit).then_some(bytes)
}

/// SFNT/TTC naming table. Prefer the Windows legacy family (name 1), which
/// CreateFontW addresses, over the typographic family used by modern layout.
fn font_family(bytes: &[u8]) -> Option<String> {
    let u16_at = |at: usize| -> Option<u16> {
        Some(u16::from_be_bytes(
            bytes.get(at..at.checked_add(2)?)?.try_into().ok()?,
        ))
    };
    let u32_at = |at: usize| -> Option<usize> {
        Some(u32::from_be_bytes(bytes.get(at..at.checked_add(4)?)?.try_into().ok()?) as usize)
    };
    let base = if bytes.get(..4)? == b"ttcf" {
        if u32_at(8)? == 0 {
            return None;
        }
        u32_at(12)?
    } else {
        0
    };
    let tables = u16_at(base.checked_add(4)?)? as usize;
    if tables > 256 {
        return None;
    }
    let mut name = None;
    for i in 0..tables {
        let at = base.checked_add(12 + i * 16)?;
        if bytes.get(at..at.checked_add(4)?)? == b"name" {
            name = Some(u32_at(at + 8)?);
            break;
        }
    }
    let name = name?;
    let count = u16_at(name.checked_add(2)?)? as usize;
    let storage = name.checked_add(u16_at(name.checked_add(4)?)? as usize)?;
    if count > 4096 {
        return None;
    }
    let mut best: Option<(u32, String)> = None;
    for i in 0..count {
        let at = name.checked_add(6 + i * 12)?;
        let platform = u16_at(at)?;
        let language = u16_at(at + 4)?;
        let id = u16_at(at + 6)?;
        if !matches!(id, 1 | 16) || !matches!(platform, 0 | 3) {
            continue;
        }
        let len = u16_at(at + 8)? as usize;
        if len == 0 || len > 2048 || len % 2 != 0 {
            continue;
        }
        let start = storage.checked_add(u16_at(at + 10)? as usize)?;
        let Some(raw) = bytes.get(start..start.checked_add(len)?) else {
            continue;
        };
        let utf16: Vec<u16> = raw
            .chunks_exact(2)
            .map(|v| u16::from_be_bytes([v[0], v[1]]))
            .collect();
        let Ok(text) = String::from_utf16(&utf16) else {
            continue;
        };
        let text = text.trim();
        if text.is_empty() || text.chars().any(char::is_control) || text.encode_utf16().count() > 31
        {
            continue;
        }
        let score = (if id == 1 { 100 } else { 0 })
            + (if platform == 3 { 10 } else { 0 })
            + (if language == 0x409 { 1 } else { 0 });
        if best.as_ref().map_or(true, |(old, _)| score > *old) {
            best = Some((score, text.into()));
        }
    }
    best.map(|(_, family)| family)
}

#[cfg(windows)]
#[derive(Default)]
pub struct Registration {
    handle: windows_sys::Win32::Foundation::HANDLE,
}
#[cfg(windows)]
impl Registration {
    pub fn load(selection: &Selection) -> Self {
        let Some(bytes) = selection
            .file
            .as_deref()
            .and_then(|p| read_bounded(p, MAX_FONT_BYTES))
        else {
            return Default::default();
        };
        if selection.hash.as_deref() != Some(format!("{:x}", Sha256::digest(&bytes)).as_str()) {
            return Default::default();
        }
        let mut count = 0;
        let handle = unsafe {
            windows_sys::Win32::Graphics::Gdi::AddFontMemResourceEx(
                bytes.as_ptr().cast(),
                bytes.len() as u32,
                std::ptr::null(),
                &mut count,
            )
        };
        if handle.is_null() {
            crate::diagnostics::warning(
                "host/font",
                "imported font could not be registered; Windows font fallback is active",
            );
        }
        Self { handle }
    }
}
#[cfg(windows)]
impl Drop for Registration {
    fn drop(&mut self) {
        if !self.handle.is_null() {
            unsafe {
                windows_sys::Win32::Graphics::Gdi::RemoveFontMemResourceEx(self.handle);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn unsupported_or_tampered_ids_do_not_load_arbitrary_paths() {
        for id in ["imported:../secret", "system:Made Up", "Default"] {
            assert_eq!(
                selection(
                    &serde_json::json!({"fontId":id,"fontSource":"imported"}),
                    Some(Path::new("."))
                ),
                Selection::default()
            );
        }
        assert_eq!(
            selection(&serde_json::json!({"fontId":"system:Consolas"}), None).family,
            "Consolas"
        );
    }
    #[test]
    fn truncated_font_tables_fail_closed() {
        for len in 0..100 {
            assert!(font_family(&vec![0; len]).is_none());
        }
        assert!(font_family(b"ttcf\0\0\0\0\0\0\0\0").is_none());
    }
    #[test]
    fn sfnt_names_are_decoded_as_utf16_and_used_for_native_fonts() {
        let mut bytes = vec![0; 54];
        bytes[4..6].copy_from_slice(&1u16.to_be_bytes());
        bytes[12..16].copy_from_slice(b"name");
        bytes[20..24].copy_from_slice(&36u32.to_be_bytes());
        bytes[38..40].copy_from_slice(&1u16.to_be_bytes());
        bytes[40..42].copy_from_slice(&18u16.to_be_bytes());
        for (offset, value) in [(42, 3u16), (44, 1), (46, 0x409), (48, 1), (50, 8)] {
            bytes[offset..offset + 2].copy_from_slice(&value.to_be_bytes());
        }
        for ch in "测试字体".encode_utf16() {
            bytes.extend_from_slice(&ch.to_be_bytes());
        }
        assert_eq!(font_family(&bytes).as_deref(), Some("测试字体"));
        bytes[52..54].copy_from_slice(&500u16.to_be_bytes());
        assert!(font_family(&bytes).is_none());
    }
    #[test]
    fn watcher_tracks_changes_and_retains_font_during_an_atomic_save() {
        let root = std::env::temp_dir().join(format!(
            "qing-island-font-{}-{}",
            std::process::id(),
            crate::activity::now_millis()
        ));
        let profile = root.join("QingToolbox.Dev");
        std::fs::create_dir_all(&profile).unwrap();
        let settings = profile.join("settings.json");
        std::fs::write(&settings, br#"{"fontId":"system:Consolas"}"#).unwrap();
        let mut watcher = Watcher::new(&profile.join("Data/qing.liveactivity/settings.json"));
        assert_eq!(watcher.selection.family, "Consolas");
        assert!(!watcher.refresh());
        std::fs::write(&settings, b"{").unwrap();
        assert!(!watcher.refresh());
        assert_eq!(watcher.selection.family, "Consolas");
        std::fs::write(&settings, br#"{"fontId":"system:Microsoft YaHei UI"}"#).unwrap();
        assert!(watcher.refresh());
        assert_eq!(watcher.selection.family, "Microsoft YaHei UI");
        std::fs::write(&settings, br#"{"fontId":"Default"}"#).unwrap();
        assert!(watcher.refresh());
        assert_eq!(watcher.selection, Selection::default());
        std::fs::remove_dir_all(root).unwrap();
    }
    #[cfg(windows)]
    #[test]
    #[ignore = "read-only validation against this machine's selected imported host font"]
    fn current_host_imported_font_is_registered_and_selected_by_gdi() {
        use windows_sys::Win32::Graphics::Gdi::*;
        let profile = PathBuf::from(std::env::var_os("APPDATA").unwrap()).join("QingToolbox");
        let watcher = Watcher::new(&profile.join("Data/qing.liveactivity/settings.json"));
        assert!(
            watcher.selection.file.is_some(),
            "this check requires an imported host font"
        );
        let registration = Registration::load(&watcher.selection);
        assert!(!registration.handle.is_null());
        unsafe {
            let dc = CreateCompatibleDC(std::ptr::null_mut());
            assert!(!dc.is_null());
            let family: Vec<u16> = watcher
                .selection
                .family
                .encode_utf16()
                .chain(Some(0))
                .collect();
            let font = CreateFontW(
                -16,
                0,
                0,
                0,
                400,
                0,
                0,
                0,
                DEFAULT_CHARSET as u32,
                OUT_DEFAULT_PRECIS as u32,
                CLIP_DEFAULT_PRECIS as u32,
                ANTIALIASED_QUALITY as u32,
                DEFAULT_PITCH as u32,
                family.as_ptr(),
            );
            assert!(!font.is_null());
            let old = SelectObject(dc, font);
            let mut face = [0u16; 64];
            let count = GetTextFaceW(dc, face.len() as i32, face.as_mut_ptr());
            assert!(count > 0);
            let actual = String::from_utf16_lossy(
                &face[..face.iter().position(|c| *c == 0).unwrap_or(face.len())],
            );
            // GetTextFaceW can return a localized family alias. Compare the
            // selected font's real naming table rather than its display name.
            let size = GetFontData(dc, 0, 0, std::ptr::null_mut(), 0);
            assert!(size != GDI_ERROR as u32 && size as u64 <= MAX_FONT_BYTES);
            let mut data = vec![0u8; size as usize];
            assert_eq!(GetFontData(dc, 0, 0, data.as_mut_ptr().cast(), size), size);
            let selected_family = font_family(&data);
            SelectObject(dc, old);
            DeleteObject(font);
            DeleteDC(dc);
            assert_eq!(
                selected_family.as_deref(),
                Some(watcher.selection.family.as_str()),
                "native text silently fell back to a different font"
            );
            eprintln!("native imported host font verified: {actual}");
        }
    }
}
