use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::path::Path;
use windows_sys::Win32::UI::Shell::{
    AssocQueryStringW, SHChangeNotify, ASSOCSTR_EXECUTABLE, ASSOCSTR_FRIENDLYAPPNAME,
    SHCNE_ASSOCCHANGED, SHCNF_IDLIST,
};
use winreg::{enums::*, RegKey, RegValue};

pub fn extension(value: &str) -> Result<String, String> {
    let value = value.trim();
    let value = value.strip_prefix('.').unwrap_or(value).to_lowercase();
    if value.is_empty()
        || value.chars().count() > 32
        || !value
            .chars()
            .all(|ch| ch.is_alphanumeric() || ch == '_' || ch == '-')
    {
        return Err(
            "请输入有效后缀，例如 .pdf、txt 或 .自定义；不要包含路径、空格或多个点。".into(),
        );
    }
    if [
        "exe",
        "com",
        "bat",
        "cmd",
        "ps1",
        "psm1",
        "psd1",
        "vbs",
        "vbe",
        "js",
        "jse",
        "wsf",
        "wsh",
        "msi",
        "msp",
        "mst",
        "scr",
        "pif",
        "lnk",
        "url",
        "reg",
        "dll",
        "sys",
        "cpl",
        "appx",
        "msix",
        "appxbundle",
        "msixbundle",
        "appref-ms",
        "msc",
        "hta",
    ]
    .contains(&value.as_str())
    {
        return Err(
            "为避免破坏系统启动或脚本行为，此版本不修改可执行文件、脚本及系统快捷方式的关联。"
                .into(),
        );
    }
    Ok(format!(".{value}"))
}
fn query(ext: &str, kind: i32) -> Option<String> {
    let ext = ext.encode_utf16().chain(Some(0)).collect::<Vec<_>>();
    let mut out = vec![0u16; 32_768];
    let mut size = out.len() as u32;
    let result = unsafe {
        AssocQueryStringW(
            0,
            kind,
            ext.as_ptr(),
            std::ptr::null(),
            out.as_mut_ptr(),
            &mut size,
        )
    };
    (result == 0 && size > 1)
        .then(|| String::from_utf16_lossy(&out[..(size as usize - 1).min(out.len())]))
}
pub fn inspect(value: &str) -> Result<Value, String> {
    let ext = extension(value)?;
    let app = query(&ext, ASSOCSTR_FRIENDLYAPPNAME);
    let executable = query(&ext, ASSOCSTR_EXECUTABLE);
    Ok(json!({"extension": ext, "application": app, "executable": executable}))
}
pub fn validate_executable(executable: &str) -> Result<(), String> {
    let path = Path::new(executable);
    if !path.is_absolute()
        || !path
            .extension()
            .is_some_and(|x| x.eq_ignore_ascii_case("exe"))
        || executable.chars().any(|ch| ch == '"' || ch.is_control())
        || executable.starts_with("\\\\")
        || executable.len() > 1024
        || !path.is_file()
    {
        return Err("请选择本地磁盘上有效的 .exe 程序，不支持网络路径或命令行参数。".into());
    }
    Ok(())
}
pub fn register(value: &str, executable: &str) -> Result<Value, String> {
    let ext = extension(value)?;
    validate_executable(executable)?;
    let path = Path::new(executable);
    let display = path
        .file_stem()
        .unwrap_or_default()
        .to_string_lossy()
        .to_string();
    // Own namespace only. Never write UserChoice, an extension's default value,
    // an existing application's registration, or any association hash.
    // Bind the ProgID to BOTH extension and executable. Otherwise re-registering
    // a different executable could silently change an already selected default.
    let hash = Sha256::digest(format!("{ext}\0{executable}").as_bytes());
    let id = format!("QingToolbox.WindowsTools.{hash:x}");
    let user = RegKey::predef(HKEY_CURRENT_USER);
    let result = (|| -> std::io::Result<()> {
        let (class, _) = user.create_subkey(format!("Software\\Classes\\{id}"))?;
        class.set_value("", &format!("{ext} · {display}"))?;
        let (application, _) = class.create_subkey("Application")?;
        application.set_value("ApplicationName", &display)?;
        application.set_value("ApplicationIcon", &format!("\"{executable}\",0"))?;
        let (command, _) = class.create_subkey("shell\\open\\command")?;
        command.set_value("", &format!("\"{executable}\" \"%1\""))?;
        let (icon, _) = class.create_subkey("DefaultIcon")?;
        icon.set_value("", &format!("\"{executable}\",0"))?;
        let (open_with, _) =
            user.create_subkey(format!("Software\\Classes\\{ext}\\OpenWithProgids"))?;
        open_with.set_raw_value(
            &id,
            &RegValue {
                vtype: REG_NONE,
                bytes: Vec::new(),
            },
        )?;
        let cap_path = "Software\\QingToolbox\\WindowsTools\\Capabilities";
        let (cap, _) = user.create_subkey(cap_path)?;
        cap.set_value("ApplicationName", &"QingToolbox 自定义后缀")?;
        cap.set_value(
            "ApplicationDescription",
            &"用户通过 Windows 设置助手添加的文件打开方式",
        )?;
        let (types, _) = cap.create_subkey("FileAssociations")?;
        types.set_value(&ext, &id)?;
        let (apps, _) = user.create_subkey("Software\\RegisteredApplications")?;
        apps.set_value("QingToolbox.WindowsTools", &cap_path)?;
        Ok(())
    })();
    result.map_err(|e| format!("注册候选打开方式失败：{e}"))?;
    unsafe {
        SHChangeNotify(
            SHCNE_ASSOCCHANGED as i32,
            SHCNF_IDLIST,
            std::ptr::null(),
            std::ptr::null(),
        );
    }
    Ok(
        json!({ "extension": ext, "candidate": display, "message": "已注册候选程序，尚未修改默认应用。请在 Windows 设置中选择此后缀并确认。" }),
    )
}

