//! The Windows island: a small, borderless, click-through-where-empty overlay.
//!
//! ## Why this is not a Tauri window
//!
//! The module is a separate process from the host, so it cannot create a host
//! window, and it must not depend on the host's WebView being alive. It also
//! must not become a second Tauri application just to draw one pill. So the
//! window is created directly with Win32, and the island's content is drawn by
//! the module itself.
//!
//! ## The two properties that matter most
//!
//! **It never blocks the desktop.** The window is exactly as large as the
//! island's current visual bounds, and `WM_NCHITTEST` reports
//! `HTTRANSPARENT` for any pixel that is not part of an interactive element.
//! A monitor-wide transparent strip would be simpler and is explicitly
//! rejected: it would swallow clicks across the top of every application.
//!
//! **It never steals focus.** `WS_EX_NOACTIVATE` plus `SW_SHOWNOACTIVATE`, and
//! a `WM_MOUSEACTIVATE` handler that returns `MA_NOACTIVATE`. Clicking the
//! island must not pull the user out of whatever they were typing in.
//!
//! ## State machine
//!
//! `Dormant` → `Compact` → `Peek` → `Expanded`, as a single enum. There is one
//! authoritative state value and one transition function, rather than a set of
//! booleans that can disagree with each other.

use std::time::Duration;

use crate::activity::LiveActivity;
use crate::display::MonitorMetrics;

/// The visual state of the island. One value, one transition, no booleans.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum IslandState {
    /// Nothing to show. The island hides itself entirely rather than showing an
    /// empty pill, because an always-present decoration with no content is
    /// noise on a desktop the user did not ask to decorate.
    Dormant,
    /// The collapsed pill: a dot and one short line.
    Compact,
    /// Hover: adds one row of context.
    Peek,
    /// Click: the full stack of activities plus the account row.
    Expanded,
}

impl IslandState {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Dormant => "dormant",
            Self::Compact => "compact",
            Self::Peek => "peek",
            Self::Expanded => "expanded",
        }
    }

    /// Logical size of the island in this state: `(width, height)`.
    ///
    /// The sizes are chosen so the pill reads as one object across the
    /// transition: only the height and the content change, and the width of the
    /// compact and peek states are close enough that the expansion does not
    /// look like a different window appearing.
    pub fn logical_size(self) -> (f64, f64) {
        match self {
            Self::Dormant => (0.0, 0.0),
            Self::Compact => (232.0, 32.0),
            Self::Peek => (300.0, 60.0),
            Self::Expanded => (340.0, 260.0),
        }
    }

    /// Whether the pointer can interact with the island at all.
    ///
    /// `Dormant` is fully click-through; the others capture only their drawn
    /// pixels, which `hit_test` decides per point.
    pub fn is_interactive(self) -> bool {
        !matches!(self, Self::Dormant)
    }
}

/// What the island is currently doing, independent of any window handle.
///
/// Keeping this separate from the window is what makes the state machine
/// testable: the transition rules below run in a plain unit test, with no
/// window, message loop or GDI in sight.
#[derive(Debug, Clone, PartialEq)]
pub struct IslandModel {
    state: IslandState,
    /// The activity the compact pill is about.
    focus: Option<LiveActivity>,
    /// Every activity, ordered, for the expanded stack.
    stack: Vec<LiveActivity>,
    overflow: usize,
    /// Account-level line, shown in peek and expanded.
    account: Option<String>,
    /// Compact quota beside the clock, independent of running tasks.
    account_header: Option<String>,
    ambient: Option<crate::ambient::AmbientContent>,
    /// Whether the pointer is over the island.
    hovered: bool,
    /// Whether the pointer is over an interactive element inside the island.
    over_control: bool,
    /// Set while the pointer is held down on an interactive element.
    pressed: bool,
    /// True while a fullscreen application owns the screen and the policy says
    /// to stay out of the way.
    suppressed: bool,
}

impl Default for IslandModel {
    fn default() -> Self {
        Self {
            state: IslandState::Dormant,
            focus: None,
            stack: Vec::new(),
            overflow: 0,
            account: None,
            account_header: None,
            ambient: None,
            hovered: false,
            over_control: false,
            pressed: false,
            suppressed: false,
        }
    }
}

impl IslandModel {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn state(&self) -> IslandState {
        self.state
    }

    pub fn stack(&self) -> &[LiveActivity] {
        &self.stack
    }

    pub fn focus(&self) -> Option<&LiveActivity> {
        self.focus.as_ref()
    }
    pub fn account(&self) -> Option<&str> {
        self.account.as_deref()
    }
    pub fn account_header(&self) -> Option<&str> {
        self.account_header.as_deref()
    }
    pub fn set_account_header(&mut self, header: Option<String>) {
        self.account_header = header;
    }
    pub fn ambient(&self) -> Option<&crate::ambient::AmbientContent> {
        self.ambient.as_ref()
    }
    pub fn set_ambient(&mut self, content: Option<crate::ambient::AmbientContent>) {
        self.ambient = content;
        self.refresh_state();
    }

    pub fn overflow(&self) -> usize {
        self.overflow
    }

    /// Replace the content and recompute the state.
    ///
    /// The state is *derived* here rather than set from outside, so content and
    /// presentation cannot drift apart: an empty broker always yields
    /// `Dormant`, no matter what the pointer is doing.
    pub fn set_content(
        &mut self,
        focus: Option<LiveActivity>,
        stack: Vec<LiveActivity>,
        overflow: usize,
        account: Option<String>,
    ) {
        self.focus = focus;
        self.stack = stack;
        self.overflow = overflow;
        self.account = account;
        if self.account.is_none() {
            self.account_header = None;
        }
        self.refresh_state();
    }

