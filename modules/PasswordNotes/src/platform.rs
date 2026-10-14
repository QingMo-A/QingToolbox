use crate::model::{Error, Result};
use std::path::Path;
use zeroize::{Zeroize, Zeroizing};

pub fn valid_link(value: &str) -> bool {
    if !value.starts_with("https://") && !value.starts_with("http://") {
        return false;
    }
    if value
        .chars()
        .any(|c| c.is_whitespace() || c.is_control() || c == '\\')
    {
        return false;
    }
    url::Url::parse(value).is_ok_and(|url| {
        matches!(url.scheme(), "http" | "https")
            && url.host_str().is_some()
            && url.username().is_empty()
            && url.password().is_none()
    })
}

#[cfg(windows)]
pub fn protect(input: &[u8], decrypt: bool) -> Result<Zeroizing<Vec<u8>>> {
    use std::ptr;
    use windows_sys::Win32::{
        Foundation::LocalFree,
        Security::Cryptography::{
            CryptProtectData, CryptUnprotectData, CRYPTPROTECT_UI_FORBIDDEN, CRYPT_INTEGER_BLOB,
        },
    };
    let mut data = CRYPT_INTEGER_BLOB {
        cbData: input
            .len()
            .try_into()
            .map_err(|_| Error::new("vaultTooLarge", "数据过大。"))?,
        pbData: input.as_ptr().cast_mut(),
    };
    let entropy_bytes = b"QingToolbox qing.passwordnotes vault v1";
    let mut entropy = CRYPT_INTEGER_BLOB {
        cbData: entropy_bytes.len() as u32,
        pbData: entropy_bytes.as_ptr().cast_mut(),
    };
    let mut out = CRYPT_INTEGER_BLOB {
        cbData: 0,
        pbData: ptr::null_mut(),
    };
    let ok = unsafe {
        if decrypt {
            CryptUnprotectData(
                &mut data,
                ptr::null_mut(),
                &mut entropy,
                ptr::null_mut(),
                ptr::null_mut(),
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut out,
            )
        } else {
            CryptProtectData(
                &mut data,
                ptr::null(),
                &mut entropy,
                ptr::null_mut(),
                ptr::null_mut(),
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut out,
            )
        }
    };
    if ok == 0 {
        return Err(Error::new(
            "protectionFailed",
            "无法加密或解密数据。请确认使用原 Windows 用户和设备；不会覆盖原数据。",
        ));
    }
    let bytes = unsafe {
        let slice = std::slice::from_raw_parts_mut(out.pbData, out.cbData as usize);
        let bytes = Zeroizing::new(slice.to_vec());
        slice.zeroize();
        LocalFree(out.pbData.cast());
        bytes
    };
    Ok(bytes)
}
#[cfg(not(windows))]
pub fn protect(_: &[u8], _: bool) -> Result<Zeroizing<Vec<u8>>> {
    Err(Error::new(
        "platformUnsupported",
        "此模块的加密存储仅支持 Windows。",
    ))
}