/// Migration from 0.1.0, which registered candidates but had no table file.
/// Reads only our exact namespace; never imports arbitrary shell commands.
pub fn registered_candidates() -> Vec<(String, String)> {
    let user = RegKey::predef(HKEY_CURRENT_USER);
    let Ok(types) =
        user.open_subkey("Software\\QingToolbox\\WindowsTools\\Capabilities\\FileAssociations")
    else {
        return vec![];
    };
    types
        .enum_values()
        .flatten()
        .take(128)
        .filter_map(|(ext, _)| {
            let ext = extension(&ext).ok()?;
            let id: String = types.get_value(&ext).ok()?;
            let hash = id.strip_prefix("QingToolbox.WindowsTools.")?;
            if hash.len() != 64 || !hash.bytes().all(|b| b.is_ascii_hexdigit()) {
                return None;
            }
            let command = user
                .open_subkey(format!("Software\\Classes\\{id}\\shell\\open\\command"))
                .ok()?;
            let value: String = command.get_value("").ok()?;
            let exe = value.strip_prefix('"')?.strip_suffix("\" \"%1\"")?;
            if exe.contains('"') || exe.chars().any(char::is_control) {
                return None;
            }
            Some((ext, exe.to_string()))
        })
        .collect()
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn extension_validation() {
        for (input, out) in [
            ("PDF", ".pdf"),
            (" .hello_2 ", ".hello_2"),
            (".自定义", ".自定义"),
        ] {
            assert_eq!(extension(input).unwrap(), out)
        }
        for bad in [
            "", ".", "..txt", "a/b", "a.b", "a b", "exe", ".LNK", "ps1", "a\0b", "a:exe", "x\"y",
        ] {
            assert!(extension(bad).is_err(), "{bad}")
        }
    }
}
