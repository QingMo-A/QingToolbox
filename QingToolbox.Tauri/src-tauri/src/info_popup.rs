//! Screen-edge notification stack. Each card owns only its message and timer;
//! compact, overlapping native windows leave the surrounding desktop interactive.
use crate::{device_diagnostics as diag, device_pairing::ForwardedNotification, HostState};
use std::{collections::VecDeque, sync::atomic::Ordering, thread, time::Duration};
use tauri::{Emitter, EventTarget, Manager, PhysicalPosition, PhysicalSize, WebviewWindow};

pub(crate) const WINDOW_LABEL: &str = "info-popup";
const PREFIX: &str = "info-popup-";
const PENDING_LIMIT: usize = 16;
const VISIBLE_LIMIT: usize = 5;
const STACK_REVEAL: f64 = 32.0;

#[derive(Clone)]
struct Card {
    label: String,
    item: ForwardedNotification,
    exiting: bool,
    exit_x: Option<i32>,
    hovered: bool,
}

#[derive(Default)]
pub(crate) struct InfoPopupQueue {
    visible: Vec<Card>, // Oldest first, starting at the selected screen corner.
    pending: VecDeque<ForwardedNotification>,
    sequence: u64,
}

impl InfoPopupQueue {
    pub(crate) fn enqueue(&mut self, item: ForwardedNotification) -> bool {
        if self.visible.iter().any(|card| card.item.id == item.id)
            || self.pending.iter().any(|pending| pending.id == item.id)
        {
            return false;
        }
        if self.pending.len() >= PENDING_LIMIT {
            self.pending.pop_front();
        }
        self.pending.push_back(item);
        true
    }

    fn fill(&mut self, capacity: usize) -> Vec<Card> {
        while self.visible.len() < capacity.clamp(1, PENDING_LIMIT) {
            let Some(item) = self.pending.pop_front() else {
                break;
            };
            let label = if self.visible.iter().all(|card| card.label != WINDOW_LABEL) {
                WINDOW_LABEL.to_string()
            } else {
                self.sequence += 1;
                format!("{PREFIX}{}", self.sequence)
            };
            self.visible.push(Card {
                label,
                item,
                exiting: false,
                exit_x: None,
                hovered: false,
            });
        }
        self.visible.clone()
    }

    pub(crate) fn item(&self, label: &str) -> Option<ForwardedNotification> {
        self.visible
            .iter()
            .find(|card| card.label == label)
            .map(|card| card.item.clone())
    }

    pub(crate) fn begin_dismiss(&mut self, label: &str, id: &str) -> bool {
        let Some(card) = self
            .visible
            .iter_mut()
            .find(|card| card.label == label && card.item.id == id)
        else {
            return false;
        };
        if card.exiting {
            return false;
        }
        card.exiting = true;
        true
    }

    fn finish(&mut self, label: &str, id: Option<&str>) -> bool {
        let before = self.visible.len();
        self.visible
            .retain(|card| card.label != label || id.is_some_and(|id| card.item.id != id));
        self.visible.len() != before
    }

    fn moving(&self, label: &str) -> bool {
        self.visible
            .iter()
            .any(|card| card.label == label && !card.exiting)
    }

    pub(crate) fn set_hovered(&mut self, label: &str, id: &str, hovered: bool) -> bool {
        if !self
            .visible
            .iter()
            .any(|card| card.label == label && card.item.id == id && !card.exiting)
        {
            return false;
        }
        for card in &mut self.visible {
            if card.label == label {
                card.hovered = hovered;
            } else if hovered {
                card.hovered = false;
            }
        }
        true
    }

    fn z_order(&self) -> Vec<String> {
        // Newest normally covers older cards. Hovering an exposed strip raises
        // that card without moving/recreating it or restarting any timer.
        let mut cards: Vec<_> = self.visible.iter().filter(|card| !card.exiting).collect();
        cards.sort_by_key(|card| card.hovered);
        cards.into_iter().map(|card| card.label.clone()).collect()
    }

    fn set_exit_x(&mut self, label: &str, id: &str, x: i32) {
        if let Some(card) = self
            .visible
            .iter_mut()
            .find(|card| card.label == label && card.item.id == id && !card.exiting)
        {
            card.exit_x = Some(x);
        }
    }
    fn exit_x(&self, label: &str, id: &str) -> Option<i32> {
        self.visible
            .iter()
            .find(|card| card.label == label && card.item.id == id)
            .and_then(|card| card.exit_x)
    }
}

