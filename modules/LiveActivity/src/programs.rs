//! Presence only: process names and parent IDs, never memory, command lines or chats.
use std::{
    collections::{HashMap, HashSet},
    time::{Duration, Instant},
};

pub const PROBE_INTERVAL: Duration = Duration::from_secs(5);
const MAX_PROCESSES: usize = 8192;

struct Process {
    pid: u32,
    parent: u32,
    name: String,
}

fn external_codex(processes: &[Process], own_pid: u32) -> bool {
    let mut children: HashMap<u32, Vec<u32>> = HashMap::new();
    let mut pending = vec![own_pid];
    for p in processes {
        children.entry(p.parent).or_default().push(p.pid);
        // Exclude other module instances too (Dev and installed can coexist).
        if p.name.eq_ignore_ascii_case("qing-liveactivity-module.exe") {
            pending.push(p.pid);
        }
    }
    let mut owned = HashSet::new();
    while let Some(pid) = pending.pop() {
        if owned.insert(pid) {
            pending.extend(children.get(&pid).into_iter().flatten().copied());
        }
    }
    processes
        .iter()
        .any(|p| p.name.eq_ignore_ascii_case("codex.exe") && !owned.contains(&p.pid))
}

#[cfg(windows)]
fn probe() -> Result<bool, String> {
    use windows_sys::Win32::{
        Foundation::{CloseHandle, GetLastError, ERROR_NO_MORE_FILES, INVALID_HANDLE_VALUE},
        System::Diagnostics::ToolHelp::{
            CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W,
            TH32CS_SNAPPROCESS,
        },
    };
    unsafe {
        let handle = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if handle == INVALID_HANDLE_VALUE {
            return Err("无法检测 Codex 程序状态".into());
        }
        struct Snapshot(windows_sys::Win32::Foundation::HANDLE);
        impl Drop for Snapshot {
            fn drop(&mut self) {
                unsafe {
                    CloseHandle(self.0);
                }
            }
        }
        let _snapshot = Snapshot(handle);
        let mut entry: PROCESSENTRY32W = std::mem::zeroed();
        entry.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
        let mut more = Process32FirstW(handle, &mut entry);
        let mut processes = Vec::new();
        while more != 0 {
            if processes.len() >= MAX_PROCESSES {
                return Err("程序状态检测超过安全数量限制".into());
            }
            let length = entry
                .szExeFile
                .iter()
                .position(|c| *c == 0)
                .unwrap_or(entry.szExeFile.len());
            processes.push(Process {
                pid: entry.th32ProcessID,
                parent: entry.th32ParentProcessID,
                name: String::from_utf16_lossy(&entry.szExeFile[..length]),
            });
            more = Process32NextW(handle, &mut entry);
        }
        if GetLastError() != ERROR_NO_MORE_FILES {
            return Err("程序状态检测未完成，稍后重试".into());
        }
        Ok(external_codex(&processes, std::process::id()))
    }
}
#[cfg(not(windows))]
fn probe() -> Result<bool, String> {
    Err("当前平台不支持程序检测".into())
}

#[derive(Debug, Default, Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProgramState {
    pub running: bool,
    pub checked_at_ms: Option<u64>,
    pub error: Option<String>,
}

pub struct ProgramMonitor {
    pub state: ProgramState,
    checked: Option<Instant>,
    #[cfg(test)]
    forced: Option<bool>,
}
impl ProgramMonitor {
    pub fn new() -> Self {
        Self {
            state: ProgramState::default(),
            checked: None,
            #[cfg(test)]
            forced: Some(false),
        }
    }
    pub fn clear(&mut self) {
        self.state = ProgramState::default();
        self.checked = None;
    }
    pub fn until_probe(&self) -> Duration {
        self.checked
            .map(|at| PROBE_INTERVAL.saturating_sub(at.elapsed()))
            .unwrap_or_default()
    }
    pub fn update(&mut self, force: bool) -> bool {
        if !force && !self.until_probe().is_zero() {
            return false;
        }
        let result = probe_result(self);
        let (running, error) = match result {
            Ok(running) => (running, None),
            Err(error) => (false, Some(error)),
        };
        let changed = self.state.running != running || self.state.error != error;
        if changed {
            crate::diagnostics::information(
                "codex/program",
                if error.is_some() {
                    "program probe failed; acquisition stopped"
                } else if running {
                    "external Codex detected; acquisition enabled"
                } else {
                    "external Codex exited; acquisition stopped"
                },
            );
        }
        self.state = ProgramState {
            running,
            error,
            checked_at_ms: Some(crate::activity::now_millis()),
        };
        self.checked = Some(Instant::now());
        changed
    }
    #[cfg(test)]
    pub fn force_for_test(&mut self, running: bool) {
        self.forced = Some(running);
        self.checked = None;
    }
}
fn probe_result(monitor: &ProgramMonitor) -> Result<bool, String> {
    #[cfg(test)]
    if let Some(running) = monitor.forced {
        return Ok(running);
    }
    #[cfg(not(test))]
    let _ = monitor;
    probe()
}

#[cfg(test)]
mod tests {
    use super::*;
    fn p(pid: u32, parent: u32, name: &str) -> Process {
        Process {
            pid,
            parent,
            name: name.into(),
        }
    }
    #[test]
    fn owned_helpers_and_other_module_instances_do_not_self_sustain() {
        let processes = vec![
            p(1, 0, "qing-liveactivity-module.exe"),
            p(2, 1, "cmd.exe"),
            p(3, 2, "codex.exe"),
            p(4, 0, "qing-liveactivity-module.exe"),
            p(5, 4, "Codex.exe"),
        ];
        assert!(!external_codex(&processes, 1));
        let mut external = processes;
        external.push(p(6, 0, "CODEX.EXE"));
        assert!(external_codex(&external, 1));
    }
    #[test]
    fn cycles_and_similar_names_do_not_match() {
        assert!(!external_codex(
            &[
                p(1, 2, "island.exe"),
                p(2, 1, "codex.exe"),
                p(3, 0, "not-codex.exe")
            ],
            1
        ));
    }
    #[test]
    fn probes_are_throttled_and_pause_resets_detection() {
        let mut monitor = ProgramMonitor::new();
        monitor.force_for_test(true);
        assert!(monitor.update(false));
        assert!(monitor.state.running);
        assert!(!monitor.update(false));
        monitor.clear();
        assert!(!monitor.state.running);
        monitor.force_for_test(false);
        assert!(!monitor.update(false));
    }
}
