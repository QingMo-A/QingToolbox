#[cfg(windows)]
pub fn copy(png: &[u8]) -> Result<(), String> {
    use windows_sys::Win32::{
        Foundation::GlobalFree,
        System::{DataExchange::*, Memory::*},
        UI::WindowsAndMessaging::*,
    };
    let decoder = png::Decoder::new(std::io::Cursor::new(png));
    let mut reader = decoder.read_info().map_err(|e| e.to_string())?;
    let mut rgba = vec![0; reader.output_buffer_size()];
    let info = reader.next_frame(&mut rgba).map_err(|e| e.to_string())?;
    if info.color_type != png::ColorType::Rgba {
        return Err("截图像素格式无效。".into());
    }
    let mut dib = vec![0u8; 40 + info.buffer_size()];
    dib[0..4].copy_from_slice(&40u32.to_le_bytes());
    dib[4..8].copy_from_slice(&(info.width as i32).to_le_bytes());
    dib[8..12].copy_from_slice(&(-(info.height as i32)).to_le_bytes());
    dib[12..14].copy_from_slice(&1u16.to_le_bytes());
    dib[14..16].copy_from_slice(&32u16.to_le_bytes());
    for (src, dst) in rgba[..info.buffer_size()]
        .chunks_exact(4)
        .zip(dib[40..].chunks_exact_mut(4))
    {
        dst.copy_from_slice(&[src[2], src[1], src[0], 255]);
    }
    unsafe {
        let class: Vec<u16> = "STATIC\0".encode_utf16().collect();
        let owner = CreateWindowExW(
            0,
            class.as_ptr(),
            class.as_ptr(),
            0,
            0,
            0,
            0,
            0,
            HWND_MESSAGE,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            std::ptr::null(),
        );
        if owner.is_null() {
            return Err("无法创建剪贴板所有者。".into());
        }
        let memory = GlobalAlloc(GMEM_MOVEABLE, dib.len());
        if memory.is_null() {
            DestroyWindow(owner);
            return Err("复制内存不足。".into());
        }
        let dest = GlobalLock(memory);
        if dest.is_null() {
            GlobalFree(memory);
            DestroyWindow(owner);
            return Err("无法访问复制内存。".into());
        }
        std::ptr::copy_nonoverlapping(dib.as_ptr(), dest.cast(), dib.len());
        GlobalUnlock(memory);
        if OpenClipboard(owner) == 0 {
            GlobalFree(memory);
            DestroyWindow(owner);
            return Err("剪贴板正在被占用，请重试。".into());
        }
        let ok = EmptyClipboard() != 0 && !SetClipboardData(8, memory).is_null(); // CF_DIB
        CloseClipboard();
        DestroyWindow(owner);
        if !ok {
            GlobalFree(memory);
            return Err("复制截图失败。".into());
        }
    }
    Ok(())
}
#[cfg(not(windows))]
pub fn copy(_: &[u8]) -> Result<(), String> {
    Err("截图复制仅支持 Windows。".into())
}
