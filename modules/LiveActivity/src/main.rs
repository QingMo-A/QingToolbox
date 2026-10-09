#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
// Some accessors exist to be asserted on by the unit tests rather than to be
// called by the shipped binary — the tests are what keep the derived state
// machine, the resolver order and the wire tokens honest. `--all-targets` sees
// those as dead code; the real build (`cargo build --release`) is warning-clean.
#![allow(dead_code)]

//! Qing Island — the Live Activity module.
//!
//! # Why this is a separate process
//!
//! The island has to outlive the toolbox window. Anything drawn inside the
//! host's WebView dies with it, so the surface is owned here and the host only
//! ever talks to this process over the standard module protocol.
//!
//! # The one rule that shapes everything else
//!
//! Data flows one way:
//!
//! ```text
//! Provider -> Broker -> PriorityResolver -> Overlay
//! ```
//!
//! A provider hands activities to the broker and never learns that a window
//! exists. The overlay reads the broker's snapshot and never asks a provider
//! anything. That is what keeps a wedged provider from freezing the island and
//! a misbehaving overlay from corrupting task state.
//!
//! # Threads
//!
//! * **main** — the host protocol loop. Blocking reads on stdin, so it must
//!   never be asked to do anything else.
//! * **tick** — drives providers, expires the broker and pushes the resulting
//!   snapshot to the overlay. Sleeps until the next thing that matters.
//! * **overlay** — owns the Win32 window and its message loop. Windows requires
//!   messages to be pumped on the thread that created the window, so this is a
//!   separate thread rather than a job for the tick loop.

mod activity;
mod ambient;
mod blur;
mod broker;
mod diagnostics;
mod display;
mod fullscreen;
mod overlay;
mod paths;
mod priority;
mod programs;
mod providers;
mod settings;
mod text_template;
mod timers;

#[cfg(windows)]
mod renderer;
#[cfg(windows)]
mod win32;

use std::{
    io::{self, BufRead, BufReader, BufWriter, Read, Write},
    sync::{
        atomic::{AtomicBool, AtomicU32, Ordering},
        mpsc, Arc, Condvar, Mutex,
    },
    time::{Duration, Instant},
};

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

use crate::activity::{ActivityState, LiveActivity, ProviderKind};
use crate::broker::ActivityBroker;
use crate::overlay::{IslandModel, IslandState};
use crate::providers::{mock::MockProvider, Provider, ProviderStatus};
use crate::settings::{Settings, SettingsPatch};

const HOST_PROTOCOL_VERSION: u16 = 1;
const HOST_MAX_FRAME_BYTES: usize = 1024 * 1024;
const MODULE_NAME: &str = "Qing Island";

/// How many activities the expanded island lists before collapsing the rest
/// into `+N`. Three fits without scrolling at the expanded size.
const VISIBLE_LIMIT: usize = 3;

/// Upper bound on how long the tick thread will sleep.
///
/// The tick thread normally sleeps according to the broker's own expiry
/// schedule, which on an idle machine is unbounded. This cap exists so that a
/// clock change, a missed wakeup or a state change arriving from another thread
/// is still noticed. One wake per second for an otherwise idle process sits
/// below the threshold where it shows up in Task Manager, and it is *not* a
/// poll: no provider is queried unless its own interval has elapsed.
const MAX_TICK_SLEEP: Duration = Duration::from_secs(1);

/// Logical inset from the work-area edge.
const EDGE_MARGIN_LOGICAL: f64 = 8.0;