    pub fn set_suppressed(&mut self, suppressed: bool) {
        if self.suppressed != suppressed {
            self.suppressed = suppressed;
        }
        self.refresh_state();
    }

    pub fn set_hovered(&mut self, hovered: bool) {
        if self.hovered == hovered {
            return;
        }
        self.hovered = hovered;
        if !hovered {
            // Leaving always drops the transient states. An expanded island
            // that stayed open after the pointer left would be a window the
            // user has to dismiss, which is the opposite of ambient.
            self.over_control = false;
            self.pressed = false;
        }
        self.refresh_state();
    }

    pub fn set_over_control(&mut self, over_control: bool) {
        self.over_control = over_control;
        // Moving onto a control keeps the island expanded even if the pointer
        // drifts to a gap between controls.
        if over_control {
            self.hovered = true;
        }
        self.refresh_state();
    }

    pub fn set_pressed(&mut self, pressed: bool) {
        if self.pressed == pressed {
            return;
        }
        self.pressed = pressed;
        // A press is a state change like any other: without this the island
        // would keep showing whatever the previous pointer event decided.
        self.refresh_state();
    }

    /// The click that expands the island.
    pub fn toggle_expanded(&mut self) {
        if self.state == IslandState::Expanded {
            // Collapsing returns to peek rather than compact: the pointer is
            // still on the island, so the hover state is still true.
            self.over_control = false;
        } else if self.state == IslandState::Peek || self.state == IslandState::Compact {
            self.over_control = true;
            self.hovered = true;
        }
        self.refresh_state();
    }

    /// Recompute the authoritative state from content plus pointer.
    fn refresh_state(&mut self) {
        // No content, or suppressed by the fullscreen policy, means nothing to
        // draw regardless of where the pointer is.
        if self.suppressed
            || (self.focus.is_none() && self.stack.is_empty() && self.ambient.is_none())
        {
            self.state = IslandState::Dormant;
            return;
        }
        self.state = if self.over_control || self.pressed {
            IslandState::Expanded
        } else if self.hovered {
            IslandState::Peek
        } else {
            IslandState::Compact
        };
    }

    /// The pill's text, kept short enough for a 232 logical-pixel window.
    ///
    /// Never includes a thread id, a token count or a model name: those belong
    /// in the peek row, which the user opts into by hovering.
    pub fn compact_label(&self) -> Option<String> {
        let Some(focus) = self.focus.as_ref() else {
            return self
                .ambient
                .as_ref()
                .map(|content| content.label().to_owned());
        };
        Some(format!(
            "{} · {}",
            provider_label(focus),
            short_state(focus)
        ))
    }

    /// The one extra row shown while hovering.
    pub fn peek_detail(&self) -> Option<String> {
        let Some(focus) = self.focus.as_ref() else {
            return self
                .ambient
                .as_ref()
                .map(|content| content.detail().to_owned());
        };
        // Prefer an explicit progress figure, then a provider-supplied detail,
        // then the account line. Each is real data; none is invented.
        if let Some(fraction) = focus
            .progress
            .as_ref()
            .and_then(|progress| progress.fraction())
        {
            return Some(format!(
                "{} {}%",
                short_state(focus),
                (fraction * 100.0).round()
            ));
        }
        for key in ["usage", "stage", "limits"] {
            if let Some(value) = focus.details.get(key) {
                return Some(value.clone());
            }
        }
        self.account.clone().or_else(|| focus.subtitle.clone())
    }
}

/// The short provider name shown in the pill.
///
/// "Codex" for the Codex provider; the mock is labelled so a developer can see
/// at a glance that they are looking at synthetic data.
fn provider_label(activity: &LiveActivity) -> &'static str {
    use crate::activity::ProviderKind;
    match activity.provider {
        ProviderKind::Codex => "Codex",
        ProviderKind::Mock => "Mock",
        ProviderKind::Media => "Media",
        ProviderKind::Transfer => "Files",
    }
}

/// A verb, not a noun, so the pill reads as a status rather than a title.
fn short_state(activity: &LiveActivity) -> String {
    use crate::activity::ActivityState;
    match activity.state {
        ActivityState::Running => activity
            .subtitle
            .clone()
            .unwrap_or_else(|| "Working".to_string()),
        ActivityState::Waiting => "Needs you".to_string(),
        ActivityState::Paused => "Paused".to_string(),
        ActivityState::Success => "Done".to_string(),
        ActivityState::Failed => "Failed".to_string(),
        ActivityState::Idle => "Idle".to_string(),
        ActivityState::Cancelled => "Cancelled".to_string(),
        ActivityState::Unknown => "Unknown".to_string(),
    }
}

/// The pixel rectangle the island occupies.
///
/// `Default` is an empty rectangle at the origin, which is the correct starting
/// point: the overlay holds one of these before the first layout runs, and an
/// empty box means "nothing to hit" until a real one arrives.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Bounds {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}

impl Bounds {
    pub fn is_empty(&self) -> bool {
        self.width <= 0 || self.height <= 0
    }

    pub fn contains(&self, x: i32, y: i32) -> bool {
        x >= self.x && y >= self.y && x < self.x + self.width && y < self.y + self.height
    }

