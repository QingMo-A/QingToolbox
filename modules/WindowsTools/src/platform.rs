use base64::{engine::general_purpose::STANDARD, Engine};
use serde_json::Value;
use std::io::Write;
use std::{
    process::{Command, Stdio},
    time::{Duration, Instant},
};
use windows_sys::Win32::{
    Foundation::{CloseHandle, GetLastError},
    System::Threading::{GetExitCodeProcess, WaitForSingleObject, INFINITE},
    UI::{
        Shell::{ShellExecuteExW, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW},
        WindowsAndMessaging::{SW_HIDE, SW_SHOWNORMAL},
    },
};

fn wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(Some(0)).collect()
}
fn powershell() -> Result<std::path::PathBuf, String> {
    // Do not search PATH for a privileged executable.
    let root = std::env::var_os("SystemRoot").ok_or("缺少 Windows 系统目录。")?;
    let path =
        std::path::PathBuf::from(root).join("System32/WindowsPowerShell/v1.0/powershell.exe");
    if !path.is_file() {
        return Err("找不到 Windows PowerShell。".into());
    }
    Ok(path)
}
pub fn valid_action(action: &str) -> bool {
    matches!(
        action,
        "update.disable" | "update.enable" | "update.restore" | "firewall.repair"
    ) || ["Domain", "Private", "Public"].iter().any(|p| {
        ["disable", "enable", "restore"]
            .iter()
            .any(|op| action == format!("firewall.{p}.{op}"))
    })
}
fn encoded(action: &str) -> String {
    // Keep the fixed embedded script below Windows' command-line limit without
    // writing an elevated script to a user-writable temporary file.
    let mut zip = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::default());
    zip.write_all(include_bytes!("system.ps1"))
        .expect("memory compression");
    let data = STANDARD.encode(zip.finish().expect("memory compression"));
    let script = format!("[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);$m=[IO.MemoryStream]::new([Convert]::FromBase64String('{data}'));$g=[IO.Compression.GZipStream]::new($m,[IO.Compression.CompressionMode]::Decompress);$r=[IO.StreamReader]::new($g,[Text.Encoding]::UTF8);$s=$r.ReadToEnd();$r.Dispose(); & ([scriptblock]::Create($s)) -Action '{action}'");
    STANDARD.encode(
        script
            .encode_utf16()
            .flat_map(u16::to_le_bytes)
            .collect::<Vec<_>>(),
    )
}
pub fn snapshot() -> Result<Value, String> {
    use std::os::windows::process::CommandExt;
    let mut child = Command::new(powershell()?)
        .args([
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-EncodedCommand",
            &encoded("snapshot"),
        ])
        .creation_flags(0x08000000)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|e| e.to_string())?;
    let start = Instant::now();
    loop {
        if child.try_wait().map_err(|e| e.to_string())?.is_some() {
            break;
        }
        if start.elapsed() > Duration::from_secs(20) {
            let _ = child.kill();
            let _ = child.wait();
            return Err("读取系统状态超时，请重试。".into());
        }
        std::thread::sleep(Duration::from_millis(40));
    }
    let output = child.wait_with_output().map_err(|e| e.to_string())?;
    if !output.status.success() {
        return Err("读取系统状态失败，请检查 PowerShell 与系统管理组件是否可用。".into());
    }
    serde_json::from_slice(&output.stdout).map_err(|_| "系统状态返回格式无效。".into())
}
pub fn apply(action: &str) -> Result<(), String> {
    if !valid_action(action) {
        return Err("不支持的系统操作。".into());
    }
    let exe = wide(&powershell()?.to_string_lossy());
    let verb = wide("runas");
    let parameters = wide(&format!(
        "-NoLogo -NoProfile -NonInteractive -EncodedCommand {}",
        encoded(action)
    ));
    let mut info: SHELLEXECUTEINFOW = unsafe { std::mem::zeroed() };
    info.cbSize = std::mem::size_of::<SHELLEXECUTEINFOW>() as u32;
    info.fMask = SEE_MASK_NOCLOSEPROCESS;
    info.lpVerb = verb.as_ptr();
    info.lpFile = exe.as_ptr();
    info.lpParameters = parameters.as_ptr();
    info.nShow = SW_HIDE;
    if unsafe { ShellExecuteExW(&mut info) } == 0 {
        return Err(if unsafe { GetLastError() } == 1223 {
            "已取消管理员授权，未修改系统设置。".into()
        } else {
            format!("无法请求管理员权限（{}）。", unsafe {
                GetLastError()
            })
        });
    }
    if info.hProcess.is_null() {
        return Err("无法取得系统操作进程。".into());
    }
    // Runs on a worker, never inside the host IPC request. No arbitrary timeout
    // kills the helper halfway through a configuration change.
    let mut code = 20;
    unsafe {
        WaitForSingleObject(info.hProcess, INFINITE);
        GetExitCodeProcess(info.hProcess, &mut code);
        CloseHandle(info.hProcess);
    }
    match code {
        0 => Ok(()),
        21 => Err("没有管理员权限，未修改系统设置。".into()),
        22 => Err("另一个 Windows 设置助手正在修改配置，请稍后重试。".into()),
        23 => Err("此设备受域或 MDM 管理，请联系组织管理员。".into()),
        24 => Err("当前 Windows 版本不支持此自动更新策略，请使用系统更新设置。".into()),
        25 => Err("设置已被其他程序或管理员更改，为避免覆盖已停止操作。".into()),
        26 => Err("未找到此项的修改前备份。".into()),
        27 => Err("配置备份无效，已停止操作。".into()),
        28 => Err("写入后的系统配置与目标值不一致，不能确认成功。备份已保留，请刷新查看。".into()),
        29 => Err("防火墙服务不可用。请先使用模块中的“检查并修复服务”，再操作网络配置。".into()),
        30 => Err("服务修复未完成。可能受系统保护、组织策略或第三方安全软件限制；请刷新查看诊断。模块未修改服务权限或重置防火墙规则。".into()),
        _ => Err(format!(
            "系统操作未完成（退出码 {code}）。请刷新状态；如有备份可尝试恢复。"
        )),
    }
}
pub fn open_settings(page: &str) -> Result<(), String> {
    let uri = match page {
        "update" => "ms-settings:windowsupdate",
        "firewall" => "windowsdefender://network",
        "defaults" => "ms-settings:defaultapps",
        "registered" => "ms-settings:defaultapps?registeredAppUser=QingToolbox.WindowsTools",
        _ => return Err("不支持的设置页面。".into()),
    };
    let file = wide(uri);
    let verb = wide("open");
    let mut info: SHELLEXECUTEINFOW = unsafe { std::mem::zeroed() };
    info.cbSize = std::mem::size_of::<SHELLEXECUTEINFOW>() as u32;
    info.lpVerb = verb.as_ptr();
    info.lpFile = file.as_ptr();
    info.nShow = SW_SHOWNORMAL;
    if unsafe { ShellExecuteExW(&mut info) } == 0 {
        return Err("无法打开系统设置。".into());
    }
    Ok(())
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn actions_are_closed_set() {
        for a in [
            "update.disable",
            "update.enable",
            "update.restore",
            "firewall.Private.disable",
            "firewall.Public.enable",
            "firewall.Domain.restore",
            "firewall.repair",
        ] {
            assert!(valid_action(a))
        }
        for a in [
            "snapshot",
            "UPDATE.disable",
            "update.disable;whoami",
            "firewall.All.disable",
            "update.disable\n",
            "firewall.Public.delete",
        ] {
            assert!(!valid_action(a))
        }
    }
    #[test]
    fn command_fits_windows_limit() {
        assert!(encoded("firewall.Private.restore").len() + 100 < 32_000);
    }
}