pub(crate) fn is_popup_label(label: &str) -> bool {
    label == WINDOW_LABEL
        || label.strip_prefix(PREFIX).is_some_and(|suffix| {
            !suffix.is_empty()
                && suffix.len() <= 20
                && suffix.bytes().all(|byte| byte.is_ascii_digit())
        })
}

#[derive(Clone, Copy)]
struct Layout {
    x: i32,
    anchor_y: i32,
    exit_x: i32,
    width: u32,
    height: u32,
    step: i32,
    bottom: bool,
    capacity: usize,
}

impl Layout {
    fn new(
        work_x: i32,
        work_y: i32,
        work_width: u32,
        work_height: u32,
        screen_edge: i32,
        scale: f64,
        corner: &str,
    ) -> Self {
        let scale = scale.clamp(0.5, 4.0);
        let px = |logical: f64| (logical * scale).round() as u32;
        let width = px(390.0).min(work_width.max(1));
        let height = px(150.0).min(work_height.max(1));
        let margin = px(24.0).min(work_height.saturating_sub(height) / 2);
        let step = px(STACK_REVEAL).min(height).max(1);
        let bottom = corner.ends_with("Bottom");
        let horizontal_margin = margin.min(work_width.saturating_sub(width));
        let x = if corner.starts_with("left") {
            work_x + horizontal_margin as i32
        } else {
            work_x + work_width.saturating_sub(width + horizontal_margin) as i32
        };
        let anchor_y = if bottom {
            work_y + work_height.saturating_sub(height + margin) as i32
        } else {
            work_y + margin as i32
        };
        Self {
            x,
            anchor_y,
            width,
            height,
            step: step as i32,
            bottom,
            exit_x: if corner.starts_with("left") {
                screen_edge - width as i32
            } else {
                screen_edge
            },
            capacity: (1 + work_height.saturating_sub(2 * margin + height) / step)
                .clamp(1, VISIBLE_LIMIT as u32) as usize,
        }
    }
    fn target(&self, index: usize) -> PhysicalPosition<i32> {
        let offset = self.step * index as i32;
        PhysicalPosition::new(
            self.x,
            self.anchor_y + if self.bottom { -offset } else { offset },
        )
    }
}

fn restore_z_order(app: &tauri::AppHandle, generation: u64) {
    // Reused base windows do not have a reliable creation-time Z order. Raise
    // explicitly, without activating a notification or stealing keyboard focus.
    #[cfg(windows)]
    {
        let app = app.clone();
        let dispatcher = app.clone();
        let _ = dispatcher.run_on_main_thread(move || {
            use windows_sys::Win32::UI::WindowsAndMessaging::{
                SetWindowPos, HWND_TOPMOST, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOOWNERZORDER,
                SWP_NOSIZE,
            };
            let state = app.state::<HostState>();
            if state.info_popup_move_generation.load(Ordering::Acquire) != generation {
                return;
            }
            let Ok(labels) = state.info_popup.lock().map(|queue| queue.z_order()) else {
                return;
            };
            for label in labels {
                if let Some(window) = app.get_webview_window(&label) {
                    if let Ok(hwnd) = window.hwnd() {
                        unsafe {
                            SetWindowPos(
                                hwnd.0 as _,
                                HWND_TOPMOST,
                                0,
                                0,
                                0,
                                0,
                                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER,
                            );
                        }
                    }
                }
            }
        });
    }
    #[cfg(not(windows))]
    let _ = (app, generation);
}

pub(crate) fn refresh_z_order(app: &tauri::AppHandle) {
    let generation = app
        .state::<HostState>()
        .info_popup_move_generation
        .load(Ordering::Acquire);
    restore_z_order(app, generation);
}

fn layout(window: &WebviewWindow, corner: &str) -> Option<Layout> {
    let monitor = window
        .current_monitor()
        .ok()
        .flatten()
        .or_else(|| window.primary_monitor().ok().flatten())?;
    let work = monitor.work_area();
    let edge = if corner.starts_with("left") {
        monitor.position().x
    } else {
        monitor.position().x + monitor.size().width as i32
    };
    Some(Layout::new(
        work.position.x,
        work.position.y,
        work.size.width,
        work.size.height,
        edge,
        monitor.scale_factor(),
        corner,
    ))
}