#[cfg(windows)]
pub fn replace_file(from: &Path, to: &Path) -> Result<()> {
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::Storage::FileSystem::{
        MoveFileExW, MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH,
    };
    let wide = |p: &Path| {
        p.as_os_str()
            .encode_wide()
            .chain(Some(0))
            .collect::<Vec<u16>>()
    };
    if unsafe {
        MoveFileExW(
            wide(from).as_ptr(),
            wide(to).as_ptr(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
    } == 0
    {
        Err(Error::new(
            "writeFailed",
            "无法完成原子保存，请检查磁盘空间和文件权限。",
        ))
    } else {
        Ok(())
    }
}
#[cfg(not(windows))]
pub fn replace_file(from: &Path, to: &Path) -> Result<()> {
    std::fs::rename(from, to).map_err(|_| Error::new("writeFailed", "无法保存文件。"))
}

#[cfg(windows)]
pub fn copy(value: &str) -> Result<()> {
    use std::{ptr, thread, time::Duration};
    use windows_sys::Win32::{
        Foundation::GlobalFree,
        System::{
            DataExchange::{
                CloseClipboard, EmptyClipboard, OpenClipboard, RegisterClipboardFormatW,
                SetClipboardData,
            },
            Memory::{GlobalAlloc, GlobalLock, GlobalUnlock, GMEM_MOVEABLE},
        },
        UI::WindowsAndMessaging::{CreateWindowExW, DestroyWindow, HWND_MESSAGE},
    };
    let class: Vec<_> = "STATIC\0".encode_utf16().collect();
    // A real message-only owner avoids OpenClipboard(NULL)/SetClipboardData ownership failures.
    let owner = unsafe {
        CreateWindowExW(
            0,
            class.as_ptr(),
            class.as_ptr(),
            0,
            0,
            0,
            0,
            0,
            HWND_MESSAGE,
            ptr::null_mut(),
            ptr::null_mut(),
            ptr::null(),
        )
    };
    if owner.is_null() {
        return Err(Error::new("clipboardUnavailable", "无法创建剪贴板会话。"));
    }
    let opened = (0..8).any(|attempt| {
        if unsafe { OpenClipboard(owner) } != 0 {
            true
        } else {
            if attempt < 7 {
                thread::sleep(Duration::from_millis(20));
            }
            false
        }
    });
    let result = if opened {
        let put = |format: u32, data: &[u8]| -> Result<()> {
            if format == 0 {
                return Err(Error::new(
                    "clipboardUnavailable",
                    "无法设置剪贴板保护标志。",
                ));
            }
            let handle = unsafe { GlobalAlloc(GMEM_MOVEABLE, data.len()) };
            if handle.is_null() {
                return Err(Error::new("clipboardUnavailable", "剪贴板内存不可用。"));
            }
            let target = unsafe { GlobalLock(handle) };
            if target.is_null() {
                unsafe {
                    GlobalFree(handle);
                }
                return Err(Error::new("clipboardUnavailable", "剪贴板内存不可用。"));
            }
            unsafe {
                ptr::copy_nonoverlapping(data.as_ptr(), target.cast(), data.len());
                GlobalUnlock(handle);
            }
            if unsafe { SetClipboardData(format, handle) }.is_null() {
                // Ownership was not transferred. Wipe the unpublished allocation before releasing it.
                let target = unsafe { GlobalLock(handle) };
                if !target.is_null() {
                    unsafe {
                        std::slice::from_raw_parts_mut(target.cast::<u8>(), data.len()).zeroize();
                        GlobalUnlock(handle);
                    }
                }
                unsafe {
                    GlobalFree(handle);
                }
                return Err(Error::new("clipboardUnavailable", "无法写入剪贴板。"));
            }
            Ok(())
        };
        (|| {
            if unsafe { EmptyClipboard() } == 0 {
                return Err(Error::new(
                    "clipboardUnavailable",
                    "无法打开剪贴板，请稍后重试。",
                ));
            }
            for name in [
                "ExcludeClipboardContentFromMonitorProcessing",
                "CanIncludeInClipboardHistory",
                "CanUploadToCloudClipboard",
            ] {
                let wide: Vec<_> = name.encode_utf16().chain(Some(0)).collect();
                let format = unsafe { RegisterClipboardFormatW(wide.as_ptr()) };
                put(format, &0_u32.to_ne_bytes())?;
            }
            let wide = Zeroizing::new(value.encode_utf16().chain(Some(0)).collect::<Vec<u16>>());
            let data =
                unsafe { std::slice::from_raw_parts(wide.as_ptr().cast::<u8>(), wide.len() * 2) };
            put(13, data)
        })()
    } else {
        Err(Error::new("clipboardBusy", "剪贴板正被占用，请稍后重试。"))
    };
    unsafe {
        if opened {
            CloseClipboard();
        }
        DestroyWindow(owner);
    }
    result
}
#[cfg(not(windows))]
pub fn copy(_: &str) -> Result<()> {
    Err(Error::new("platformUnsupported", "复制仅支持 Windows。"))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn links_reject_credentials_and_nonweb_schemes() {
        assert!(valid_link("https://example.com/game"));
        for v in [
            "file:///secret",
            "javascript:alert(1)",
            "https://name:password@example.com",
            "https://example.com\\bad",
            "https://",
            "https://example.com\n",
        ] {
            assert!(!valid_link(v));
        }
    }
    #[test]
    #[cfg(windows)]
    fn dpapi_roundtrip_does_not_store_plaintext_and_detects_tampering() {
        let secret = b"Synthetic-test-password-not-a-real-account";
        let mut sealed = protect(secret, false).unwrap();
        assert!(!sealed.windows(secret.len()).any(|v| v == secret));
        assert_eq!(&*protect(&sealed, true).unwrap(), secret);
        let index = sealed.len() / 2;
        sealed[index] ^= 0x55;
        assert!(protect(&sealed, true).is_err());
    }
}
