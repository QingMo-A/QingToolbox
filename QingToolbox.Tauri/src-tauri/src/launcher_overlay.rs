//! Presentation and dismissal protection for the launcher only. Ordinary
//! module windows keep their existing native window behaviour.
use std::time::{Duration, Instant};
use tauri::{Emitter, Manager, Runtime, WebviewWindow};

pub const MODULE_ID: &str = "qing.launcher";
pub const DRAG_EVENT: &str = "launcher:external-drag";

#[derive(Default)]
pub struct DropProtection {
    active: bool,
    importing: bool,
    settled_until: Option<Instant>,
}

impl DropProtection {
    pub fn enter(&mut self) -> bool {
        let changed = !self.active;
        self.active = true;
        changed
    }
    pub fn finish(&mut self) {
        self.active = false;
        self.importing = false;
        self.settled_until = Some(Instant::now() + Duration::from_millis(600));
    }
    fn begin_import(&mut self) {
        self.active = true;
        self.importing = true;
    }
    fn leave(&mut self) -> bool {
        if self.importing {
            return false;
        }
        self.finish();
        true
    }
    pub fn can_dismiss(&self) -> bool {
        self.can_dismiss_at(Instant::now())
    }
    fn can_dismiss_at(&self, now: Instant) -> bool {
        !self.active && self.settled_until.map_or(true, |until| now >= until)
    }
}

pub fn can_dismiss<R: Runtime>(window: &WebviewWindow<R>) -> bool {
    window
        .app_handle()
        .try_state::<crate::HostState>()
        .and_then(|state| {
            state
                .launcher_drop_protection
                .lock()
                .ok()
                .map(|guard| guard.can_dismiss())
        })
        .unwrap_or(false)
}

pub fn drag_state<R: Runtime>(app: &tauri::AppHandle<R>, label: &str, active: bool) {
    let changed = app
        .try_state::<crate::HostState>()
        .and_then(|state| {
            state.launcher_drop_protection.lock().ok().map(|mut guard| {
                if active {
                    guard.enter()
                } else {
                    guard.finish();
                    true
                }
            })
        })
        .unwrap_or(false);
    if changed {
        let _ = app.emit_to(label, DRAG_EVENT, serde_json::json!({ "active": active }));
    }
}

pub fn begin_import<R: Runtime>(app: &tauri::AppHandle<R>, label: &str) {
    if let Some(state) = app.try_state::<crate::HostState>() {
        if let Ok(mut guard) = state.launcher_drop_protection.lock() {
            guard.begin_import();
        }
    }
    let _ = app.emit_to(label, DRAG_EVENT, serde_json::json!({ "active": true }));
}

pub fn leave<R: Runtime>(app: &tauri::AppHandle<R>, label: &str) {
    let finished = app
        .try_state::<crate::HostState>()
        .and_then(|state| {
            state
                .launcher_drop_protection
                .lock()
                .ok()
                .map(|mut guard| guard.leave())
        })
        .unwrap_or(false);
    if finished {
        let _ = app.emit_to(label, DRAG_EVENT, serde_json::json!({ "active": false }));
    }
}

/// Re-evaluate the monitor on every invocation (including mixed-DPI setups).
/// The native surface is panel-sized, never a monitor-sized transparent
/// input shield (which blocks Explorer and resembles a fullscreen app).
pub fn show<R: Runtime>(window: &WebviewWindow<R>) -> tauri::Result<()> {
    let app = window.app_handle();
    let monitor = app
        .cursor_position()
        .ok()
        .and_then(|p| window.monitor_from_point(p.x, p.y).ok().flatten())
        .or_else(|| window.current_monitor().ok().flatten())
        .or_else(|| window.primary_monitor().ok().flatten());
    if let Some(monitor) = monitor {
        let area = monitor.work_area();
        let (width, height, x, y) =
            panel_bounds(area.size.width, area.size.height, monitor.scale_factor());
        window.set_size(tauri::PhysicalSize::new(width, height))?;
        window.set_position(tauri::PhysicalPosition::new(
            area.position.x + x,
            area.position.y + y,
        ))?;
    }
    window.set_always_on_top(true)?;
    window.unminimize()?;
    window.show()?;
    if let Some(state) = app.try_state::<crate::HostState>() {
        if let Ok(mut observer) = state.launcher_outside_click.lock() {
            observer.take();
            *observer = crate::launcher_outside_click::start(window);
        }
    }
    // Taking focus during an Explorer OLE drag can cancel the source drag.
    #[cfg(windows)]
    let dragging =
        unsafe { windows_sys::Win32::UI::Input::KeyboardAndMouse::GetAsyncKeyState(1) < 0 };
    #[cfg(not(windows))]
    let dragging = false;
    if !dragging {
        let _ = window.set_focus();
    }
    let _ = window.emit(
        "launcher:shown",
        serde_json::json!({ "focusSearch": !dragging }),
    );
    Ok(())
}

fn panel_bounds(width: u32, height: u32, scale: f64) -> (u32, u32, i32, i32) {
    let margin = (16.0 * scale).round() as u32;
    let w = ((988.0 * scale).round() as u32).min(width.saturating_sub(margin * 2).max(1));
    let h = ((642.0 * scale).round() as u32).min(height.saturating_sub(margin * 2).max(1));
    (w, h, ((width - w) / 2) as i32, ((height - h) / 2) as i32)
}

pub fn hide<R: Runtime>(window: &WebviewWindow<R>) -> tauri::Result<()> {
    window.hide()?;
    crate::launcher_outside_click::stop(window.app_handle());
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn panel_stays_centered_and_leaves_desktop_access_at_mixed_dpi() {
        for (width, height, scale) in [(1920, 1040, 1.0), (2560, 1400, 1.5), (1080, 700, 2.0)] {
            let (w, h, x, y) = panel_bounds(width, height, scale);
            assert!(w < width && h < height && x > 0 && y > 0);
            assert!((x * 2 + w as i32 - width as i32).abs() <= 1);
            assert!((y * 2 + h as i32 - height as i32).abs() <= 1);
        }
    }
    #[test]
    fn external_drag_and_release_cannot_dismiss_overlay() {
        let mut guard = DropProtection::default();
        assert!(guard.can_dismiss());
        assert!(guard.enter());
        assert!(!guard.enter());
        assert!(!guard.can_dismiss());
        guard.finish();
        assert!(!guard.can_dismiss());
        assert!(guard.can_dismiss_at(Instant::now() + Duration::from_secs(1)));
        guard.enter();
        assert!(!guard.can_dismiss_at(Instant::now() + Duration::from_secs(2)));
    }
    #[test]
    fn native_leave_during_import_does_not_release_protection() {
        let mut guard = DropProtection::default();
        guard.enter();
        guard.begin_import();
        assert!(!guard.leave());
        assert!(!guard.can_dismiss_at(Instant::now() + Duration::from_secs(30)));
        guard.finish();
        assert!(guard.can_dismiss_at(Instant::now() + Duration::from_secs(1)));
    }
}