// Detached workers match the existing Screen Pin pattern: never synchronously
// create a WebView2 window inside an invoke handler. The gate serializes builds.
pub(crate) fn present(app: tauri::AppHandle) {
    let generation = app
        .state::<HostState>()
        .info_popup_move_generation
        .fetch_add(1, Ordering::AcqRel)
        + 1;
    thread::spawn(move || {
        let state = app.state::<HostState>();
        let Ok(_gate) = state.info_popup_layout_gate.lock() else {
            return;
        };
        if state.info_popup_move_generation.load(Ordering::Acquire) != generation {
            return;
        }
        // The base card may be hidden beyond a screen edge after exiting.
        // Anchor to the host monitor, not that off-screen window's monitor.
        let Some(anchor) = app
            .get_webview_window("main")
            .or_else(|| app.get_webview_window(WINDOW_LABEL))
        else {
            return;
        };
        let Ok(settings) = state.settings.lock().map(|store| store.snapshot()) else {
            return;
        };
        let Some(layout) = layout(&anchor, &settings.info_popup_corner) else {
            return;
        };
        let Ok(cards) = state
            .info_popup
            .lock()
            .map(|mut queue| queue.fill(layout.capacity))
        else {
            return;
        };
        let mut movements = Vec::new();
        let mut failed = false;
        for (index, card) in cards.into_iter().enumerate() {
            if card.exiting {
                continue;
            }
            let target = layout.target(index);
            let window = if let Some(window) = app.get_webview_window(&card.label) {
                window
            } else {
                match tauri::WebviewWindowBuilder::new(
                    &app,
                    &card.label,
                    tauri::WebviewUrl::App("index.html?surface=info-popup".into()),
                )
                .title("QingToolbox 消息")
                .inner_size(390.0, 150.0)
                .resizable(false)
                .decorations(false)
                .transparent(true)
                .shadow(false)
                .always_on_top(true)
                .focused(false)
                .focusable(false)
                .skip_taskbar(true)
                .disable_drag_drop_handler()
                .visible(false)
                .build()
                {
                    Ok(window) => {
                        let app_for_close = app.clone();
                        let label = card.label.clone();
                        window.on_window_event(move |event| {
                            if matches!(event, tauri::WindowEvent::Destroyed) {
                                let removed = app_for_close
                                    .state::<HostState>()
                                    .info_popup
                                    .lock()
                                    .is_ok_and(|mut queue| queue.finish(&label, None));
                                if removed {
                                    present(app_for_close.clone());
                                }
                            }
                        });
                        window
                    }
                    Err(_) => {
                        diag::record(
                            diag::Level::Error,
                            diag::Event::NotificationPopupFailed,
                            diag::Reason::Unexpected,
                            None,
                            Some(&card.item.id),
                            true,
                        );
                        if let Ok(mut queue) = state.info_popup.lock() {
                            queue.finish(&card.label, Some(&card.item.id));
                        }
                        failed = true;
                        continue;
                    }
                }
            };
            if state.info_popup_move_generation.load(Ordering::Acquire) != generation {
                return;
            }
            let entering = !window.is_visible().unwrap_or(false);
            if let Ok(mut queue) = state.info_popup.lock() {
                queue.set_exit_x(&card.label, &card.item.id, layout.exit_x);
            }
            let size = PhysicalSize::new(layout.width, layout.height);
            if window.outer_size().ok() != Some(size) {
                let _ = window.set_size(size);
            }
            let start = if entering {
                PhysicalPosition::new(
                    if settings.info_popup_animation {
                        layout.exit_x
                    } else {
                        layout.x
                    },
                    target.y,
                )
            } else {
                window.outer_position().unwrap_or(target)
            };
            let _ = window.set_position(start);
            if entering {
                let shown = window.show();
                diag::record(
                    if shown.is_ok() {
                        diag::Level::Information
                    } else {
                        diag::Level::Error
                    },
                    if shown.is_ok() {
                        diag::Event::NotificationPopupShown
                    } else {
                        diag::Event::NotificationPopupFailed
                    },
                    if shown.is_ok() {
                        diag::Reason::None
                    } else {
                        diag::Reason::Unexpected
                    },
                    None,
                    Some(&card.item.id),
                    false,
                );
                if shown.is_err() {
                    if let Ok(mut queue) = state.info_popup.lock() {
                        queue.finish(&card.label, Some(&card.item.id));
                    }
                    if card.label != WINDOW_LABEL {
                        let _ = window.close();
                    }
                    failed = true;
                    continue;
                }
                let _ = app.emit_to(
                    EventTarget::webview_window(&card.label),
                    "qing:info-popup-changed",
                    (),
                );
            }
            if start != target {
                movements.push((window, start, target));
            }
        }
        // Do not serialize animation time with the next layout: a newer layout
        // starts from each window's actual position, never from its entry edge.
        drop(_gate);
        if failed {
            present(app.clone());
            return;
        }
        restore_z_order(&app, generation);
        let duration = if settings.info_popup_animation {
            settings.info_popup_duration_ms
        } else {
            0
        };
        let started = std::time::Instant::now();
        loop {
            if state.info_popup_move_generation.load(Ordering::Acquire) != generation {
                return;
            }
            let progress = if duration == 0 {
                1.0
            } else {
                (started.elapsed().as_secs_f64() * 1000.0 / f64::from(duration)).min(1.0)
            };
            let eased = 1.0 - (1.0 - progress).powi(3);
            for (window, start, target) in &movements {
                if state
                    .info_popup
                    .lock()
                    .is_ok_and(|queue| queue.moving(window.label()))
                {
                    let _ = window.set_position(PhysicalPosition::new(
                        (start.x as f64 + (target.x - start.x) as f64 * eased).round() as i32,
                        (start.y as f64 + (target.y - start.y) as f64 * eased).round() as i32,
                    ));
                }
            }
            if progress >= 1.0 {
                break;
            }
            thread::sleep(Duration::from_millis(16));
        }
    });
}

