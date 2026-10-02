//! Shell-owned, high-resolution icons (not file previews or enlarged 32px icons).
#[cfg(windows)]
pub fn extract(path: &std::path::Path) -> Option<Vec<u8>> {
    use std::{mem, os::windows::ffi::OsStrExt, ptr};
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
    use windows_sys::Win32::Graphics::Gdi::*;
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
    let image = unsafe {
        factory
            .GetImage(
                SIZE { cx: 256, cy: 256 },
                SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK,
            )
            .map_err(|e| {
                #[cfg(test)]
                eprintln!("Shell image: {e}");
                let _ = e;
            })
            .ok()?
    };
    struct Bitmap(HBITMAP);
    impl Drop for Bitmap {
        fn drop(&mut self) {
            unsafe {
                DeleteObject(self.0);
            }
        }
    }
    let image = Bitmap(image.0);
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
    let mut png = Vec::new();
    {
        let mut encoder = png::Encoder::new(&mut png, width as u32, height as u32);
        encoder.set_color(png::ColorType::Rgba);
        encoder.set_depth(png::BitDepth::Eight);
        encoder.set_compression(png::Compression::Best);
        let mut writer = encoder.write_header().ok()?;
        writer.write_image_data(&bytes).ok()?;
    }
    Some(png)
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
    #[test]
    fn transparent_edges_keep_their_original_color() {
        let mut bytes = [25, 50, 100, 128, 5, 5, 5, 0];
        super::unpremultiply_bgra(&mut bytes);
        assert_eq!(bytes, [199, 100, 50, 128, 0, 0, 0, 0]);
    }

    #[cfg(windows)]
    #[test]
    fn shell_extracts_high_resolution_real_icon_and_shortcut() {
        let icon = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../src-tauri/icons/icon.ico")
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
