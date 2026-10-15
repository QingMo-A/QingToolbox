//! Shell-owned icons. Prefer real detail, not the size of a padded Shell canvas.
#[cfg(windows)]
pub fn extract(path: &std::path::Path) -> Option<Vec<u8>> {
    use std::os::windows::ffi::OsStrExt;
    use windows::{
        core::PCWSTR,
        Win32::{
            Foundation::SIZE,
            System::Com::{CoInitializeEx, CoUninitialize, COINIT_APARTMENTTHREADED},
            UI::Shell::{
                IShellItemImageFactory, SHCreateItemFromParsingName, SIIGBF_BIGGERSIZEOK,
                SIIGBF_ICONONLY,
            },
        },
    };
    struct Com(bool);
    impl Drop for Com {
        fn drop(&mut self) {
            if self.0 {
                unsafe { CoUninitialize() }
            }
        }
    }
    let _com = Com(unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED).is_ok() });
    // Shell namespace parsing does not consistently accept Rust's verbatim
    // canonical path prefix, even though filesystem APIs accept it.
    let display = path.to_string_lossy();
    let shell_path = if let Some(unc) = display.strip_prefix("\\\\?\\UNC\\") {
        format!("\\\\{unc}")
    } else {
        display
            .strip_prefix("\\\\?\\")
            .unwrap_or(&display)
            .to_string()
    };
    let path = std::ffi::OsStr::new(&shell_path)
        .encode_wide()
        .chain(Some(0))
        .collect::<Vec<_>>();
    let factory: IShellItemImageFactory = unsafe {
        SHCreateItemFromParsingName(PCWSTR(path.as_ptr()), None)
            .map_err(|e| {
                #[cfg(test)]
                eprintln!("Shell item: {e}");
                let _ = e;
            })
            .ok()?
    };
    let read = |size| unsafe {
        let image = factory
            .GetImage(
                SIZE { cx: size, cy: size },
                SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK,
            )
            .ok()?;
        read_bitmap(image.0)
    };
    let mut best = read(256)?;
    // Some Shell jumbo icons contain a 48px icon in a 256px canvas (and even
    // a faint frame). Ask for smaller native representations only in that
    // case; a smaller canvas can contain *more actual detail*. Never request
    // SIIGBF_SCALEUP and mistake interpolated pixels for a high-res resource.
    if best.is_padded() {
        for size in [128, 64] {
            if let Some(candidate) = read(size) {
                if candidate.detail_area() > best.detail_area() {
                    best = candidate;
                }
            }
        }
    }
    best.trim_padding().encode()
}

#[cfg(windows)]
fn read_bitmap(image: windows_sys::Win32::Graphics::Gdi::HBITMAP) -> Option<IconPixels> {
    use std::{mem, ptr};
    use windows_sys::Win32::Graphics::Gdi::*;
    struct Bitmap(HBITMAP);
    impl Drop for Bitmap {
        fn drop(&mut self) {
            unsafe {
                DeleteObject(self.0);
            }
        }
    }
    let image = Bitmap(image);
    let mut bitmap: BITMAP = unsafe { mem::zeroed() };
    if unsafe {
        GetObjectW(
            image.0,
            mem::size_of::<BITMAP>() as i32,
            &mut bitmap as *mut _ as _,
        )
    } == 0
    {
        return None;
    }
    let (width, height) = (bitmap.bmWidth, bitmap.bmHeight.abs());
    if !(1..=512).contains(&width) || !(1..=512).contains(&height) {
        return None;
    }
    let mut info = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: mem::size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: width,
            biHeight: -height,
            biPlanes: 1,
            biBitCount: 32,
            biCompression: BI_RGB,
            ..Default::default()
        },
        ..Default::default()
    };
    let mut bytes = vec![0u8; width as usize * height as usize * 4];
    let dc = unsafe { GetDC(ptr::null_mut()) };
    if dc.is_null() {
        return None;
    }
    let rows = unsafe {
        GetDIBits(
            dc,
            image.0,
            0,
            height as u32,
            bytes.as_mut_ptr().cast(),
            &mut info,
            DIB_RGB_COLORS,
        )
    };
    unsafe {
        ReleaseDC(ptr::null_mut(), dc);
    }
    if rows != height {
        return None;
    }
    unpremultiply_bgra(&mut bytes);
    Some(IconPixels {
        width: width as usize,
        height: height as usize,
        bytes,
    })
}

struct IconPixels {
    width: usize,
    height: usize,
    bytes: Vec<u8>,
}

impl IconPixels {
    fn bounds(&self) -> Option<(usize, usize, usize, usize)> {
        let (mut left, mut top, mut right, mut bottom) = (self.width, self.height, 0, 0);
        for (i, rgba) in self.bytes.chunks_exact(4).enumerate() {
            // Ignore faint Shell framing/shadows when measuring the real icon.
            // A fully translucent asset has no such bounds and is left intact.
            if rgba[3] >= 96 {
                let (x, y) = (i % self.width, i / self.width);
                left = left.min(x);
                top = top.min(y);
                right = right.max(x + 1);
                bottom = bottom.max(y + 1);
            }
        }
        (right > left && bottom > top).then_some((left, top, right, bottom))
    }
    fn detail_area(&self) -> usize {
        self.bounds().map_or(0, |(l, t, r, b)| (r - l) * (b - t))
    }
    fn is_padded(&self) -> bool {
        self.bounds()
            .is_some_and(|(l, t, r, b)| (r - l).max(b - t) * 100 < self.width.max(self.height) * 60)
    }
    fn trim_padding(self) -> Self {
        if !self.is_padded() {
            return self;
        }
        let (left, top, right, bottom) = self.bounds().unwrap();
        // Retain a little breathing space and the subject's anti-aliased edge.
        let margin = ((right - left).max(bottom - top) / 16).max(2);
        let (left, top) = (left.saturating_sub(margin), top.saturating_sub(margin));
        let (right, bottom) = (
            (right + margin).min(self.width),
            (bottom + margin).min(self.height),
        );
        let mut bytes = Vec::with_capacity((right - left) * (bottom - top) * 4);
        for y in top..bottom {
            bytes.extend_from_slice(
                &self.bytes[(y * self.width + left) * 4..(y * self.width + right) * 4],
            );
        }
        Self {
            width: right - left,
            height: bottom - top,
            bytes,
        }
    }
    fn encode(&self) -> Option<Vec<u8>> {
        let mut png = Vec::new();
        {
            let mut encoder = png::Encoder::new(&mut png, self.width as u32, self.height as u32);
            encoder.set_color(png::ColorType::Rgba);
            encoder.set_depth(png::BitDepth::Eight);
            encoder.set_compression(png::Compression::Best);
            let mut writer = encoder.write_header().ok()?;
            writer.write_image_data(&self.bytes).ok()?;
        }
        Some(png)
    }
}