    /// Convert to the Win32 `(x, y, width, height)` tuple order.
    pub fn as_win32(&self) -> (i32, i32, i32, i32) {
        (self.x, self.y, self.width, self.height)
    }
}

/// Geometry for one island state on one monitor.
///
/// Pure: given a state and a monitor it produces a rectangle. The window code
/// only applies the result. That separation is what lets placement be verified
/// across DPIs and monitors in a unit test.
pub fn bounds_for(
    state: IslandState,
    monitor: &MonitorMetrics,
    anchor_grows_downward: bool,
    user_scale: f64,
    margin_logical: f64,
) -> Bounds {
    let (logical_width, logical_height) = state.logical_size();
    if logical_width <= 0.0 || logical_height <= 0.0 {
        return Bounds {
            x: monitor.center_x(0),
            y: monitor.y,
            width: 0,
            height: 0,
        };
    }
    // User scale multiplies logical size before DPI conversion, so a user who
    // wants a larger island gets it on every monitor rather than only on the
    // one where the raw pixel count happens to look right.
    let scale = user_scale.clamp(0.75, 1.5);
    let margin = monitor.to_physical(margin_logical).max(0);
    // Clamp to the work area before centring. Without this a large user scale
    // on a small monitor produces a window that runs off both edges, and the
    // centre calculation would then place its middle off-screen.
    let width = monitor
        .to_physical(logical_width * scale)
        .clamp(1, (monitor.width as i32 - margin * 2).max(1))
        .max(1);
    let height = monitor
        .to_physical(logical_height * scale)
        .clamp(1, (monitor.height as i32 - margin * 2).max(1))
        .max(1);
    Bounds {
        x: monitor.center_x(width),
        y: monitor.anchor_y(height, margin, anchor_grows_downward),
        width,
        height,
    }
}

/// Settings-aware geometry. The same placement function is used during
/// animated resizing, so non-centred anchors never snap back to the centre.
pub fn configured_bounds(
    state: IslandState,
    monitor: &MonitorMetrics,
    settings: &crate::settings::Settings,
    margin_logical: f64,
) -> Bounds {
    let mut bounds = bounds_for(
        state,
        monitor,
        settings.anchor.grows_downward(),
        settings.scale,
        margin_logical,
    );
    if bounds.is_empty() {
        return bounds;
    }
    let logical_width = state.logical_size().0 + settings.compact_width as f64 - 232.0;
    let margin = monitor.to_physical(margin_logical).max(0);
    bounds.width = monitor
        .to_physical(logical_width * settings.scale.clamp(0.75, 1.5))
        .clamp(1, (monitor.width as i32 - margin * 2).max(1));
    position_bounds(bounds, monitor, settings, margin_logical)
}

pub fn position_bounds(
    mut bounds: Bounds,
    monitor: &MonitorMetrics,
    settings: &crate::settings::Settings,
    margin_logical: f64,
) -> Bounds {
    let margin = monitor.to_physical(margin_logical).max(0);
    let horizontal_room = (monitor.width as i32 - bounds.width - margin * 2).max(0);
    let x = monitor
        .x
        .saturating_add(margin)
        .saturating_add(
            (horizontal_room as f64 * settings.anchor.horizontal_fraction()).round() as i32,
        )
        .saturating_add(monitor.to_physical(settings.offset_x as f64));
    let y = monitor
        .anchor_y(bounds.height, margin, settings.anchor.grows_downward())
        .saturating_add(monitor.to_physical(settings.offset_y as f64));
    bounds.x = x.clamp(
        monitor.x,
        monitor
            .x
            .saturating_add((monitor.width as i32 - bounds.width).max(0)),
    );
    bounds.y = y.clamp(
        monitor.y,
        monitor
            .y
            .saturating_add((monitor.height as i32 - bounds.height).max(0)),
    );
    bounds
}

/// A connected quota adds one compact header row without widening the user's
/// chosen capsule or changing the full panel height.
pub fn model_bounds(
    model: &IslandModel,
    monitor: &MonitorMetrics,
    settings: &crate::settings::Settings,
    margin_logical: f64,
) -> Bounds {
    let mut bounds = configured_bounds(model.state(), monitor, settings, margin_logical);
    if model.account_header().is_some()
        && matches!(model.state(), IslandState::Compact | IslandState::Peek)
    {
        let height = model.state().logical_size().1 + 24.0;
        let margin = monitor.to_physical(margin_logical).max(0);
        bounds.height = monitor
            .to_physical(height * settings.scale.clamp(0.75, 1.5))
            .clamp(1, (monitor.height as i32 - margin * 2).max(1));
        bounds = position_bounds(bounds, monitor, settings, margin_logical);
    }
    bounds
}

/// Where a point is relative to the island's interactive regions.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Hit {
    /// Part of a control: the window captures the mouse here.
    Interactive,
    /// Inside the window but visually empty: the click passes through to
    /// whatever is underneath.
    PassThrough,
}