// ---------------------------------------------------------------------------
// Host protocol envelopes
// ---------------------------------------------------------------------------

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct HostEnvelope {
    protocol_version: u16,
    message_type: String,
    request_id: String,
    payload: Value,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct HostResponse<'a> {
    protocol_version: u16,
    message_type: &'a str,
    request_id: &'a str,
    payload: Value,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<HostErrorBody>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct HostErrorBody {
    code: &'static str,
    message: String,
}

/// A failure that belongs to the caller, not to the process.
#[derive(Debug, Clone)]
struct ModuleError {
    code: &'static str,
    message: String,
}

impl ModuleError {
    fn new(code: &'static str, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }

    fn invalid(message: impl Into<String>) -> Self {
        Self::new("invalid_payload", message)
    }

    fn unknown_method() -> Self {
        Self::new("unknown_method", "未知的实时活动操作。")
    }
}

// ---------------------------------------------------------------------------
// Overlay commands
// ---------------------------------------------------------------------------

/// One instruction for the overlay thread.
///
/// The tick thread never touches a window: it sends one of these and moves on.
/// If the overlay is busy the message waits in a queue, which is the correct
/// behaviour — dropping a resize would leave the window the wrong size.
#[derive(Debug)]
enum OverlayCommand {
    /// Recompute geometry and visibility from this content.
    Sync {
        settings: Settings,
        focus: Option<LiveActivity>,
        stack: Vec<LiveActivity>,
        overflow: usize,
        ambient: Option<ambient::AmbientContent>,
        temporarily_hidden: bool,
        /// Set while a fullscreen application owns the foreground.
        fullscreen: bool,
    },
    /// Show a scripted preview without touching broker state.
    Preview {
        settings: Settings,
        focus: LiveActivity,
        stack: Vec<LiveActivity>,
        ambient: Option<ambient::AmbientContent>,
    },
    /// End a preview and return to whatever the broker says.
    EndPreview,
    /// Leave the message loop and destroy the window.
    Shutdown,
}

/// The channel to the overlay thread, plus the slot recording a creation
/// failure.
///
/// The sender is `Clone` and the failure slot is an `Arc`, so a handler can
/// hold a copy while the owner keeps the joinable thread. That split is what
/// lets the module send commands without owning the thread's lifetime.
#[derive(Clone)]
struct OverlayChannel {
    sender: mpsc::Sender<OverlayCommand>,
    failure: Arc<Mutex<Option<String>>>,
    thread_id: Arc<AtomicU32>,
    report: Arc<Mutex<Value>>,
}

impl OverlayChannel {
    fn send(&self, command: OverlayCommand) {
        // A closed channel means the overlay thread has already exited. That is
        // a normal shutdown race, not an error worth surfacing.
        let _ = self.sender.send(command);
        #[cfg(windows)]
        crate::win32::wake_thread(self.thread_id.load(Ordering::Acquire));
    }

    fn failure(&self) -> Option<String> {
        self.failure
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .clone()
    }
}

/// Owns the overlay thread so shutdown can join it.
struct OverlayRuntime {
    channel: OverlayChannel,
    stop: Arc<AtomicBool>,
    thread: Option<std::thread::JoinHandle<()>>,
}

impl OverlayRuntime {
    fn start() -> Self {
        let (sender, receiver) = mpsc::channel::<OverlayCommand>();
        let failure = Arc::new(Mutex::new(None));
        let stop = Arc::new(AtomicBool::new(false));
        let thread_id = Arc::new(AtomicU32::new(0));
        let report = Arc::new(Mutex::new(Value::Null));
        let thread = {
            let failure = Arc::clone(&failure);
            let stop = Arc::clone(&stop);
            let thread_id = Arc::clone(&thread_id);
            let report = Arc::clone(&report);
            std::thread::Builder::new()
                .name("qing-island-overlay".to_string())
                .spawn(move || run_overlay_thread(receiver, failure, stop, thread_id, report))
        };
        let thread = match thread {
            Ok(thread) => Some(thread),
            Err(error) => {
                let message = format!("could not start overlay thread: {error}");
                diagnostics::error("overlay", &message);
                *failure.lock().unwrap_or_else(|p| p.into_inner()) = Some(message);
                None
            }
        };
        Self {
            channel: OverlayChannel {
                sender,
                failure,
                thread_id,
                report,
            },
            stop,
            thread,
        }
    }

    /// Signal the overlay to stop and wait for it. Joining is what guarantees
    /// the HWND is destroyed before the process exits.
    fn shutdown(&mut self) {
        self.channel.send(OverlayCommand::Shutdown);
        self.stop.store(true, Ordering::Relaxed);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

// ---------------------------------------------------------------------------
// Module state
// ---------------------------------------------------------------------------

/// Everything the invoke handlers read or write.
struct Module {
    active: bool,
    wake: Arc<Condvar>,
    settings: Settings,
    broker: ActivityBroker,
    model: IslandModel,
    mock: MockProvider,
    codex: providers::codex::worker::ManagedCodex,
    program: programs::ProgramMonitor,
    timers: timers::Timers,
    hidden_until: Option<Instant>,
    overlay: OverlayChannel,
    /// Set while a preview is on screen, so a broker update does not stomp it.
    preview_active: bool,
    preview_until: Option<Instant>,
    /// Whether the island is currently suppressed, mirroring the overlay's
    /// decision so `getState` answers without a cross-thread round trip.
    suppressed: bool,
    /// The last foreground-fullscreen observation, so the tick thread can tell
    /// a real transition from a repeated observation of the same state.
    last_fullscreen: bool,
    /// Where this module instance persists its settings.
    ///
    /// Carried on the instance rather than recomputed from the environment at
    /// every call site so that tests can point one instance at a private
    /// directory. Reading a process-wide fallback here would make every test in
    /// the binary share a single `settings.json` and pass or fail depending on
    /// the order they happen to run in.
    settings_path: std::path::PathBuf,
    started: Instant,
}

impl Module {
    fn new(overlay: OverlayChannel) -> Self {
        Self::with_settings_path(overlay, paths::settings_path())
    }

    fn with_settings_path(overlay: OverlayChannel, settings_path: std::path::PathBuf) -> Self {
        let settings = Settings::load(&settings_path);
        let mock = MockProvider::new();
        let wake = Arc::new(Condvar::new());
        let codex = providers::codex::worker::ManagedCodex::new(Arc::clone(&wake));
        diagnostics::information("module", "Qing Island loaded (inactive)");
        Self {
            active: false,
            wake,
            settings,
            broker: ActivityBroker::new(),
            model: IslandModel::new(),
            mock,
            codex,
            program: programs::ProgramMonitor::new(),
            timers: timers::Timers::default(),
            hidden_until: None,
            overlay,
            preview_active: false,
            preview_until: None,
            suppressed: false,
            last_fullscreen: false,
            settings_path,
            started: Instant::now(),
        }
    }

    fn provider_statuses(&self) -> Vec<ProviderStatus> {
        let mut codex = self.codex.status();
        if self.active && self.settings.enabled {
            if !self.program.state.running {
                codex = providers::disabled_status(
                    ProviderKind::Codex,
                    self.program
                        .state
                        .error
                        .as_deref()
                        .unwrap_or("等待 Codex 程序启动"),
                );
            } else if codex.health == providers::ProviderHealth::Disabled {
                codex.health = providers::ProviderHealth::Disconnected;
                codex.detail = Some("connecting".into());
            }
        }
        vec![self.mock.status(), codex]
    }

    fn set_active(&mut self, active: bool) {
        if self.active == active && !self.preview_active {
            return;
        }
        self.active = active;
        self.preview_active = false;
        self.preview_until = None;
        self.overlay.send(OverlayCommand::EndPreview);
        self.reconcile_providers();
        if !active {
            self.broker.clear(None);
        }
        self.sync_overlay(false);
        self.wake.notify_all();
        diagnostics::information(
            "module",
            if active {
                "module enabled"
            } else {
                "module disabled; resident process retained"
            },
        );
    }

    fn reconcile_providers(&mut self) {
        let running = self.active && self.settings.enabled;
        if running {
            if self.mock.status().health == providers::ProviderHealth::Disabled {
                self.mock.start();
            }
        } else {
            self.mock.stop();
            self.broker.clear(None);
            self.timers.pause_all();
        }
        if running {
            self.program.update(false);
        } else {
            self.program.clear();
        }
        // Visibility and templates do not control the data source lifetime.
        self.codex
            .configure(running && self.program.state.running, 0);
    }

    fn upsert(&mut self, activity: LiveActivity) -> bool {
        let state = activity.state;
        let mutation = self.broker.upsert(activity);
        // One line per accepted change. Rejections are logged by the broker
        // itself, which is where the validation lives.
        let changed = matches!(
            mutation,
            crate::broker::Mutation::Created | crate::broker::Mutation::Updated
        );
        if changed {
            diagnostics::information(
                "broker",
                &format!("{mutation:?} activity ({})", state.as_str()),
            );
        }
        changed
    }

    fn account_data(&self) -> Option<providers::codex::worker::AccountSnapshot> {
        (self.active && self.settings.enabled && self.program.state.running)
            .then(|| self.codex.account_snapshot())
    }

    /// The tasks templates may refer to: the scripted preview while one is
    /// showing, otherwise the broker's ordered view.
    fn task_view(&self) -> (Option<LiveActivity>, Vec<LiveActivity>) {
        if self.preview_active {
            return (self.model.focus().cloned(), self.model.stack().to_vec());
        }
        let snapshot = self.broker.snapshot(VISIBLE_LIMIT);
        (snapshot.focus, snapshot.activities)
    }

    fn ambient_content(&self) -> Option<ambient::AmbientContent> {
        let (focus, stack) = self.task_view();
        ambient::content_with_runtime(
            &self.settings,
            ambient::local_time(),
            self.account_data().as_ref(),
            Some(&self.timers.snapshot(self.settings.countdown_seconds)),
            Some(&ambient::Tasks {
                focus: focus.as_ref(),
                stack: &stack,
                total: stack.len(),
            }),
        )
    }

    /// Push current truth to the overlay thread.
    ///
    /// The main thread keeps its own copy of the island model so that
    /// `getState` answers with the same state the overlay was told to draw.
    /// Reading the overlay thread's model instead would mean a round trip to a
    /// thread that may be wedged, so the copy is authoritative for reporting and
    /// the overlay thread owns the copy that drives pixels.
    fn sync_overlay(&mut self, fullscreen: bool) {
        self.last_fullscreen = fullscreen;
        let ambient = if (self.active && self.settings.enabled) || self.preview_active {
            self.ambient_content()
        } else {
            None
        };
        self.model.set_ambient(ambient.clone());
        if self.preview_active {
            if let Some(focus) = self.model.focus().cloned() {
                self.overlay.send(OverlayCommand::Preview {
                    settings: self.settings.clone(),
                    focus,
                    stack: self.model.stack().to_vec(),
                    ambient,
                });
            }
            return;
        }
        let snapshot = self.broker.snapshot(VISIBLE_LIMIT);
        self.suppressed = !self.active
            || !self.settings.enabled
            || self.hidden_until.is_some_and(|at| Instant::now() < at)
            || !fullscreen::allows(
                self.settings.fullscreen_policy,
                fullscreen,
                snapshot.focus.as_ref(),
            );
        self.model.set_suppressed(self.suppressed);
        self.model.set_content(
            snapshot.focus.clone(),
            snapshot.activities.clone(),
            snapshot.overflow,
            None,
        );
        self.overlay.send(OverlayCommand::Sync {
            settings: self.settings.clone(),
            focus: snapshot.focus,
            stack: snapshot.activities,
            overflow: snapshot.overflow,
            ambient,
            temporarily_hidden: self.hidden_until.is_some_and(|at| Instant::now() < at),
            fullscreen,
        });
    }

    /// One provider pass plus broker maintenance.
    ///
    /// Returns whether anything visible changed, so the caller can decide
    /// whether the overlay needs telling.
    fn tick(&mut self, now: u64) -> bool {
        if !self.active || !self.settings.enabled {
            return false;
        }
        let mut changed = self.program.update(false);
        if self.hidden_until.is_some_and(|at| Instant::now() >= at) {
            self.hidden_until = None;
            changed = true;
        }
        if self.timers.tick(self.settings.countdown_seconds) {
            diagnostics::information("timer", "countdown finished");
            changed = true;
        }
        if changed {
            self.codex.configure(self.program.state.running, 0);
        }

        // Each provider is polled exactly once per tick. Polling twice would
        // advance the mock's progress twice as fast and, worse, would make the
        // Codex provider's own interval accounting depend on call order.
        let mock_activities = self.mock.poll();
        for activity in mock_activities {
            changed |= self.upsert(activity);
        }

        let mut codex_activities = self.codex.poll();
        if !self.settings.show_codex_data {
            codex_activities.clear();
        }
        let codex_connected = self.codex.is_connected();
        let live_ids: Vec<String> = codex_activities
            .iter()
            .map(|activity| activity.id.clone())
            .collect();
        for activity in codex_activities {
            changed |= self.upsert(activity);
        }

        // A thread Codex stopped reporting is gone, not frozen. Without this a
        // finished thread would sit on the island forever.
        {
            let stale: Vec<String> = self
                .broker
                .activities()
                .into_iter()
                .filter(|activity| {
                    activity.provider == ProviderKind::Codex
                        && (!codex_connected || !live_ids.contains(&activity.id))
                })
                .map(|activity| activity.id)
                .collect();
            for id in stale {
                self.broker.remove(&id);
                changed = true;
            }
        }

        if self.broker.expire(now) > 0 {
            changed = true;
        }
        // Resolve templates after every provider and expiry change of this
        // tick, so task placeholders never lag one tick behind the broker.
        let ambient = self.ambient_content();
        if self.model.ambient() != ambient.as_ref() {
            self.model.set_ambient(ambient);
            changed = true;
        }
        changed
    }

    /// How long the tick thread may sleep before re-checking.
    ///
    /// Derived from the broker's own expiry schedule rather than a fixed
    /// interval, so a module holding a `Success` wakes exactly when it lapses
    /// and an idle one sleeps the full cap.
    fn tick_sleep(&self, now: u64) -> Duration {
        let cap_duration = if self.settings.show_clock
            || [
                &self.settings.custom_text,
                &self.settings.peek_text,
                &self.settings.expanded_text,
            ]
            .into_iter()
            .any(|text| text_template::uses(text, "time"))
            || self.timers.running()
            || self.hidden_until.is_some()
            || self.program.state.running
            || self.preview_active
            || self.settings.fullscreen_policy != settings::FullscreenPolicy::Always
        {
            MAX_TICK_SLEEP
        } else {
            self.program.until_probe().max(Duration::from_millis(16))
        };
        let cap = cap_duration.as_millis() as u64;
        match self.broker.next_expiry() {
            Some(at) => Duration::from_millis(at.saturating_sub(now).min(cap)),
            None => cap_duration,
        }
    }

    /// Whether the last sync was made while a fullscreen app was foreground.
    ///
    /// Tracked as its own field rather than derived from `suppressed`:
    /// suppression is the *policy* outcome and depends on whether anything
    /// important is showing, so a change of foreground app can be invisible to
    /// a comparison based on it alone.
    fn observed_fullscreen(&self) -> bool {
        self.last_fullscreen
    }
}

// ---------------------------------------------------------------------------
// Overlay thread
// ---------------------------------------------------------------------------

/// Own the island window for the lifetime of the module.
///
/// The window is created lazily — only once there is something to show — and
/// destroyed again when the island goes dormant, so an idle module holds no
/// topmost window at all.
fn run_overlay_thread(
    receiver: mpsc::Receiver<OverlayCommand>,
    failure: Arc<Mutex<Option<String>>>,
    stop: Arc<AtomicBool>,
    thread_id: Arc<AtomicU32>,
    report: Arc<Mutex<Value>>,
) {
    #[cfg(not(windows))]
    {
        let _ = (receiver, failure, stop, thread_id, report);
    }

    #[cfg(windows)]
    {
        run_overlay_thread_windows(receiver, failure, stop, thread_id, report);
    }
}

#[cfg(windows)]
fn run_overlay_thread_windows(
    receiver: mpsc::Receiver<OverlayCommand>,
    failure: Arc<Mutex<Option<String>>>,
    stop: Arc<AtomicBool>,
    thread_id: Arc<AtomicU32>,
    report: Arc<Mutex<Value>>,
) {
    let mut state = OverlayState::default();
    crate::win32::prepare_thread(&thread_id);

    loop {
        if stop.load(Ordering::Relaxed) {
            break;
        }

        crate::win32::pump_messages();
        let mut dirty = false;
        while let Ok(command) = receiver.try_recv() {
            dirty = true;
            match command {
                OverlayCommand::Shutdown => {
                    state.release_window();
                    return;
                }
                OverlayCommand::Sync {
                    settings,
                    focus,
                    stack,
                    overflow,
                    ambient,
                    temporarily_hidden,
                    fullscreen,
                } => {
                    state.settings = settings;
                    if state.preview.is_none() {
                        let allowed = state.settings.enabled
                            && !temporarily_hidden
                            && fullscreen::allows(
                                state.settings.fullscreen_policy,
                                fullscreen,
                                focus.as_ref(),
                            );
                        state.model.set_suppressed(!allowed);
                        state.model.set_ambient(ambient);
                        // The main thread already applied the same content to its
                        // own reporting model; this copy drives the window.
                        state.model.set_content(focus, stack, overflow, None);
                    }
                }
                OverlayCommand::Preview {
                    settings,
                    focus,
                    stack,
                    ambient,
                } => {
                    state.settings = settings;
                    state.preview = Some((focus.clone(), stack.clone()));
                    state.model.set_suppressed(false);
                    state.model.set_ambient(ambient);
                    state.model.set_content(Some(focus), stack, 0, None);
                }
                OverlayCommand::EndPreview => {
                    state.preview = None;
                    state.model.set_suppressed(true);
                    state.model.set_ambient(None);
                    state.model.set_content(None, Vec::new(), 0, None);
                }
            }
        }
        dirty |= state.poll_pointer();
        dirty |= state.pointer_moved();
        dirty |= state
            .window
            .as_ref()
            .is_some_and(|w| w.take_layout_change());
        let glass_active = state
            .window
            .as_ref()
            .is_some_and(|w| w.is_visible() && w.material().samples_backdrop());
        if dirty
            || state.transition.as_ref().is_some_and(|t| t.is_active())
            || glass_active && state.renderer.until_refresh().is_zero()
        {
            state.drive(&failure);
        }
        *report.lock().unwrap_or_else(|p| p.into_inner()) = json!({
            "state":state.model.state().as_str(), "visible": state.window.as_ref().is_some_and(|w| w.is_visible()),
            "hwnd":state.window.as_ref().map(|w| w.handle() as usize).unwrap_or(0),
            "bounds":{"x":state.anchor_bounds.x,"y":state.anchor_bounds.y,"width":state.anchor_bounds.width,"height":state.anchor_bounds.height},
            "renderCount":state.render_count,
            "material":state.window.as_ref().map(|w|w.material().as_str()),
            "materialFallback":state.window.as_ref().and_then(|w|w.material_fallback()),
            "clickThrough":state.window.as_ref().is_some_and(|w|w.click_through()),
            "glassSamples":state.renderer.samples,"glassSampleMicros":state.renderer.sample_micros,
        });
        let animating = state.transition.as_ref().is_some_and(|t| t.is_active());
        // Animation frames are paced by the display itself: wait for the
        // compositor's next frame, then draw again at once. Only if that is
        // unavailable fall back to a ~60 Hz timer.
        let paced = animating && crate::win32::wait_for_vblank();
        crate::win32::wait_messages(if paced {
            Some(Duration::ZERO)
        } else if animating {
            Some(Duration::from_millis(16))
        } else {
            state
                .window
                .as_ref()
                .filter(|w| w.is_visible() && w.material().samples_backdrop())
                .map(|_| state.renderer.until_refresh())
        });
    }

    // Dropping the window on this thread is required: the HWND belongs here.
    state.release_window();
    diagnostics::information("overlay", "overlay thread stopped");
}

/// The overlay thread's owned state.
///
/// Grouped into a struct because the alternative — a dozen locals threaded
/// through free functions — made every signature unreadable and every field
/// impossible to find.
#[cfg(windows)]
#[derive(Default)]
struct OverlayState {
    model: IslandModel,
    settings: Settings,
    window: Option<Box<crate::win32::IslandWindow>>,
    current: Option<IslandState>,
    anchor_bounds: crate::overlay::Bounds,
    monitor: Option<crate::display::MonitorMetrics>,
    transition: Option<crate::overlay::Transition>,
    target_bounds: crate::overlay::Bounds,
    last_frame: Option<Instant>,
    render_count: u64,
    /// A preview outranks broker content until it is dismissed.
    preview: Option<(LiveActivity, Vec<LiveActivity>)>,
    renderer: crate::renderer::Renderer,
}

#[cfg(windows)]
impl OverlayState {
    /// The monitor to place the island on, resolved fresh each time so a
    /// dock/undock is picked up without restarting the module.
    fn resolve_monitor(&self) -> crate::display::MonitorMetrics {
        use crate::settings::MonitorStrategy;
        let point = match self.settings.monitor_strategy {
            MonitorStrategy::Primary => {
                return crate::win32::primary_monitor().unwrap_or_else(crate::display::fallback)
            }
            // The cursor's monitor, falling back to the primary when the query
            // fails (a locked workstation returns nothing).
            MonitorStrategy::Active => crate::win32::IslandWindow::cursor_position(),
        };
        point
            .and_then(|(x, y)| crate::win32::monitor_for_point(x, y))
            .unwrap_or_else(crate::display::fallback)
    }

    fn release_window(&mut self) {
        self.renderer.clear();
        self.window = None;
        self.current = None;
        self.transition = None;
    }

    /// Create, place, show or hide the window to match the model.
    fn drive(&mut self, failure: &Arc<Mutex<Option<String>>>) {
        let state = self.model.state();
        if !crate::overlay::should_create_window(state) {
            self.renderer.clear();
            if let Some(active) = self.window.as_ref() {
                active.set_visible(false);
            }
            // Reuse at most one hidden HWND. The message wait has no timer in
            // this state, and the resource is destroyed on module shutdown.
            self.transition = None;
            self.anchor_bounds = Default::default();
            self.target_bounds = Default::default();
            return;
        }

        // A module that cannot draw must say so once, then stay quiet.
        if self
            .window
            .as_ref()
            .is_some_and(|w| w.requested_style() != self.settings.surface_style)
        {
            self.release_window();
        }
        if self.window.is_none() {
            match crate::win32::IslandWindow::create(self.settings.surface_style) {
                Ok(created) => {
                    if let Some(reason) = created.material_fallback() {
                        diagnostics::information("overlay/material", &reason);
                    }
                    self.window = Some(Box::new(created));
                    diagnostics::information("overlay", "island window created");
                    if let Ok(mut guard) = failure.lock() {
                        *guard = None;
                    }
                }
                Err(error) => {
                    diagnostics::error(
                        "overlay",
                        &format!("could not create the island window: {error}"),
                    );
                    if let Ok(mut guard) = failure.lock() {
                        *guard = Some(error);
                    }
                    return;
                }
            }
        }

        let monitor = self.resolve_monitor();
        let bounds = geometry(&self.model, &monitor, &self.settings);
        if bounds != self.target_bounds {
            let from = if self.anchor_bounds.is_empty() {
                (bounds.width as f64 * 0.9, bounds.height as f64 * 0.9)
            } else {
                (
                    self.anchor_bounds.width as f64,
                    self.anchor_bounds.height as f64,
                )
            };
            self.transition = Some(crate::overlay::Transition::begin_with(
                from,
                (bounds.width as f64, bounds.height as f64),
                crate::overlay::Motion::for_material(self.settings.surface_style),
            ));
            self.target_bounds = bounds;
            self.last_frame = Some(Instant::now());
        }
        let mut drawn = bounds;
        if let Some(transition) = &mut self.transition {
            let elapsed = self.last_frame.map(|at| at.elapsed()).unwrap_or_default();
            let (width, height) = transition.advance(elapsed);
            drawn.width = width.round().max(1.0) as i32;
            drawn.height = height.round().max(1.0) as i32;
            drawn = crate::overlay::position_bounds(
                drawn,
                &monitor,
                &self.settings,
                EDGE_MARGIN_LOGICAL,
            );
        }
        self.last_frame = Some(Instant::now());

        if let Some(active) = self.window.as_ref() {
            let controls = if self.settings.click_through {
                Vec::new()
            } else {
                interactive_regions(state, drawn, monitor.scale * self.settings.scale)
            };
            active.set_click_through(self.settings.click_through);
            if let Err(error) = active.apply(
                drawn,
                !self.settings.click_through,
                monitor.scale * self.settings.scale,
            ) {
                diagnostics::warning("overlay", &format!("could not place the island: {error}"));
                *failure.lock().unwrap_or_else(|p| p.into_inner()) = Some(error);
                return;
            }
            active.set_controls(controls);
            let progress = self
                .transition
                .as_ref()
                .map_or(1.0, crate::overlay::Transition::progress);
            // Liquid glass takes its light from the pointer and bulges towards
            // it, but only while the pointer is actually over the island.
            self.renderer.pointer = self.liquid_pointer(drawn);
            // While the shape changes, glass samples everything the island
            // will pass through (current and target, plus room for overshoot)
            // once, instead of capturing the desktop on every frame.
            let cover = if self.transition.as_ref().is_some_and(|t| t.is_active()) {
                let target = self.target_bounds;
                let left = drawn.x.min(target.x);
                let top = drawn.y.min(target.y);
                let right = (drawn.x + drawn.width).max(target.x + target.width);
                let bottom = (drawn.y + drawn.height).max(target.y + target.height);
                let slack_x = target.width / 6;
                let slack_y = target.height / 6;
                crate::overlay::Bounds {
                    x: left - slack_x,
                    y: top - slack_y,
                    width: right - left + slack_x * 2,
                    height: bottom - top + slack_y * 2,
                }
            } else {
                drawn
            };
            let painted = match crate::renderer::draw(
                &mut self.renderer,
                active,
                crate::renderer::Placement {
                    bounds: drawn,
                    cover,
                    scale: monitor.scale * self.settings.scale,
                },
                &self.model,
                &self.settings,
                progress,
            ) {
                Ok(painted) => painted,
                Err(error) => {
                    if let Ok(mut guard) = failure.lock() {
                        *guard = Some(error);
                    }
                    return;
                }
            };
            self.render_count += u64::from(painted);
            active.set_visible(true);
            *failure.lock().unwrap_or_else(|p| p.into_inner()) = None;
        }

        self.anchor_bounds = drawn;
        self.monitor = Some(monitor);
        self.current = Some(state);
    }

    /// The pointer in island-local pixels while it hovers liquid glass.
    fn liquid_pointer(&self, drawn: crate::overlay::Bounds) -> Option<(f64, f64)> {
        let window = self.window.as_ref()?;
        if self.settings.surface_style != settings::SurfaceStyle::Liquid
            || self.settings.click_through
            || !window.hovered()
        {
            return None;
        }
        crate::win32::IslandWindow::cursor_position()
            .map(|(x, y)| ((x - drawn.x) as f64 + 0.5, (y - drawn.y) as f64 + 0.5))
    }

    /// Whether the pointer moved over liquid glass since the last frame; its
    /// light and bulge follow it, so that alone is worth a redraw.
    fn pointer_moved(&mut self) -> bool {
        let now = self.liquid_pointer(self.anchor_bounds);
        let moved = now != self.renderer.pointer;
        moved && (now.is_some() || self.renderer.pointer.is_some())
    }

    /// Consume flags set by native mouse messages, without a pointer timer.
    fn poll_pointer(&mut self) -> bool {
        if self.model.state() == IslandState::Dormant {
            return false;
        }
        let previous = self.model.state();
        if self.settings.click_through {
            self.model.set_hovered(false);
            return self.model.state() != previous;
        }
        if let Some(window) = &self.window {
            if window.take_click() {
                self.model.toggle_expanded();
            } else if self.settings.peek_on_hover || !window.hovered() {
                self.model.set_hovered(window.hovered());
            }
        }
        self.model.state() != previous
    }
}

/// Where the island sits for a given state, settings and monitor.
///
/// Split out as a free function so the arithmetic can be read in one place and
/// tested without a window.
#[cfg(windows)]
fn geometry(
    model: &IslandModel,
    monitor: &crate::display::MonitorMetrics,
    settings: &Settings,
) -> crate::overlay::Bounds {
    crate::overlay::model_bounds(model, monitor, settings, EDGE_MARGIN_LOGICAL)
}

/// Which regions of the window accept the mouse.
///
/// Only the drawn pill is interactive. Everything else in the window — the
/// padding around the pill, the gap between stacked rows — is click-through, so
/// the island never behaves like an invisible slab across the screen.
#[cfg(windows)]
fn interactive_regions(
    state: IslandState,
    bounds: crate::overlay::Bounds,
    scale: f64,
) -> Vec<crate::overlay::Bounds> {
    if !state.is_interactive() {
        return Vec::new();
    }
    let scale = scale.max(0.1);
    vec![crate::overlay::Bounds {
        x: 0,
        y: 0,
        width: (bounds.width as f64 / scale).ceil() as i32,
        height: (bounds.height as f64 / scale).ceil() as i32,
    }]
}

fn current_fullscreen() -> bool {
    #[cfg(windows)]
    {
        crate::win32::foreground_is_fullscreen()
    }
    #[cfg(not(windows))]
    {
        false
    }
}

// ---------------------------------------------------------------------------
// Tick thread
// ---------------------------------------------------------------------------

fn spawn_tick_thread(
    module: Arc<Mutex<Module>>,
    stop: Arc<AtomicBool>,
) -> std::thread::JoinHandle<()> {
    std::thread::spawn(move || {
        let wake = Arc::clone(&module.lock().unwrap_or_else(|p| p.into_inner()).wake);
        let mut guard = module.lock().unwrap_or_else(|p| p.into_inner());
        while !stop.load(Ordering::Relaxed) {
            if guard.preview_until.is_some_and(|at| Instant::now() >= at) {
                let _ = guard.invoke("dismissPreview", &json!({}));
            }
            let running = guard.active && guard.settings.enabled;
            // Active modules continue a low-frequency presence probe even if
            // the clock/data display are hidden; inactive modules have no timer.
            let work = running;
            if work {
                let fullscreen = current_fullscreen();
                let changed = guard.tick(activity::now_millis());
                if changed || fullscreen != guard.observed_fullscreen() {
                    guard.sync_overlay(fullscreen);
                }
            }
            if work || guard.preview_active {
                let sleep = guard
                    .tick_sleep(activity::now_millis())
                    .max(Duration::from_millis(16));
                guard = wake
                    .wait_timeout(guard, sleep)
                    .unwrap_or_else(|p| p.into_inner())
                    .0;
            } else {
                // Loaded/disabled and dormant/no-provider states have no timer.
                guard = wake.wait(guard).unwrap_or_else(|p| p.into_inner());
            }
        }
        diagnostics::information("module", "tick thread stopped");
    })
}

// ---------------------------------------------------------------------------
// Invoke dispatch
// ---------------------------------------------------------------------------

/// Every method this module answers, in the order `module.json` declares them.
///
/// The host refuses an invoke for a method that is not in the manifest, so a
/// method added to `invoke` without also being listed here would be silently
/// unreachable. The test below pins the two lists together.
const OPERATIONS: &[&str] = &[
    "getState",
    "setSettings",
    "previewSettings",
    "previewIsland",
    "dismissPreview",
    "emitMockActivity",
    "clearActivities",
    "readDiagnostics",
    "clearDiagnostics",
    "setCodexEnabled",
    "refreshProviders",
    "timerCommand",
    "hideTemporarily",
    "restoreIsland",
];

impl Module {
    fn invoke(&mut self, method: &str, payload: &Value) -> Result<Value, ModuleError> {
        match method {
            "getState" => Ok(self.state_payload()),
            "setSettings" => self.set_settings(payload),
            "previewSettings" => self.preview_settings(payload),
            "timerCommand" => {
                let command: timers::Command = serde_json::from_value(payload.clone())
                    .map_err(|_| ModuleError::invalid("计时器操作无效。"))?;
                if matches!(command.action, timers::Action::Start)
                    && (!self.active || !self.settings.enabled)
                {
                    return Err(ModuleError::new("inactive", "请先启用灵动岛。"));
                }
                self.timers.tick(self.settings.countdown_seconds);
                self.timers.command(command);
                self.sync_overlay(self.last_fullscreen);
                self.wake.notify_all();
                Ok(self.state_payload())
            }
            "hideTemporarily" => {
                self.preview_active = false;
                self.preview_until = None;
                self.overlay.send(OverlayCommand::EndPreview);
                self.hidden_until = Some(Instant::now() + Duration::from_secs(30));
                self.sync_overlay(self.last_fullscreen);
                self.wake.notify_all();
                Ok(self.state_payload())
            }
            "restoreIsland" => {
                self.hidden_until = None;
                self.sync_overlay(self.last_fullscreen);
                self.wake.notify_all();
                Ok(self.state_payload())
            }
            "previewIsland" => self.preview_island(payload),
            "dismissPreview" => {
                self.preview_active = false;
                self.preview_until = None;
                self.overlay.send(OverlayCommand::EndPreview);
                // Dropping the preview must restore the reporting model to the
                // real stack, otherwise `getState` would keep describing the
                // preview after it was dismissed.
                let fullscreen = self.last_fullscreen;
                self.sync_overlay(fullscreen);
                Ok(self.state_payload())
            }
            "emitMockActivity" => self.emit_mock(payload),
            "clearActivities" => {
                self.mock.stop();
                if self.active && self.settings.enabled {
                    self.mock.start();
                }
                self.broker.clear(None);
                let fullscreen = self.last_fullscreen;
                self.sync_overlay(fullscreen);
                Ok(self.state_payload())
            }
            "readDiagnostics" => Ok(self.diagnostics_payload()),
            "clearDiagnostics" => {
                diagnostics::clear();
                Ok(self.diagnostics_payload())
            }
            "setCodexEnabled" => self.set_codex_enabled(payload),
            "refreshProviders" => {
                if self.active && self.settings.enabled {
                    self.program.update(true);
                    self.reconcile_providers();
                }
                self.codex.refresh();
                let fullscreen = self.last_fullscreen;
                self.tick(activity::now_millis());
                self.sync_overlay(fullscreen);
                Ok(self.state_payload())
            }
            _ => Err(ModuleError::unknown_method()),
        }
    }

    fn state_payload(&self) -> Value {
        let timers = self.timers.snapshot(self.settings.countdown_seconds);
        let mut template_values = text_template::values(
            &self.settings,
            ambient::local_time(),
            self.account_data().as_ref(),
            activity::now_millis(),
        );
        ambient::apply_timer_values(Some(&timers), &mut template_values);
        let (focus, stack) = self.task_view();
        text_template::apply_task_values(&mut template_values, focus.as_ref(), &stack, stack.len());
        let snapshot = self.broker.snapshot(VISIBLE_LIMIT);
        let native = self
            .overlay
            .report
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .clone();
        let visual_state = if self.model.state() == IslandState::Dormant {
            "dormant"
        } else {
            native["state"]
                .as_str()
                .filter(|state| *state != "dormant")
                .unwrap_or(self.model.state().as_str())
        };
        json!({
            "active": self.active,
            "settings": self.settings,
            "island": {
                "state": visual_state,
                "suppressed": self.suppressed,
                "previewActive": self.preview_active,
                "focus": snapshot.focus,
                "stack": snapshot.activities,
                "overflow": snapshot.overflow,
                // Retired API 1 wire keys remain null for old clients.
                "account": null,
                "accountHeader": null,
                "ambient": self.model.ambient(),
            },
            "counts": {
                "activities": snapshot.activities.len(),
                "revision": snapshot.revision,
                "dropped": self.broker.dropped(),
            },
            "providers": self.provider_statuses(),
            "codexAccount": self.account_data().unwrap_or_default(),
            "codexProgram": self.program.state,
            "placeholders": text_template::PLACEHOLDERS.iter().map(|(key, label)| json!({"key":key,"label":label})).collect::<Vec<_>>(),
            "templateValues": template_values,
            "timers": timers,
            "hiddenSeconds": self.hidden_until.map(|at| at.saturating_duration_since(Instant::now()).as_millis().div_ceil(1000)).unwrap_or(0),
            "overlay": {
                // A round trip to the overlay thread would let a wedged overlay
                // hang `getState`. The failure slot is the only piece of overlay
                // state the settings page genuinely needs to see.
                "failure": self.overlay.failure(),
                "visible": native["visible"].as_bool().unwrap_or(false),
                "state": native["state"],
                "hwnd": native["hwnd"].as_u64().unwrap_or(0),
                "bounds": native["bounds"],
                "renderCount": native["renderCount"].as_u64().unwrap_or(0),
                "material":native["material"],
                "materialFallback":native["materialFallback"],
                "clickThrough":native["clickThrough"].as_bool().unwrap_or(false),
                "glassSamples":native["glassSamples"],"glassSampleMicros":native["glassSampleMicros"],
            },
            "uptimeSeconds": self.started.elapsed().as_secs(),
            "platform": if cfg!(windows) { "windows" } else { "unsupported" },
        })
    }

    fn diagnostics_payload(&self) -> Value {
        json!({
            "entries": diagnostics::entries(),
            "totalRecorded": diagnostics::total_recorded(),
            "providers": self.provider_statuses(),
        })
    }

    fn set_settings(&mut self, payload: &Value) -> Result<Value, ModuleError> {
        let patch: SettingsPatch = serde_json::from_value(payload.clone())
            .map_err(|error| ModuleError::invalid(format!("设置参数无效：{error}")))?;
        let mut next = self.settings.clone();
        next.apply(patch);
        let patch_duration_changed = next.countdown_seconds != self.settings.countdown_seconds;
        if let Err(error) = next.save(&self.settings_path) {
            diagnostics::warning("settings", &format!("could not persist settings: {error}"));
            return Err(ModuleError::new("persist_failed", error));
        }
        self.settings = next;
        if patch_duration_changed {
            self.timers.reset_countdown();
        }
        self.reconcile_providers();
        self.wake.notify_all();
        // Use the tracked observation rather than probing again: the tick thread
        // owns fullscreen detection, and a second probe here could disagree with
        // it mid-transition.
        let fullscreen = self.last_fullscreen;
        self.sync_overlay(fullscreen);
        Ok(self.state_payload())
    }

    /// Apply geometry or tint while a slider is held, without writing the
    /// settings file on every pointer move. The page commits the released
    /// value with `setSettings`, which persists the whole in-memory state.
    fn preview_settings(&mut self, payload: &Value) -> Result<Value, ModuleError> {
        let patch: settings::VisualPatch = serde_json::from_value(payload.clone())
            .map_err(|error| ModuleError::invalid(format!("预览参数无效：{error}")))?;
        self.settings.apply(patch.into());
        // Same bounds as a saved value, without writing the file.
        self.settings.normalize();
        let fullscreen = self.last_fullscreen;
        self.sync_overlay(fullscreen);
        Ok(json!({ "previewed": true }))
    }

    fn preview_island(&mut self, payload: &Value) -> Result<Value, ModuleError> {
        // The preview is rendered from the mock provider's scripted content so
        // that opening the settings page can never disturb a real task. The
        // preview island and the real overlay share a renderer but not a state:
        // this flag is what keeps a broker update from replacing it.
        let scenario = payload
            .get("scenario")
            .and_then(Value::as_str)
            .unwrap_or("working");
        let parsed = crate::providers::mock::MockScenario::parse(scenario)
            .ok_or_else(|| ModuleError::invalid("未知的预览场景。"))?;
        let mut preview = MockProvider::new();
        preview.start();
        preview.emit(parsed);
        let focus = preview
            .peek()
            .into_iter()
            .find(|activity| activity.details.get("scenario").map(String::as_str) == Some(scenario))
            .unwrap_or_else(|| {
                LiveActivity::running("preview", ProviderKind::Mock, "preview", "Preview")
                    .with_state(ActivityState::Running)
                    .with_subtitle("Working in the background")
            });
        self.preview_active = true;
        self.preview_until = Some(Instant::now() + Duration::from_secs(30));
        self.model.set_suppressed(false);
        self.suppressed = false;
        // Mirror the preview into the reporting model as well, so an active
        // preview is visible through `getState` without waking the overlay.
        // It goes in first: the templates' task placeholders read it.
        self.model
            .set_content(Some(focus.clone()), vec![focus.clone()], 0, None);
        self.model.set_ambient(self.ambient_content());
        self.overlay.send(OverlayCommand::Preview {
            settings: self.settings.clone(),
            focus: focus.clone(),
            stack: vec![focus],
            ambient: self.ambient_content(),
        });
        self.wake.notify_all();
        Ok(self.state_payload())
    }

    fn emit_mock(&mut self, payload: &Value) -> Result<Value, ModuleError> {
        let scenario = payload
            .get("scenario")
            .and_then(Value::as_str)
            .unwrap_or("working");
        if scenario != "all" && crate::providers::mock::MockScenario::parse(scenario).is_none() {
            return Err(ModuleError::invalid("未知的模拟场景。"));
        }
        if !self.active || !self.settings.enabled {
            return Err(ModuleError::new(
                "inactive",
                "请先启用模块和实时活动；预览无需启用。",
            ));
        }
        if scenario == "all" {
            for id in self.mock.emit_all() {
                diagnostics::information("module", &format!("scripted activity {id}"));
            }
        } else {
            let parsed = crate::providers::mock::MockScenario::parse(scenario)
                .ok_or_else(|| ModuleError::invalid(format!("未知的模拟场景：{scenario}")))?;
            self.mock.emit(parsed);
        }
        let fullscreen = self.last_fullscreen;
        self.tick(activity::now_millis());
        self.sync_overlay(fullscreen);
        self.wake.notify_all();
        Ok(self.state_payload())
    }

    fn set_codex_enabled(&mut self, payload: &Value) -> Result<Value, ModuleError> {
        let enabled = payload
            .get("enabled")
            .and_then(Value::as_bool)
            .ok_or_else(|| ModuleError::invalid("enabled 必须是布尔值。"))?;
        // Kept for v1 callers: this operation now changes display only.
        self.set_settings(&json!({"showCodexData": enabled}))
    }
}

// ---------------------------------------------------------------------------
// Protocol loop
// ---------------------------------------------------------------------------

fn valid_token(value: &str, max: usize) -> bool {
    !value.is_empty()
        && value.len() <= max
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-' | b'_' | b':'))
}

fn valid_envelope(envelope: &HostEnvelope) -> bool {
    envelope.protocol_version == HOST_PROTOCOL_VERSION
        && valid_token(&envelope.message_type, 64)
        && valid_token(&envelope.request_id, 128)
}

fn write_response(
    writer: &mut BufWriter<impl Write>,
    message_type: &str,
    request_id: &str,
    payload: Value,
    error: Option<HostErrorBody>,
) {
    let response = HostResponse {
        protocol_version: HOST_PROTOCOL_VERSION,
        message_type,
        request_id,
        payload,
        error,
    };
    if let Ok(bytes) = serde_json::to_vec(&response) {
        if bytes.len() <= HOST_MAX_FRAME_BYTES {
            let _ = writer.write_all(&bytes);
            let _ = writer.write_all(b"\n");
            let _ = writer.flush();
        }
    }
}

fn main() {
    #[cfg(windows)]
    crate::win32::enable_dpi_awareness();
    let mut runtime = OverlayRuntime::start();
    let module = Arc::new(Mutex::new(Module::new(runtime.channel.clone())));
    let tick_stop = Arc::new(AtomicBool::new(false));
    let tick = spawn_tick_thread(Arc::clone(&module), Arc::clone(&tick_stop));

    let module_id =
        std::env::var("QINGTOOLBOX_MODULE_ID").unwrap_or_else(|_| paths::MODULE_ID.to_string());
    let nonce = std::env::var("QINGTOOLBOX_MODULE_NONCE").unwrap_or_default();
    let stdin = io::stdin();
    let stdout = io::stdout();
    let mut reader = BufReader::new(stdin.lock());
    let mut writer = BufWriter::new(stdout.lock());
    let mut line = Vec::new();
    let mut handshaken = false;

    loop {
        line.clear();
        let read = match (&mut reader)
            .take((HOST_MAX_FRAME_BYTES + 1) as u64)
            .read_until(b'\n', &mut line)
        {
            Ok(read) => read,
            Err(_) => break,
        };
        if read == 0 {
            break;
        }
        if line.len() > HOST_MAX_FRAME_BYTES {
            write_response(
                &mut writer,
                "module.protocol.response",
                "unknown",
                json!({}),
                Some(HostErrorBody {
                    code: "frame_too_large",
                    message: "模块请求帧过大。".to_string(),
                }),
            );
            break;
        }
        let envelope = match serde_json::from_slice::<HostEnvelope>(&line) {
            Ok(envelope) if valid_envelope(&envelope) => envelope,
            _ => {
                write_response(
                    &mut writer,
                    "module.protocol.response",
                    "unknown",
                    json!({}),
                    Some(HostErrorBody {
                        code: "invalid_frame",
                        message: "模块请求帧无效。".to_string(),
                    }),
                );
                continue;
            }
        };

        match envelope.message_type.as_str() {
            "module.hello.request" if !handshaken => {
                let valid = envelope.payload.get("moduleId").and_then(Value::as_str)
                    == Some(module_id.as_str())
                    && envelope.payload.get("nonce").and_then(Value::as_str)
                        == Some(nonce.as_str());
                if !valid {
                    write_response(
                        &mut writer,
                        "module.hello.response",
                        &envelope.request_id,
                        json!({}),
                        Some(HostErrorBody {
                            code: "hello_rejected",
                            message: "模块 hello 校验失败。".to_string(),
                        }),
                    );
                    break;
                }
                handshaken = true;
                write_response(
                    &mut writer,
                    "module.hello.response",
                    &envelope.request_id,
                    json!({
                        "moduleId": module_id,
                        "nonce": nonce,
                        "lifecycleVersion": 1,
                        "name": MODULE_NAME,
                        "protocolVersion": HOST_PROTOCOL_VERSION
                    }),
                    None,
                );
            }
            "module.lifecycle.request" if handshaken => {
                let Some(active) = envelope.payload.get("active").and_then(Value::as_bool) else {
                    break;
                };
                module
                    .lock()
                    .unwrap_or_else(|p| p.into_inner())
                    .set_active(active);
                write_response(
                    &mut writer,
                    "module.lifecycle.response",
                    &envelope.request_id,
                    json!({ "active": active }),
                    None,
                );
            }
            "module.invoke.request" if handshaken => {
                let method = envelope
                    .payload
                    .get("method")
                    .and_then(Value::as_str)
                    .unwrap_or_default();
                let payload = envelope.payload.get("payload").unwrap_or(&Value::Null);
                let result = {
                    let mut guard = module
                        .lock()
                        .unwrap_or_else(|poisoned| poisoned.into_inner());
                    guard.invoke(method, payload)
                };
                match result {
                    Ok(value) => write_response(
                        &mut writer,
                        "module.invoke.response",
                        &envelope.request_id,
                        value,
                        None,
                    ),
                    Err(error) => write_response(
                        &mut writer,
                        "module.invoke.response",
                        &envelope.request_id,
                        json!({}),
                        Some(HostErrorBody {
                            code: error.code,
                            message: error.message,
                        }),
                    ),
                }
            }
            "module.shutdown.request" if handshaken => {
                write_response(
                    &mut writer,
                    "module.shutdown.response",
                    &envelope.request_id,
                    json!({}),
                    None,
                );
                break;
            }
            _ => write_response(
                &mut writer,
                "module.protocol.response",
                &envelope.request_id,
                json!({}),
                Some(HostErrorBody {
                    code: "invalid_message",
                    message: "模块消息顺序或类型无效。".to_string(),
                }),
            ),
        }
    }

    // Ordered shutdown: stop producing, then stop drawing, then release
    // providers. Doing it in this order means the overlay never asks the broker
    // for a snapshot while the broker is being torn down.
    tick_stop.store(true, Ordering::Relaxed);
    module
        .lock()
        .unwrap_or_else(|p| p.into_inner())
        .wake
        .notify_all();
    let _ = tick.join();
    {
        let mut guard = module
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        guard.codex.configure(false, 0);
        guard.mock.stop();
    }
    runtime.shutdown();
    diagnostics::information("module", "Qing Island stopped");
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn clock_and_custom_text_are_not_broker_tasks_and_stop_with_the_module() {
        let mut module = headless_module("ambient-residency");
        module
            .invoke(
                "setSettings",
                &json!({"showClock":true,"customText":"专注当下","fullscreenPolicy":"always"}),
            )
            .unwrap();
        let state = module.state_payload();
        assert_eq!(state["counts"]["activities"], 0);
        assert!(state["island"]["ambient"]["clock"].as_str().is_some());
        assert_eq!(state["island"]["state"], "compact");
        module
            .invoke("emitMockActivity", &json!({"scenario":"waiting"}))
            .unwrap();
        assert_eq!(
            module.state_payload()["island"]["focus"]["state"],
            "waiting"
        );
        module.invoke("clearActivities", &json!({})).unwrap();
        assert_eq!(module.state_payload()["island"]["state"], "compact");
        module.set_active(false);
        assert_eq!(module.state_payload()["island"]["state"], "dormant");
        assert!(module.state_payload()["island"]["ambient"].is_null());
        module.set_active(true);
        assert_eq!(module.state_payload()["island"]["state"], "compact");
        module
            .invoke("setSettings", &json!({"showClock":false,"customText":""}))
            .unwrap();
        assert_eq!(module.state_payload()["island"]["state"], "dormant");
    }

    #[test]
    fn settings_can_update_the_active_preview_without_ending_it() {
        let mut module = headless_module("ambient-preview-settings");
        module
            .invoke("previewIsland", &json!({"scenario":"working"}))
            .unwrap();
        module
            .invoke(
                "setSettings",
                &json!({"customText":"Keep focus","surfaceStyle":"frosted"}),
            )
            .unwrap();
        assert!(module.preview_active);
        assert_eq!(module.model.focus().unwrap().provider, ProviderKind::Mock);
        assert_eq!(module.model.ambient().unwrap().text, "Keep focus");
        assert_eq!(module.broker.activities().len(), 0);
    }

    /// A module with no overlay, pointed at its own private settings file.
    ///
    /// The state directory is never the shared process fallback: two tests that
    /// both call `setSettings` would otherwise write the same `settings.json`
    /// and make the suite order-dependent.
    fn headless_module(name: &str) -> Module {
        let dir = std::env::temp_dir().join(format!(
            "qing-liveactivity-test-{name}-{}",
            std::process::id()
        ));
        // Start from a clean slate: a leftover settings file from an earlier run
        // would silently change the defaults these tests assert on.
        let _ = std::fs::remove_dir_all(&dir);
        let _ = std::fs::create_dir_all(&dir);
        let (sender, _receiver) = mpsc::channel();
        let mut module = Module::with_settings_path(
            OverlayChannel {
                sender,
                failure: Arc::new(Mutex::new(None)),
                thread_id: Arc::new(AtomicU32::new(0)),
                report: Arc::new(Mutex::new(Value::Null)),
            },
            dir.join("settings.json"),
        );
        module.settings.enabled = true;
        module.settings.show_clock = false;
        module.settings.peek_text.clear();
        module.settings.expanded_text.clear();
        module.set_active(true);
        module
    }

    #[test]
    fn envelope_validation_rejects_a_foreign_protocol_version() {
        let envelope = HostEnvelope {
            protocol_version: 2,
            message_type: "module.invoke.request".to_string(),
            request_id: "abc".to_string(),
            payload: json!({}),
        };
        assert!(!valid_envelope(&envelope));
    }

    #[test]
    fn envelope_validation_rejects_a_malformed_request_id() {
        let envelope = HostEnvelope {
            protocol_version: HOST_PROTOCOL_VERSION,
            message_type: "module.invoke.request".to_string(),
            request_id: "has a space".to_string(),
            payload: json!({}),
        };
        assert!(!valid_envelope(&envelope));
    }

    #[test]
    fn unknown_methods_are_reported_rather_than_silently_ignored() {
        let mut module = headless_module("unknown-methods");
        let error = module.invoke("launchTheMissiles", &json!({})).unwrap_err();
        assert_eq!(error.code, "unknown_method");
    }

    #[test]
    fn state_payload_always_reports_every_declared_provider() {
        let module = headless_module("provider-reporting");
        let state = module.state_payload();
        let providers = state["providers"].as_array().expect("providers array");
        let kinds: Vec<&str> = providers
            .iter()
            .filter_map(|entry| entry["kind"].as_str())
            .collect();
        assert!(kinds.contains(&"mock"));
        assert!(kinds.contains(&"codex"));
        assert_eq!(state["platform"], "windows");
    }

    #[test]
    fn quota_changes_update_a_taskless_island_without_waiting_for_the_clock() {
        let mut module = headless_module("quota-only-content");
        module.active = true;
        module.settings.enabled = true;
        module.settings.show_clock = false;
        module.settings.custom_text = "余量 {codex.remaining|未接入}".into();
        module.program.force_for_test(true);
        module.reconcile_providers();
        module.codex.set_limits_for_test(None);
        module.sync_overlay(false);
        assert!(!module.tick(activity::now_millis()));
        module
            .codex
            .set_limits_for_test(Some(providers::codex::protocol::RateLimits::parse(
                &json!({"usedPercent":25}),
            )));
        assert!(module.tick(activity::now_millis()));
        module.sync_overlay(false);
        assert_eq!(module.model.account(), None);
        assert_eq!(module.model.ambient().unwrap().text, "余量 75%");
        assert!(module.model.account_header().is_none());
        assert!(!module.tick(activity::now_millis()));
        module.codex.set_limits_for_test(None);
        assert!(module.tick(activity::now_millis()));
        module.sync_overlay(false);
        assert!(module.model.account().is_none());
        assert!(module.model.account_header().is_none());
        assert_eq!(module.model.ambient().unwrap().text, "余量 未接入");
    }

    #[cfg(windows)]
    #[test]
    fn resized_capsule_remains_clickable_at_the_actual_width_and_dpi() {
        let bounds = overlay::Bounds {
            x: 0,
            y: 0,
            width: 480,
            height: 84,
        };
        let controls = interactive_regions(IslandState::Compact, bounds, 1.5);
        assert_eq!(
            overlay::hit_test(IslandState::Compact, (460, 70), (480, 84), 1.5, &controls),
            overlay::Hit::Interactive
        );
        assert!(interactive_regions(IslandState::Dormant, bounds, 1.5).is_empty());
    }

    #[test]
    fn a_mock_activity_reaches_the_snapshot_without_a_window() {
        let mut module = headless_module("mock-snapshot-without-window");
        module
            .invoke("emitMockActivity", &json!({ "scenario": "waiting" }))
            .expect("emit");
        let state = module.state_payload();
        assert_eq!(state["counts"]["activities"], 1);
        assert_eq!(state["island"]["focus"]["state"], "waiting");
    }

    #[test]
    fn the_reported_island_state_follows_the_content() {
        // Regression: `state_payload` used to read a model that only the overlay
        // thread ever updated, so `getState` answered "dormant" forever while the
        // user could see a pill on screen. The reporting model has to track
        // content, not just the suppressed flag.
        let mut module = headless_module("reported-state-follows-content");
        // Pin the policy so ambient fullscreen state cannot decide this test.
        module
            .invoke("setSettings", &json!({ "fullscreenPolicy": "always" }))
            .expect("pin policy");
        assert_eq!(module.state_payload()["island"]["state"], "dormant");

        module
            .invoke("emitMockActivity", &json!({ "scenario": "working" }))
            .expect("emit");
        let state = module.state_payload();
        assert_eq!(state["island"]["suppressed"], false);
        assert_eq!(state["island"]["state"], "compact");

        module.invoke("clearActivities", &json!({})).expect("clear");
        assert_eq!(module.state_payload()["island"]["state"], "dormant");
    }

    #[test]
    fn a_dismissed_preview_restores_the_real_island_state() {
        let mut module = headless_module("dismissed-preview-restores-state");
        module
            .invoke("setSettings", &json!({ "fullscreenPolicy": "always" }))
            .expect("pin policy");
        module
            .invoke("previewIsland", &json!({ "scenario": "working" }))
            .expect("preview");
        let previewing = module.state_payload();
        assert_eq!(previewing["island"]["previewActive"], true);
        assert_ne!(previewing["island"]["state"], "dormant");

        module
            .invoke("dismissPreview", &json!({}))
            .expect("dismiss");
        let after = module.state_payload();
        assert_eq!(after["island"]["previewActive"], false);
        // The preview never created real activities, so the island must be gone.
        assert_eq!(after["island"]["state"], "dormant");
        assert_eq!(after["counts"]["activities"], 0);
    }

    #[test]
    fn a_rejected_scenario_does_not_corrupt_the_snapshot() {
        let mut module = headless_module("rejected-scenario");
        let error = module
            .invoke("emitMockActivity", &json!({ "scenario": "nonsense" }))
            .unwrap_err();
        assert_eq!(error.code, "invalid_payload");
        assert_eq!(module.state_payload()["counts"]["activities"], 0);
    }

    #[test]
    fn settings_patches_stop_at_the_boundary() {
        let mut module = headless_module("settings-boundary");
        let error = module
            .invoke("setSettings", &json!({ "turnItToEleven": true }))
            .unwrap_err();
        assert_eq!(error.code, "invalid_payload");
    }

    #[test]
    fn clearing_activities_leaves_an_empty_island_not_a_stale_one() {
        let mut module = headless_module("clearing-leaves-empty-island");
        module
            .invoke("emitMockActivity", &json!({ "scenario": "all" }))
            .expect("emit");
        assert!(
            module.state_payload()["counts"]["activities"]
                .as_u64()
                .unwrap()
                > 0
        );
        let state = module.invoke("clearActivities", &json!({})).expect("clear");
        assert_eq!(state["counts"]["activities"], 0);
        assert!(state["island"]["focus"].is_null());
    }

    #[test]
    fn codex_can_be_switched_off_and_the_choice_sticks() {
        let mut module = headless_module("codex-toggle-sticks");
        module
            .invoke("setCodexEnabled", &json!({ "enabled": true }))
            .expect("enable");
        module
            .invoke("setCodexEnabled", &json!({ "enabled": false }))
            .expect("disable");
        let error = module
            .invoke("setCodexEnabled", &json!({ "enabled": "yes" }))
            .unwrap_err();
        assert_eq!(error.code, "invalid_payload");
    }

    #[test]
    fn automatic_acquisition_does_not_depend_on_visibility_and_stops_on_exit() {
        let mut module = headless_module("codex-auto-presence");
        module.program.force_for_test(true);
        module.reconcile_providers();
        module.codex.set_limits_for_test(None);
        assert!(module.codex.configured_for_test());
        module
            .invoke("setSettings", &json!({"showCodexData":false}))
            .unwrap();
        assert!(
            module.codex.configured_for_test(),
            "hiding data must not stop acquisition"
        );
        module.program.force_for_test(false);
        module.tick(activity::now_millis());
        assert!(!module.codex.configured_for_test());
        assert!(module.account_data().is_none());
        module.set_active(false);
        assert!(module.program.state.checked_at_ms.is_none());
    }

    #[test]
    fn templates_are_the_only_quota_display_and_legacy_positions_are_ignored() {
        let mut module = headless_module("template-positions");
        module.settings.custom_text = "额度 {codex.remaining|未连接}".into();
        module.settings.peek_text = "悬停 {codex.remaining|未连接}".into();
        module.settings.expanded_text = "展开 {codex.remaining|未连接}".into();
        module.program.force_for_test(true);
        module.reconcile_providers();
        module
            .codex
            .set_limits_for_test(Some(providers::codex::protocol::RateLimits::parse(
                &json!({"usedPercent":25}),
            )));
        module.tick(activity::now_millis());
        module.sync_overlay(false);
        assert_eq!(module.model.ambient().unwrap().text, "额度 75%");
        assert_eq!(module.model.peek_detail().as_deref(), Some("悬停 75%"));
        assert_eq!(module.model.expanded_text(), Some("展开 75%"));
        assert!(module.model.account().is_none() && module.model.account_header().is_none());
        module
            .invoke("setSettings", &json!({"codexDataPosition":"expanded"}))
            .unwrap();
        assert!(module.model.account().is_none() && module.model.account_header().is_none());
        assert_eq!(module.model.ambient().unwrap().text, "额度 75%");
        module
            .invoke("setSettings", &json!({"codexDataPosition":"header"}))
            .unwrap();
        assert!(module.model.account().is_none() && module.model.account_header().is_none());
        assert_eq!(module.model.ambient().unwrap().text, "额度 75%");
        assert!(module.state_payload()["settings"]
            .get("codexDataPosition")
            .is_none());
        module
            .invoke("setSettings", &json!({"showCodexData":false}))
            .unwrap();
        assert_eq!(module.model.ambient().unwrap().text, "额度 未连接");
        assert_eq!(module.model.peek_detail().as_deref(), Some("悬停 未连接"));
        assert_eq!(module.model.expanded_text(), Some("展开 未连接"));
        assert!(
            module.codex.configured_for_test(),
            "display must not stop acquisition"
        );
        module
            .invoke("setSettings", &json!({"showCodexData":true}))
            .unwrap();
        module.program.force_for_test(false);
        module.tick(activity::now_millis());
        module.sync_overlay(false);
        assert_eq!(module.model.ambient().unwrap().text, "额度 未连接");
        assert!(module.model.account_header().is_none());
    }

    #[test]
    fn missing_template_with_blank_fallback_does_not_create_an_empty_island() {
        let mut module = headless_module("template-empty");
        module.invoke("setSettings", &json!({"customText":"{codex.remaining}","placeholderFallback":"","fullscreenPolicy":"always"})).unwrap();
        assert!(module.model.ambient().is_none());
        assert_eq!(module.model.state(), IslandState::Dormant);
    }

    #[test]
    fn quota_without_templates_does_not_create_a_preset_island() {
        let mut module = headless_module("quota-only-content");
        module.program.force_for_test(true);
        module.reconcile_providers();
        module
            .codex
            .set_limits_for_test(Some(providers::codex::protocol::RateLimits::parse(
                &json!({"usedPercent":25}),
            )));
        for position in ["header", "expanded"] {
            module
                .invoke(
                    "setSettings",
                    &json!({"showClock":false,"customText":"","codexDataPosition":position}),
                )
                .unwrap();
            assert!(module.model.ambient().is_none());
            assert_eq!(module.model.state(), IslandState::Dormant);
            assert_eq!(module.model.compact_label(), None);
            assert!(module.model.account().is_none() && module.model.account_header().is_none());
        }
        module
            .invoke("setSettings", &json!({"showCodexData":false}))
            .unwrap();
        assert_eq!(module.model.state(), IslandState::Dormant);
        assert!(module.codex.configured_for_test());
    }

    #[test]
    fn timer_runtime_outlives_ui_pause_and_hidden_island() {
        let mut module = headless_module("timer-lifecycle");
        module
            .invoke("setSettings", &json!({"customText":"{stopwatch}"}))
            .unwrap();
        module
            .invoke(
                "timerCommand",
                &json!({"kind":"stopwatch","action":"start"}),
            )
            .unwrap();
        assert!(module.state_payload()["timers"]["stopwatch"]["running"]
            .as_bool()
            .unwrap());
        module.invoke("hideTemporarily", &json!({})).unwrap();
        assert_eq!(module.model.state(), IslandState::Dormant);
        assert!(module.timers.running());
        module.invoke("restoreIsland", &json!({})).unwrap();
        assert_ne!(module.model.state(), IslandState::Dormant);
        module.set_active(false);
        assert!(!module.timers.running());
        assert!(module.timers.snapshot(300).stopwatch.started);
        assert!(module
            .invoke(
                "timerCommand",
                &json!({"kind":"stopwatch","action":"start"})
            )
            .is_err());
        module.set_active(true);
        assert!(
            !module.timers.running(),
            "enable must not silently restart a paused timer"
        );
        module
            .invoke(
                "timerCommand",
                &json!({"kind":"countdown","action":"start"}),
            )
            .unwrap();
        module
            .invoke("setSettings", &json!({"countdownSeconds":10}))
            .unwrap();
        assert!(!module.timers.snapshot(10).countdown.started);
        module.hidden_until = Some(Instant::now());
        assert!(module.tick(activity::now_millis()));
        assert!(module.hidden_until.is_none());
    }

    #[test]
    fn slider_previews_apply_live_but_only_a_commit_is_saved() {
        let mut module = headless_module("live-slider");
        let saved = |module: &Module| Settings::load(&module.settings_path).compact_width;
        let before = saved(&module);
        module
            .invoke("previewSettings", &json!({"compactWidth":360,"scale":1.25}))
            .unwrap();
        assert_eq!(module.settings.compact_width, 360);
        assert_eq!(module.settings.scale, 1.25);
        assert_eq!(saved(&module), before, "dragging must not write the file");
        assert!(
            module
                .invoke("previewSettings", &json!({"customText":"no"}))
                .is_err(),
            "only geometry and tint can be previewed"
        );
        module
            .invoke("setSettings", &json!({"compactWidth":360}))
            .unwrap();
        assert_eq!(saved(&module), 360);
        assert_eq!(Settings::load(&module.settings_path).scale, 1.25);
        // The frosted blur previews live too, and stays within its range.
        module
            .invoke("previewSettings", &json!({"frostBlur":99}))
            .unwrap();
        assert_eq!(module.settings.frost_blur, 40);
        assert_eq!(Settings::default().frost_blur, 6);
    }

    #[test]
    fn timers_reach_the_capsule_only_through_placeholders() {
        let mut module = headless_module("timer-text");
        module
            .invoke(
                "setSettings",
                &json!({"showClock":false,"customText":"","peekText":"","expandedText":""}),
            )
            .unwrap();
        for kind in ["stopwatch", "countdown"] {
            module
                .invoke("timerCommand", &json!({"kind":kind,"action":"start"}))
                .unwrap();
        }
        assert!(
            module.model.ambient().is_none(),
            "running timers must not write text the user did not ask for"
        );
        assert_eq!(module.model.state(), IslandState::Dormant);
        module
            .invoke(
                "setSettings",
                &json!({"customText":"计时 {stopwatch} · {stopwatch.state} / {countdown.state}"}),
            )
            .unwrap();
        assert_eq!(
            module.model.ambient().unwrap().text,
            "计时 00:00:00 · 进行中 / 进行中"
        );
        // The retired visibility switches no longer hide or add anything.
        module
            .invoke(
                "setSettings",
                &json!({"showStopwatch":false,"showCountdown":false}),
            )
            .unwrap();
        assert_eq!(
            module.model.ambient().unwrap().text,
            "计时 00:00:00 · 进行中 / 进行中"
        );
        module
            .invoke(
                "timerCommand",
                &json!({"kind":"stopwatch","action":"pause"}),
            )
            .unwrap();
        assert!(module.model.ambient().unwrap().text.contains("已暂停"));
        assert!(module
            .invoke(
                "timerCommand",
                &json!({"kind":"countdown","action":"delete"})
            )
            .is_err());
    }

    #[test]
    fn tasks_fill_placeholders_without_taking_over_the_capsule() {
        let mut module = headless_module("task-text");
        module
            .invoke(
                "setSettings",
                &json!({"showClock":false,"customText":"专注","peekText":"{task|空闲} · {task.state|-}","expandedText":"{tasks.count} 个任务\n{tasks|无}"}),
            )
            .unwrap();
        assert_eq!(module.model.compact_label().as_deref(), Some("专注"));
        assert_eq!(module.model.peek_detail().as_deref(), Some("空闲 · -"));
        module
            .invoke("emitMockActivity", &json!({"scenario":"waiting"}))
            .unwrap();
        module.tick(activity::now_millis());
        assert_eq!(module.model.compact_label().as_deref(), Some("专注"));
        let peek = module.model.peek_detail().unwrap();
        assert!(peek.ends_with("需要你处理"), "{peek}");
        assert!(module
            .model
            .expanded_text()
            .unwrap()
            .starts_with("1 个任务\n"));
        let values = &module.state_payload()["templateValues"];
        assert_eq!(values["task.state"], "需要你处理");
        assert_eq!(values["tasks.count"], "1");
    }

    #[test]
    fn a_full_tick_never_polls_a_provider_twice() {
        // Guards the bug this file was rewritten to fix: polling Codex once to
        // collect ids and again to upsert doubled its interval accounting.
        let mut module = headless_module("full-tick-polls-once");
        let before = module.mock.status().activity_count;
        module.tick(activity::now_millis());
        module.tick(activity::now_millis());
        assert_eq!(
            module.mock.status().activity_count,
            before,
            "a plain tick must not manufacture scripted activities"
        );
    }

    #[test]
    fn the_tick_never_sleeps_longer_than_its_cap() {
        let module = headless_module("tick-sleep-cap");
        assert!(module.tick_sleep(activity::now_millis()) <= MAX_TICK_SLEEP);
    }

    // ---- manifest contract ----

    fn module_manifest() -> Value {
        let raw = include_str!("../module.json");
        serde_json::from_str(raw).expect("module.json must be valid JSON")
    }

    #[test]
    fn the_manifest_declares_every_operation_this_module_answers() {
        // The host rejects an invoke whose method is not in the manifest, so a
        // handler added to `invoke` without a matching manifest entry would be
        // unreachable in production while every unit test still passed.
        let manifest = module_manifest();
        let declared: Vec<String> = manifest["operations"]
            .as_array()
            .expect("operations array")
            .iter()
            .map(|entry| entry.as_str().expect("operation name").to_string())
            .collect();
        let expected: Vec<String> = OPERATIONS.iter().map(|name| (*name).to_string()).collect();
        assert_eq!(
            declared, expected,
            "module.json operations drifted from invoke"
        );
    }

    #[test]
    fn the_manifest_satisfies_the_host_module_contract() {
        // Mirrors the host's `validate_manifest`: a module that fails any of
        // these is rejected outright rather than started.
        let manifest = module_manifest();
        assert_eq!(manifest["id"], crate::paths::MODULE_ID);
        assert_eq!(manifest["apiVersion"], 1);
        assert_eq!(manifest["runtimeType"], "Process");
        assert_eq!(manifest["runtimeIsolation"], "OutOfProcess");
        // "Background" is not a value the host accepts, however apt it sounds.
        assert_eq!(manifest["loadMode"], "Startup");
        assert_eq!(manifest["uiKind"], "Web");
        let entry = manifest["entry"].as_str().expect("entry");
        assert!(
            entry.ends_with(".exe"),
            "entry must be an executable: {entry}"
        );
        let web = manifest["webEntry"].as_str().expect("webEntry");
        assert!(web.ends_with(".html"), "webEntry must be HTML: {web}");
        let allowed = [
            "FileRead",
            "FileWrite",
            "ProcessStart",
            "Network",
            "Clipboard",
            "WindowControl",
        ];
        for permission in manifest["permissions"].as_array().expect("permissions") {
            let name = permission.as_str().expect("permission name");
            assert!(allowed.contains(&name), "unknown permission: {name}");
        }
    }

    #[test]
    fn declared_operations_are_the_only_ones_the_dispatcher_accepts() {
        // The other half of the same contract: nothing the manifest advertises
        // may fall through to `unknown_method`.
        let mut module = headless_module("operation-parity");
        for method in OPERATIONS {
            let result = module.invoke(method, &json!({}));
            if let Err(error) = result {
                assert_ne!(
                    error.code, "unknown_method",
                    "{method} is advertised in the manifest but not dispatched"
                );
            }
        }
    }

    #[test]
    fn disable_stops_providers_and_reenable_keeps_the_process_state() {
        let mut module = headless_module("lifecycle-regression");
        module
            .invoke("emitMockActivity", &json!({"scenario":"waiting"}))
            .unwrap();
        module.set_active(false);
        assert!(!module.active);
        assert!(module.broker.activities().is_empty());
        assert_eq!(
            module.mock.status().health,
            providers::ProviderHealth::Disabled
        );
        assert_eq!(
            module
                .invoke("emitMockActivity", &json!({"scenario":"working"}))
                .unwrap_err()
                .code,
            "inactive"
        );
        module.set_active(true);
        module
            .invoke("emitMockActivity", &json!({"scenario":"working"}))
            .unwrap();
        assert_eq!(module.broker.activities().len(), 1);
    }

    #[test]
    fn failed_save_leaves_settings_and_providers_unchanged() {
        let mut module = headless_module("atomic-settings");
        let old = module.settings.clone();
        let parent = module
            .settings_path
            .parent()
            .unwrap()
            .join("not-a-directory");
        std::fs::write(&parent, b"fixture").unwrap();
        module.settings_path = parent.join("settings.json");
        assert_eq!(
            module
                .invoke("setSettings", &json!({"enabled":false}))
                .unwrap_err()
                .code,
            "persist_failed"
        );
        assert_eq!(module.settings, old);
        assert_eq!(
            module.mock.status().health,
            providers::ProviderHealth::Connected
        );
    }

    #[test]
    fn clearing_does_not_resurrect_provider_cache_on_the_next_tick() {
        let mut module = headless_module("clear-cache-regression");
        module
            .invoke("emitMockActivity", &json!({"scenario":"all"}))
            .unwrap();
        module.invoke("clearActivities", &json!({})).unwrap();
        module.tick(activity::now_millis());
        assert!(module.broker.activities().is_empty());
    }
}