pub(crate) fn dismiss(window: WebviewWindow, id: String) {
    let app = window.app_handle().clone();
    // Stop reflow from writing to the exiting card; other cards keep their slots
    // until it is fully off-screen, then compact exactly once.
    present(app.clone());
    thread::spawn(move || {
        let state = app.state::<HostState>();
        let config = state.settings.lock().ok().map(|store| store.snapshot());
        let exit_x = state
            .info_popup
            .lock()
            .ok()
            .and_then(|queue| queue.exit_x(window.label(), &id));
        if let (Some(config), Ok(position)) = (config, window.outer_position()) {
            if config.info_popup_animation {
                if let Some(exit_x) = exit_x {
                    let started = std::time::Instant::now();
                    loop {
                        let progress = (started.elapsed().as_secs_f64() * 1000.0
                            / f64::from(config.info_popup_duration_ms))
                        .min(1.0);
                        let eased = progress.powi(3); // Reverse entry path, accelerating out.
                        let _ = window.set_position(PhysicalPosition::new(
                            (position.x as f64 + (exit_x - position.x) as f64 * eased).round()
                                as i32,
                            position.y,
                        ));
                        if progress >= 1.0 {
                            break;
                        }
                        thread::sleep(Duration::from_millis(16));
                    }
                }
            }
        }
        let _ = window.hide();
        if let Ok(mut queue) = state.info_popup.lock() {
            queue.finish(window.label(), Some(&id));
        }
        if window.label() == WINDOW_LABEL {
            let _ = app.emit_to(
                EventTarget::webview_window(WINDOW_LABEL),
                "qing:info-popup-changed",
                (),
            );
        } else {
            let _ = window.close();
        }
        present(app.clone());
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    fn item(id: &str) -> ForwardedNotification {
        ForwardedNotification {
            id: id.into(),
            device_name: "Phone".into(),
            app_name: "Messages".into(),
            title: "Title".into(),
            body: "Body".into(),
        }
    }
    #[test]
    fn new_messages_stack_without_replacing_existing_cards() {
        let mut queue = InfoPopupQueue::default();
        queue.enqueue(item("one"));
        queue.enqueue(item("two"));
        queue.enqueue(item("three"));
        let cards = queue.fill(3);
        assert_eq!(
            cards
                .iter()
                .map(|card| card.item.id.as_str())
                .collect::<Vec<_>>(),
            ["one", "two", "three"]
        );
        assert!(!queue.enqueue(item("two")));
        queue.set_exit_x(&cards[0].label, "one", 1920);
        assert!(queue.begin_dismiss(&cards[0].label, "one"));
        queue.set_exit_x(&cards[0].label, "one", -1920);
        assert_eq!(queue.exit_x(&cards[0].label, "one"), Some(1920)); // Keep the entry edge during exit.
        assert!(!queue.begin_dismiss(&cards[0].label, "one"));
        assert!(!queue.begin_dismiss(&cards[1].label, "three"));
        assert_eq!(queue.fill(3).len(), 3); // Exiting card still owns its space.
        queue.finish(&cards[0].label, Some("one"));
        let remaining = queue.fill(3);
        assert_eq!(remaining[0].item.id, "two");
        assert_eq!(remaining[1].item.id, "three");
        assert_eq!(remaining[0].label, cards[1].label); // Reflow does not recreate cards/timers.
    }
    #[test]
    fn middle_removal_and_screen_capacity_keep_fifo_order() {
        let mut queue = InfoPopupQueue::default();
        for id in ["one", "two", "three", "four"] {
            queue.enqueue(item(id));
        }
        let cards = queue.fill(3);
        assert_eq!(queue.pending.front().unwrap().id, "four");
        queue.begin_dismiss(&cards[1].label, "two");
        queue.finish(&cards[1].label, Some("two"));
        assert_eq!(
            queue
                .fill(3)
                .iter()
                .map(|card| card.item.id.as_str())
                .collect::<Vec<_>>(),
            ["one", "three", "four"]
        );
        assert!(queue.item("main").is_none());
    }
    #[test]
    fn positions_follow_all_corners_and_fill_gaps_at_the_anchor() {
        for corner in ["rightBottom", "rightTop", "leftBottom", "leftTop"] {
            let layout = Layout::new(
                -1920,
                20,
                1920,
                1040,
                if corner.starts_with("left") { -1920 } else { 0 },
                1.0,
                corner,
            );
            let first = layout.target(0);
            let next = layout.target(1);
            assert_eq!(next.x, first.x);
            assert_eq!(next.y - first.y, if layout.bottom { -32 } else { 32 });
            assert!(layout.step < layout.height as i32);
            assert_eq!(layout.capacity, VISIBLE_LIMIT);
            for index in 0..layout.capacity {
                let position = layout.target(index);
                assert!(position.y >= 20 && position.y + layout.height as i32 <= 1060);
            }
            assert_eq!(layout.target(0), first); // Any surviving first card takes the anchor slot.
        }
        assert_eq!(
            Layout::new(0, 0, 2560, 1400, 2560, 2.0, "rightBottom").step,
            64
        );
    }
    #[test]
    fn compact_stack_fits_small_screens_and_limits_its_footprint() {
        let layout = Layout::new(0, 0, 1920, 1080, 1920, 1.0, "rightBottom");
        assert_eq!(
            layout.height + layout.step as u32 * (layout.capacity as u32 - 1),
            278
        );
        for corner in ["rightBottom", "rightTop", "leftBottom", "leftTop"] {
            for height in [1, 150, 200, 240, 300, 1080] {
                let layout = Layout::new(0, 0, 1920, height, 1920, 1.0, corner);
                for index in 0..layout.capacity {
                    let position = layout.target(index);
                    assert!(position.y >= 0 && position.y + layout.height as i32 <= height as i32);
                }
            }
        }
    }
    #[test]
    fn newest_covers_old_cards_and_hover_temporarily_raises_only_its_owner() {
        let mut queue = InfoPopupQueue::default();
        for id in ["one", "two", "three"] {
            queue.enqueue(item(id));
        }
        let cards = queue.fill(3);
        let labels: Vec<_> = cards.iter().map(|card| card.label.clone()).collect();
        assert_eq!(queue.z_order(), labels);
        assert!(!queue.set_hovered(&labels[0], "two", true));
        assert!(queue.set_hovered(&labels[0], "one", true));
        assert_eq!(
            queue.z_order(),
            [labels[1].clone(), labels[2].clone(), labels[0].clone()]
        );
        assert!(queue.set_hovered(&labels[0], "one", false));
        assert_eq!(queue.z_order(), labels);
        queue.begin_dismiss(&labels[0], "one");
        assert!(!queue.set_hovered(&labels[0], "one", true));
        queue.finish(&labels[0], Some("one"));
        queue.enqueue(item("four"));
        let next = queue.fill(3);
        assert_eq!(next[2].label, WINDOW_LABEL);
        assert_eq!(queue.z_order().last(), Some(&next[2].label));
        assert!(!queue.set_hovered(WINDOW_LABEL, "one", true));
    }
    #[test]
    fn window_authorization_accepts_only_owned_popup_label_shapes() {
        for label in [WINDOW_LABEL, "info-popup-1", "info-popup-123"] {
            assert!(is_popup_label(label));
        }
        for label in [
            "main",
            "module-launcher",
            "info-popup-",
            "info-popup-one",
            "info-popup-1/other",
        ] {
            assert!(!is_popup_label(label));
        }
    }
}