/// Decide whether a point in window coordinates hits a control.
///
/// `controls` are in window-relative logical units, which the settings page and
/// the layout code share. The pill's body and its controls are interactive; the
/// transparent padding around them is not, and neither is the gap between two
/// stacked cards.
pub fn hit_test(
    state: IslandState,
    point: (i32, i32),
    window_size: (i32, i32),
    scale: f64,
    controls: &[Bounds],
) -> Hit {
    if !state.is_interactive() {
        return Hit::PassThrough;
    }
    let (x, y) = point;
    if x < 0 || y < 0 || x >= window_size.0 || y >= window_size.1 {
        return Hit::PassThrough;
    }
    let hit_scale = if scale > 0.0 { scale } else { 1.0 };
    for control in controls {
        // Controls are declared in logical units; convert once, rounding
        // outward so a rounded edge never creates a one-pixel dead zone.
        let left = (control.x as f64 * hit_scale).floor() as i32;
        let top = (control.y as f64 * hit_scale).floor() as i32;
        let right = ((control.x + control.width) as f64 * hit_scale).ceil() as i32;
        let bottom = ((control.y + control.height) as f64 * hit_scale).ceil() as i32;
        if x >= left && x < right && y >= top && y < bottom {
            return Hit::Interactive;
        }
    }
    Hit::PassThrough
}

/// Frame budget and easing, shared by every animation.
pub mod motion {
    use super::Duration;

    /// One transition. Short enough to read as immediate, long enough not to
    /// look like a jump cut.
    pub const DURATION: Duration = Duration::from_millis(140);

    /// The animation timer's period. About one frame at 120 Hz, so a
    /// high-refresh display does not visibly step while a 60 Hz one still gets
    /// its full frame rate.
    pub const FRAME: Duration = Duration::from_millis(8);

    /// Cubic ease-out: fast start, settled finish. No overshoot, because an
    /// island that bounces reads as a toy.
    pub fn ease_out(progress: f64) -> f64 {
        let t = progress.clamp(0.0, 1.0);
        1.0 - (1.0 - t).powi(3)
    }

    /// Interpolate between two lengths with ease-out applied.
    pub fn lerp(from: f64, to: f64, elapsed: Duration) -> f64 {
        if DURATION.is_zero() {
            return to;
        }
        let progress = elapsed.as_secs_f64() / DURATION.as_secs_f64();
        let eased = ease_out(progress);
        from + (to - from) * eased
    }

    /// Whether an animation started at `elapsed` ago has finished.
    pub fn finished(elapsed: Duration) -> bool {
        elapsed >= DURATION
    }
}

/// A running animation, so the frame loop can stop when nothing is moving.
///
/// The explicit `active` flag exists because the alternative — a timer that
/// always runs — keeps the process (and the GPU) awake for a static pixel.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Transition {
    from_width: f64,
    from_height: f64,
    to_width: f64,
    to_height: f64,
    elapsed: Duration,
    active: bool,
}

impl Transition {
    /// Begin a transition. Identical endpoints complete immediately, which is
    /// the common case and must not schedule any frames at all.
    pub fn begin(from: (f64, f64), to: (f64, f64)) -> Self {
        let active = (from.0 - to.0).abs() > 0.5 || (from.1 - to.1).abs() > 0.5;
        Self {
            from_width: from.0,
            from_height: from.1,
            to_width: to.0,
            to_height: to.1,
            elapsed: Duration::ZERO,
            active,
        }
    }

    pub fn is_active(&self) -> bool {
        self.active
    }

    /// Advance by one frame and return the size to apply.
    pub fn advance(&mut self, delta: Duration) -> (f64, f64) {
        if !self.active {
            return (self.to_width, self.to_height);
        }
        self.elapsed = self.elapsed.saturating_add(delta);
        let width = motion::lerp(self.from_width, self.to_width, self.elapsed);
        let height = motion::lerp(self.from_height, self.to_height, self.elapsed);
        if motion::finished(self.elapsed) {
            self.active = false;
            return (self.to_width, self.to_height);
        }
        (width, height)
    }

    /// Snap to the end, used when a transition is superseded.
    pub fn settle(&mut self) -> (f64, f64) {
        self.active = false;
        self.elapsed = motion::DURATION;
        (self.to_width, self.to_height)
    }
}

/// Whether the module should create a window at all right now.
///
/// Extracted from the Win32 code so the "should anything exist" question is
/// testable. The answer is no while dormant, which means the module owns no
/// window while there is nothing to show.
pub fn should_create_window(state: IslandState) -> bool {
    state.is_interactive()
}