fn unpremultiply_bgra(bytes: &mut [u8]) {
    for pixel in bytes.chunks_exact_mut(4) {
        pixel.swap(0, 2);
        let alpha = pixel[3] as u32;
        for channel in &mut pixel[..3] {
            *channel = (*channel as u32 * 255 + alpha / 2)
                .checked_div(alpha)
                .unwrap_or(0)
                .min(255) as u8;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn fixture(size: usize, subject: std::ops::Range<usize>, alpha: u8) -> IconPixels {
        let mut image = IconPixels {
            width: size,
            height: size,
            bytes: vec![0; size * size * 4],
        };
        for y in subject.clone() {
            for x in subject.clone() {
                image.bytes[(y * size + x) * 4..(y * size + x) * 4 + 4]
                    .copy_from_slice(&[90, 120, 180, alpha]);
            }
        }
        image
    }
    #[test]
    fn padded_jumbo_icon_is_cropped_without_upscaling_or_shell_frame() {
        let mut image = fixture(256, 104..150, 255);
        for x in 0..256 {
            image.bytes[x * 4..x * 4 + 4].copy_from_slice(&[255, 255, 255, 77]);
        }
        assert!(image.is_padded());
        let trimmed = image.trim_padding();
        assert_eq!((trimmed.width, trimmed.height), (50, 50));
        assert_eq!(trimmed.detail_area(), 46 * 46);
        assert!(trimmed.bytes.chunks_exact(4).all(|p| p[3] != 77));
    }
    #[test]
    fn full_size_translucent_and_asymmetric_icons_keep_their_shape() {
        assert_eq!(fixture(256, 10..246, 255).trim_padding().width, 256);
        assert_eq!(fixture(256, 104..150, 80).trim_padding().width, 256);
        let image = IconPixels {
            width: 256,
            height: 256,
            bytes: vec![0; 256 * 256 * 4],
        }
        .trim_padding();
        assert_eq!(image.width, 256);
        let mut wide = fixture(256, 0..0, 255);
        for y in 100..124 {
            for x in 90..162 {
                wide.bytes[(y * 256 + x) * 4 + 3] = 255;
            }
        }
        let wide = wide.trim_padding();
        assert_eq!((wide.width, wide.height), (80, 32));
        assert_eq!(wide.detail_area(), 72 * 24);
    }
    #[test]
    fn transparent_edges_keep_their_original_color() {
        let mut bytes = [25, 50, 100, 128, 5, 5, 5, 0];
        super::unpremultiply_bgra(&mut bytes);
        assert_eq!(bytes, [199, 100, 50, 128, 0, 0, 0, 0]);
    }

    #[cfg(windows)]
    #[test]
    fn shell_extracts_high_resolution_real_icon_and_shortcut() {
        // Modules now live in their own branch/worktree; the old sibling
        // native-launcher/src-tauri path no longer exists. Reuse the same
        // controlled host resolver used by the module UI build.
        let resolver =
            std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../scripts/host-ui.mjs");
        let resolved = std::process::Command::new("node")
            .arg(resolver)
            .output()
            .unwrap();
        assert!(
            resolved.status.success(),
            "Compatible host checkout required for the Shell icon fixture"
        );
        let host = String::from_utf8(resolved.stdout).unwrap();
        let icon = std::path::Path::new(host.trim())
            .join("QingToolbox.Tauri/src-tauri/icons/icon.ico")
            .canonicalize()
            .unwrap();
        let shortcut =
            std::env::temp_dir().join(format!("qing-icon-test-{}.url", std::process::id()));
        std::fs::write(&shortcut, format!("[InternetShortcut]\r\nURL=https://example.invalid/\r\nIconFile={}\r\nIconIndex=0\r\n", icon.display())).unwrap();
        for path in [&icon, &shortcut] {
            let bytes = super::extract(path)
                .unwrap_or_else(|| panic!("Shell icon extraction: {}", path.display()));
            let mut reader = png::Decoder::new(std::io::Cursor::new(&bytes))
                .read_info()
                .unwrap();
            assert!(
                reader.info().width >= 128,
                "Shell returned a small icon for {}",
                path.display()
            );
            let mut pixels = vec![0; reader.output_buffer_size()];
            let info = reader.next_frame(&mut pixels).unwrap();
            let pixels = &pixels[..info.buffer_size()];
            assert!(pixels.chunks_exact(4).any(|p| p[3] == 0));
            assert!(pixels.chunks_exact(4).any(|p| p[3] == 255));
        }
        let _ = std::fs::remove_file(shortcut);
    }
}