/// Whether the window must be resized for a state change.
///
/// Resizing is the expensive operation (it reallocates the layered surface), so
/// it is skipped when the bounds are unchanged.
pub fn needs_resize(current: IslandState, next: IslandState) -> bool {
    !current.is_interactive() || current.logical_size() != next.logical_size()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::activity::{ActivityProgress, ActivityState, ProviderKind};

    fn monitor(width: u32, height: u32, scale: f64) -> MonitorMetrics {
        MonitorMetrics {
            x: 0,
            y: 0,
            width,
            height,
            scale,
            primary: true,
        }
    }

    #[test]
    fn configured_anchors_resize_offsets_and_dpi_stay_in_work_area() {
        use crate::settings::{Anchor, Settings};
        let area = MonitorMetrics {
            x: -2560,
            y: -200,
            ..monitor(2560, 1400, 1.5)
        };
        for anchor in [
            Anchor::TopLeft,
            Anchor::TopCenter,
            Anchor::TopRight,
            Anchor::BottomLeft,
            Anchor::BottomCenter,
            Anchor::BottomRight,
        ] {
            let settings = Settings {
                anchor,
                compact_width: 320,
                ..Settings::default()
            };
            let compact = configured_bounds(IslandState::Compact, &area, &settings, 12.0);
            let expanded = configured_bounds(IslandState::Expanded, &area, &settings, 12.0);
            assert_eq!(compact.width, 480);
            assert_eq!(expanded.width, 642);
            if anchor.grows_downward() {
                assert_eq!(compact.y, expanded.y);
            } else {
                assert_eq!(compact.y + compact.height, expanded.y + expanded.height);
            }
            if anchor.horizontal_fraction() == 0.0 {
                assert_eq!(compact.x, expanded.x);
            }
            if anchor.horizontal_fraction() == 1.0 {
                assert_eq!(compact.x + compact.width, expanded.x + expanded.width);
            }
            // Animated widths use exactly the same anchoring as the final frame.
            assert_eq!(position_bounds(expanded, &area, &settings, 12.0), expanded);
            for (offset_x, offset_y) in [(-4096, -4096), (4096, 4096), (40, -32)] {
                let moved = configured_bounds(
                    IslandState::Expanded,
                    &area,
                    &Settings {
                        offset_x,
                        offset_y,
                        ..settings.clone()
                    },
                    12.0,
                );
                assert!(moved.x >= area.x && moved.y >= area.y);
                assert!(moved.x + moved.width <= area.x + area.width as i32);
                assert!(moved.y + moved.height <= area.y + area.height as i32);
            }
        }
        // A pathological work area must not panic in clamp(min, max).
        let tiny = configured_bounds(
            IslandState::Compact,
            &monitor(10, 10, 1.0),
            &Settings::default(),
            12.0,
        );
        assert!(tiny.width > 0 && tiny.x + tiny.width <= 10);
    }

    #[test]
    fn default_configured_geometry_keeps_existing_profiles_in_place() {
        let area = monitor(1920, 1040, 1.0);
        let settings = crate::settings::Settings::default();
        for state in [
            IslandState::Compact,
            IslandState::Peek,
            IslandState::Expanded,
        ] {
            assert_eq!(
                configured_bounds(state, &area, &settings, 12.0),
                bounds_for(state, &area, true, 1.0, 12.0)
            );
        }
    }

    #[test]
    fn clock_quota_row_resizes_only_compact_and_peek_without_moving_docked_edges() {
        use crate::settings::{Anchor, Settings};
        let mut model = IslandModel::new();
        model.set_ambient(Some(crate::ambient::AmbientContent {
            clock: Some("12:34".into()),
            date: "2026-10-07".into(),
            text: String::new(),
        }));
        for dpi in [1.0, 1.5, 2.0] {
            for scale in [0.75, 1.0, 1.5] {
                for anchor in [Anchor::TopLeft, Anchor::TopCenter, Anchor::BottomRight] {
                    let settings = Settings {
                        anchor,
                        scale,
                        ..Settings::default()
                    };
                    let area = MonitorMetrics {
                        x: -1920,
                        y: -100,
                        ..monitor(1920, 1080, dpi)
                    };
                    model.set_hovered(false);
                    model.set_content(None, Vec::new(), 0, Some("Codex · 每周剩余 97%".into()));
                    let plain = model_bounds(&model, &area, &settings, 12.0);
                    model.set_account_header(Some("每周剩余 97% · 6天后重置".into()));
                    let compact = model_bounds(&model, &area, &settings, 12.0);
                    assert_eq!(compact.height, area.to_physical(56.0 * scale));
                    assert_eq!(compact.width, plain.width);
                    if anchor.grows_downward() {
                        assert_eq!(compact.y, plain.y);
                    } else {
                        assert_eq!(compact.y + compact.height, plain.y + plain.height);
                    }
                    model.set_hovered(true);
                    assert_eq!(
                        model_bounds(&model, &area, &settings, 12.0).height,
                        area.to_physical(84.0 * scale)
                    );
                    model.toggle_expanded();
                    assert_eq!(
                        model_bounds(&model, &area, &settings, 12.0).height,
                        area.to_physical(260.0 * scale)
                    );
                    model.set_hovered(false);
                    model.set_content(None, Vec::new(), 0, None);
                    assert!(model.account_header().is_none());
                    assert_eq!(model_bounds(&model, &area, &settings, 12.0), plain);
                }
            }
        }
    }

    fn activity(id: &str, state: ActivityState) -> LiveActivity {
        LiveActivity::running(id, ProviderKind::Mock, "demo", "Demo").with_state(state)
    }

    fn seeded() -> IslandModel {
        let mut model = IslandModel::new();
        model.set_content(
            Some(activity("a", ActivityState::Running)),
            vec![activity("a", ActivityState::Running)],
            0,
            None,
        );
        model
    }

    // ---- state machine ----

    #[test]
    fn island_starts_dormant_and_only_appears_with_content() {
        let model = IslandModel::new();
        assert_eq!(model.state(), IslandState::Dormant);
        assert!(!should_create_window(model.state()));

        let mut model = IslandModel::new();
        model.set_content(None, Vec::new(), 0, None);
        assert_eq!(
            model.state(),
            IslandState::Dormant,
            "an empty broker must never produce a visible pill"
        );
    }

    #[test]
    fn content_moves_the_island_from_dormant_to_compact() {
        let model = seeded();
        assert_eq!(model.state(), IslandState::Compact);
        assert!(should_create_window(model.state()));
    }

    #[test]
    fn hovering_peeks_and_clicking_expands() {
        let mut model = seeded();
        model.set_hovered(true);
        assert_eq!(model.state(), IslandState::Peek);
        model.set_over_control(true);
        assert_eq!(model.state(), IslandState::Expanded);
    }

    #[test]
    fn leaving_the_island_collapses_back_to_compact() {
        let mut model = seeded();
        model.set_hovered(true);
        model.set_over_control(true);
        assert_eq!(model.state(), IslandState::Expanded);

        model.set_hovered(false);
        assert_eq!(
            model.state(),
            IslandState::Compact,
            "an expanded island must not require a dismissal"
        );
    }

    #[test]
    fn toggle_expanded_round_trips_without_leaving_the_pointer_state_broken() {
        let mut model = seeded();
        model.set_hovered(true);
        model.set_over_control(true);
        assert_eq!(model.state(), IslandState::Expanded);

        model.toggle_expanded();
        // Collapsing while still hovered lands on Peek, not Compact.
        assert_eq!(model.state(), IslandState::Peek);

        model.toggle_expanded();
        assert_eq!(model.state(), IslandState::Expanded);
    }

    #[test]
    fn content_disappearing_dormantifies_an_expanded_island() {
        let mut model = seeded();
        model.set_hovered(true);
        model.set_over_control(true);
        assert_eq!(model.state(), IslandState::Expanded);

        model.set_content(None, Vec::new(), 0, None);
        assert_eq!(
            model.state(),
            IslandState::Dormant,
            "the last activity finishing must hide the island even mid-interaction"
        );
        assert!(!should_create_window(model.state()));
    }

    #[test]
    fn the_fullscreen_policy_overrides_every_pointer_state() {
        let mut model = seeded();
        model.set_hovered(true);
        model.set_over_control(true);
        model.set_suppressed(true);
        assert_eq!(model.state(), IslandState::Dormant);

        // And releasing it restores the state implied by the pointer.
        model.set_suppressed(false);
        assert_eq!(model.state(), IslandState::Expanded);
    }

    #[test]
    fn pointer_state_is_cleared_when_the_pointer_leaves() {
        let mut model = seeded();
        model.set_hovered(true);
        model.set_over_control(true);
        model.set_pressed(true);
        model.set_hovered(false);
        model.set_over_control(false);
        assert_eq!(model.state(), IslandState::Compact);

        // Re-entering must not resume a stale expanded state.
        model.set_hovered(true);
        assert_eq!(model.state(), IslandState::Peek);
    }

    #[test]
    fn a_press_alone_keeps_the_island_expanded() {
        let mut model = seeded();
        model.set_hovered(true);
        model.set_pressed(true);
        assert_eq!(model.state(), IslandState::Expanded);
    }

    // ---- content projection ----

    #[test]
    fn the_compact_label_names_the_provider_and_a_verb() {
        let model = seeded();
        let label = model.compact_label().expect("label");
        assert!(label.starts_with("Mock"), "the pill must name the source");
        assert!(label.contains("Working"));
        assert!(
            label.len() <= 40,
            "the pill has room for a very short string"
        );
    }

    #[test]
    fn the_compact_label_never_leaks_identifiers_or_counters() {
        let mut activity = activity("thread-abc123", ActivityState::Running);
        activity
            .details
            .insert("thread".into(), "thread-abc123".into());
        activity
            .details
            .insert("usage".into(), "Context 72% · 184M".into());
        let mut model = IslandModel::new();
        model.set_content(Some(activity.clone()), vec![activity], 0, None);

        let label = model.compact_label().expect("label");
        for leaked in ["abc123", "72", "184M", "0x"] {
            assert!(
                !label.contains(leaked),
                "the collapsed pill must not show {leaked}; that belongs in peek"
            );
        }
    }

    #[test]
    fn waiting_reads_as_a_request_for_the_user() {
        let mut model = IslandModel::new();
        let item = activity("a", ActivityState::Waiting);
        model.set_content(Some(item.clone()), vec![item], 0, None);
        assert!(model.compact_label().expect("label").contains("Needs you"));
    }

    #[test]
    fn peek_prefers_real_progress_over_a_generic_line() {
        let mut item = activity("a", ActivityState::Running);
        item.progress = Some(ActivityProgress::determinate(3.0, 4.0));
        let mut model = IslandModel::new();
        model.set_content(
            Some(item),
            Vec::new(),
            0,
            Some("Weekly usage 76%".to_string()),
        );
        let detail = model.peek_detail().expect("detail");
        assert!(
            detail.contains("75%"),
            "a measurable bar wins: got {detail}"
        );
    }

    #[test]
    fn peek_falls_back_to_provider_details_then_the_account_row() {
        let mut item = activity("a", ActivityState::Running);
        item.details
            .insert("usage".into(), "Context 72% · 184M".into());
        let mut model = IslandModel::new();
        model.set_content(
            Some(item),
            Vec::new(),
            0,
            Some("Weekly usage 76%".to_string()),
        );
        assert_eq!(model.peek_detail().as_deref(), Some("Context 72% · 184M"));

        let plain = activity("b", ActivityState::Running);
        model.set_content(
            Some(plain),
            Vec::new(),
            0,
            Some("Weekly usage 76%".to_string()),
        );
        assert_eq!(model.peek_detail().as_deref(), Some("Weekly usage 76%"));
    }

    #[test]
    fn peek_with_nothing_known_says_nothing() {
        let mut item = activity("a", ActivityState::Running);
        item.subtitle = None;
        let mut model = IslandModel::new();
        model.set_content(Some(item), Vec::new(), 0, None);
        assert_eq!(model.peek_detail(), None, "no fabricated filler line");
    }

    #[test]
    fn the_expanded_stack_carries_overflow_and_every_activity() {
        let mut model = IslandModel::new();
        let stack = (0..7)
            .map(|index| activity(&format!("a{index}"), ActivityState::Running))
            .collect::<Vec<_>>();
        model.set_content(stack.first().cloned(), stack.clone(), 4, None);
        assert_eq!(model.stack().len(), 7);
        assert_eq!(
            model.overflow(),
            4,
            "the +N count must be available to the UI"
        );
    }

    // ---- geometry ----

    #[test]
    fn the_island_is_centered_and_anchored_to_the_working_area() {
        let area = monitor(1920, 1040, 1.0);
        let top = bounds_for(IslandState::Compact, &area, true, 1.0, 12.0);
        assert_eq!(top.width, 232);
        assert_eq!(top.height, 32);
        assert_eq!(top.y, 12);
        assert_eq!(top.x, (1920 - 232) / 2);

        let bottom = bounds_for(IslandState::Compact, &area, false, 1.0, 12.0);
        assert_eq!(bottom.y, 1040 - 32 - 12);
    }

    #[test]
    fn expansion_grows_away_from_the_docked_edge() {
        let area = monitor(1920, 1040, 1.0);
        let compact = bounds_for(IslandState::Compact, &area, true, 1.0, 12.0);
        let expanded = bounds_for(IslandState::Expanded, &area, true, 1.0, 12.0);
        assert_eq!(
            expanded.y, compact.y,
            "docked to the top, the top edge must not move"
        );
        assert!(expanded.height > compact.height);
        assert!(expanded.x < compact.x, "a wider island grows symmetrically");

        let compact = bounds_for(IslandState::Compact, &area, false, 1.0, 12.0);
        let expanded = bounds_for(IslandState::Expanded, &area, false, 1.0, 12.0);
        assert_eq!(
            expanded.y + expanded.height,
            compact.y + compact.height,
            "docked to the bottom, the bottom edge must not move"
        );
    }

    #[test]
    fn dormant_occupies_no_pixels_at_all() {
        let area = monitor(1920, 1040, 1.0);
        let bounds = bounds_for(IslandState::Dormant, &area, true, 1.0, 12.0);
        assert!(bounds.is_empty());
    }

    #[test]
    fn dpi_scaling_keeps_the_island_proportionally_identical() {
        let standard = bounds_for(
            IslandState::Compact,
            &monitor(1920, 1040, 1.0),
            true,
            1.0,
            12.0,
        );
        let hidpi = bounds_for(
            IslandState::Compact,
            &monitor(3840, 2080, 2.0),
            true,
            1.0,
            12.0,
        );
        assert_eq!(hidpi.width, standard.width * 2);
        assert_eq!(hidpi.height, standard.height * 2);
        // And it still lands fully on screen.
        assert!(hidpi.x >= 0 && hidpi.x + hidpi.width <= 3840);
        assert!(hidpi.y + hidpi.height <= 2080);
    }

    #[test]
    fn user_scale_changes_size_without_breaking_placement() {
        let area = monitor(1920, 1040, 1.0);
        let small = bounds_for(IslandState::Compact, &area, true, 0.75, 12.0);
        let large = bounds_for(IslandState::Compact, &area, true, 1.5, 12.0);
        assert!(large.width > small.width);
        assert_eq!(
            large.y, 12,
            "the margin is not scaled by the user preference"
        );
        assert_eq!(large.x, (1920 - large.width) / 2);
        // An absurd value is clamped rather than producing a giant window.
        let absurd = bounds_for(IslandState::Compact, &area, true, 99.0, 12.0);
        assert_eq!(absurd.width, large.width);
    }

    #[test]
    fn the_island_is_never_larger_than_its_monitor() {
        // A small monitor or a large user scale must still produce a window that
        // fits, rather than one that runs off the edge.
        for (width, height, scale, user) in [
            (1024u32, 600u32, 1.0, 1.5),
            (800, 600, 2.0, 1.5),
            (3840, 2160, 1.0, 1.5),
        ] {
            let area = monitor(width, height, scale);
            for state in [
                IslandState::Compact,
                IslandState::Peek,
                IslandState::Expanded,
            ] {
                let bounds = bounds_for(state, &area, true, user, 12.0);
                assert!(
                    bounds.width <= area.width as i32,
                    "{state:?} at scale {scale} on {width}x{height} overflowed"
                );
            }
        }
    }

    #[test]
    fn a_monitor_with_a_negative_origin_is_handled() {
        let left = MonitorMetrics {
            x: -1920,
            y: 0,
            width: 1920,
            height: 1040,
            scale: 1.0,
            primary: false,
        };
        let bounds = bounds_for(IslandState::Compact, &left, true, 1.0, 12.0);
        assert!(bounds.x >= -1920);
        assert!(bounds.x + bounds.width <= 0);
    }

    // ---- hit testing ----

    #[test]
    fn a_dormant_island_is_fully_click_through() {
        let hit = hit_test(
            IslandState::Dormant,
            (10, 10),
            (100, 30),
            1.0,
            &[Bounds {
                x: 0,
                y: 0,
                width: 100,
                height: 30,
            }],
        );
        assert_eq!(hit, Hit::PassThrough);
    }

    #[test]
    fn only_controls_capture_the_mouse_not_the_whole_window() {
        let window = (340, 260);
        let controls = [
            // The pill's body.
            Bounds {
                x: 0,
                y: 0,
                width: 340,
                height: 44,
            },
            // A button inside the expanded list.
            Bounds {
                x: 12,
                y: 200,
                width: 80,
                height: 28,
            },
        ];

        assert_eq!(
            hit_test(IslandState::Expanded, (170, 20), window, 1.0, &controls),
            Hit::Interactive
        );
        assert_eq!(
            hit_test(IslandState::Expanded, (40, 210), window, 1.0, &controls),
            Hit::Interactive,
            "a control inside the island must be clickable"
        );
        // The empty area between the pill and the button passes through, which
        // is the property that keeps the desktop usable underneath.
        assert_eq!(
            hit_test(IslandState::Expanded, (170, 140), window, 1.0, &controls),
            Hit::PassThrough
        );
    }

    #[test]
    fn hit_testing_respects_dpi_scaling() {
        let controls = [Bounds {
            x: 0,
            y: 0,
            width: 100,
            height: 40,
        }];
        let window = (200, 80);
        // At 2x the control covers the first 200x80 physical pixels.
        assert_eq!(
            hit_test(IslandState::Compact, (150, 60), window, 2.0, &controls),
            Hit::Interactive
        );
        assert_eq!(
            hit_test(IslandState::Compact, (150, 60), window, 1.0, &controls),
            Hit::PassThrough
        );
    }

    #[test]
    fn points_outside_the_window_never_capture() {
        let controls = [Bounds {
            x: 0,
            y: 0,
            width: 100,
            height: 40,
        }];
        for point in [(-1, 10), (10, -1), (1000, 10), (10, 1000)] {
            assert_eq!(
                hit_test(IslandState::Expanded, point, (100, 40), 1.0, &controls),
                Hit::PassThrough,
                "{point:?} is outside the window"
            );
        }
    }

    #[test]
    fn hit_testing_survives_a_degenerate_scale() {
        let controls = [Bounds {
            x: 0,
            y: 0,
            width: 10,
            height: 10,
        }];
        // A broken DPI reading must not produce a division by zero or a panic.
        let hit = hit_test(IslandState::Compact, (5, 5), (10, 10), 0.0, &controls);
        assert!(matches!(hit, Hit::Interactive | Hit::PassThrough));
    }

    #[test]
    fn an_empty_control_list_means_the_island_is_window_only() {
        assert_eq!(
            hit_test(IslandState::Expanded, (10, 10), (100, 100), 1.0, &[]),
            Hit::PassThrough
        );
    }

    // ---- motion ----

    #[test]
    fn resize_is_only_requested_when_the_size_actually_changes() {
        assert!(!needs_resize(IslandState::Compact, IslandState::Compact));
        assert!(needs_resize(IslandState::Compact, IslandState::Expanded));
        assert!(needs_resize(IslandState::Peek, IslandState::Expanded));
        assert!(
            needs_resize(IslandState::Dormant, IslandState::Compact),
            "coming back from dormant always needs a real resize"
        );
    }

    #[test]
    fn a_transition_reaches_its_target_and_exactly_then() {
        let mut transition = Transition::begin((232.0, 32.0), (340.0, 260.0));
        assert!(transition.is_active());
        let mut steps = 0;
        loop {
            let (width, height) = transition.advance(motion::FRAME);
            steps += 1;
            if !transition.is_active() {
                assert_eq!((width, height), (340.0, 260.0));
                break;
            }
            assert!(steps < 200, "a 140ms transition must not run forever");
        }
        // 140ms at 8ms per frame is about 18 frames.
        assert!(steps <= 20, "took {steps} frames");
    }

    #[test]
    fn an_identical_target_does_not_schedule_any_frames() {
        let transition = Transition::begin((232.0, 32.0), (232.0, 32.0));
        assert!(
            !transition.is_active(),
            "the common no-op case must not start a frame loop"
        );
    }

    #[test]
    fn the_frame_loop_stops_once_every_transition_has_finished() {
        // The property that keeps idle CPU at roughly zero.
        let mut active = vec![
            Transition::begin((232.0, 32.0), (340.0, 260.0)),
            Transition::begin((300.0, 60.0), (340.0, 260.0)),
        ];
        let mut frames = 0;
        while active.iter().any(Transition::is_active) {
            for transition in &mut active {
                transition.advance(motion::FRAME);
            }
            frames += 1;
            assert!(frames < 200);
        }
        assert!(active.iter().all(|transition| !transition.is_active()));
    }

    #[test]
    fn easing_is_monotonic_and_never_overshoots() {
        let mut previous = -1.0;
        for step in 0..=100 {
            let value = motion::ease_out(step as f64 / 100.0);
            assert!((0.0..=1.0).contains(&value), "ease_out produced {value}");
            assert!(value >= previous, "ease_out must not go backwards");
            previous = value;
        }
        assert_eq!(motion::ease_out(1.0), 1.0);
        assert_eq!(
            motion::ease_out(2.0),
            1.0,
            "an overrun clamps rather than overshooting"
        );
        assert_eq!(motion::ease_out(-1.0), 0.0);
    }

    #[test]
    fn settling_snaps_to_the_target_size() {
        let mut transition = Transition::begin((232.0, 32.0), (340.0, 260.0));
        transition.advance(motion::FRAME);
        let settled = transition.settle();
        assert_eq!(settled, (340.0, 260.0));
        assert!(
            !transition.is_active(),
            "a superseded transition must not keep running"
        );
    }

    #[test]
    fn transition_duration_stays_in_the_range_that_reads_as_immediate() {
        // Guards the "never bouncy, never sluggish" requirement.
        assert!(motion::DURATION >= Duration::from_millis(80));
        assert!(motion::DURATION <= Duration::from_millis(220));
    }
}
