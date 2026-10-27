//! Slow work never occupies the module's serial host request loop. The host
//! releases its runtime lock after a short task receipt; UI polls bounded IDs.
use super::*;
use std::sync::{
    atomic::{AtomicBool, AtomicU64, Ordering},
    mpsc, Arc, Mutex,
};
use std::time::{Duration, Instant};

type Outcome = Result<Value, (&'static str, String)>;
struct Task {
    created: Instant,
    outcome: Option<Outcome>,
}
struct Completion {
    id: String,
    outcome: Outcome,
}
enum RuntimeWork {
    Active(bool),
    Invoke(String, String, Value, u64, u64),
    Status,
    Shutdown(mpsc::Sender<()>),
}
struct IconWork {
    id: String,
    path: PathBuf,
    generation: u64,
}

pub struct Background {
    next: u64,
    tasks: HashMap<String, Task>,
    receiver: mpsc::Receiver<Completion>,
    runtime: mpsc::SyncSender<RuntimeWork>,
    icons: mpsc::SyncSender<IconWork>,
    enabled: Arc<AtomicBool>,
    active: Arc<AtomicBool>,
    activation: Arc<AtomicU64>,
    settings: Arc<Mutex<SearchSettings>>,
    status: Arc<Mutex<Value>>,
    status_pending: Arc<AtomicBool>,
    desktop: Option<mpsc::Receiver<Vec<LauncherItem>>>,
    desktop_revision: u64,
    icon_cache: cache::DiskCache,
    query_cache: cache::DiskCache,
    shutdown_sent: bool,
}

impl Background {
    pub fn new(
        store: &LauncherStore,
        module: PathBuf,
        data: PathBuf,
        query_cache: cache::DiskCache,
    ) -> Self {
        let (sender, receiver) = mpsc::channel();
        let (runtime, work) = mpsc::sync_channel(32);
        let (icons, icon_work) = mpsc::sync_channel::<IconWork>(64);
        let enabled = Arc::new(AtomicBool::new(store.cache_enabled));
        let active = Arc::new(AtomicBool::new(false));
        let activation = Arc::new(AtomicU64::new(0));
        let settings = Arc::new(Mutex::new(store.everything_settings));
        let status = Arc::new(Mutex::new(
            json!({"status":"indexing","error":"Everything 正在后台初始化。"}),
        ));
        let status_pending = Arc::new(AtomicBool::new(false));
        let icon_cache = store.icon_cache.clone();
        {
            let enabled = enabled.clone();
            let active = active.clone();
            let activation = activation.clone();
            let settings = settings.clone();
            let status = status.clone();
            let pending = status_pending.clone();
            let sender = sender.clone();
            let query_cache = query_cache.clone();
            let mut worker_store = store.clone();
            std::thread::spawn(move || {
                let mut runtime = EverythingRuntime::new(module, data);
                runtime.use_shared_cache(query_cache.clone());
                if enabled.load(Ordering::Acquire) {
                    let count = query_cache.warm();
                    eprintln!("[startup] Everything query cache warmed: {count} fresh pages (database is separate)");
                }
                while let Ok(command) = work.recv() {
                    runtime.set_cache_enabled(enabled.load(Ordering::Acquire));
                    let current_settings = *settings.lock().unwrap_or_else(|e| e.into_inner());
                    if worker_store.everything_settings != current_settings {
                        runtime.clear_search();
                    }
                    worker_store.everything_settings = current_settings;
                    match command {
                        RuntimeWork::Shutdown(done) => {
                            runtime.shutdown();
                            let _ = done.send(());
                            break;
                        }
                        RuntimeWork::Active(false) => runtime.shutdown(),
                        RuntimeWork::Active(true) | RuntimeWork::Status => {
                            let epoch = activation.load(Ordering::Acquire);
                            if active.load(Ordering::Acquire) {
                                let start = Instant::now();
                                let value = json!(runtime.status());
                                eprintln!(
                                    "[startup] Everything status={} elapsedMs={}",
                                    value["status"],
                                    start.elapsed().as_millis()
                                );
                                if active.load(Ordering::Acquire)
                                    && epoch == activation.load(Ordering::Acquire)
                                {
                                    *status.lock().unwrap_or_else(|e| e.into_inner()) = value;
                                }
                            }
                            pending.store(false, Ordering::Release);
                        }
                        RuntimeWork::Invoke(id, method, payload, epoch, cache_generation) => {
                            runtime.set_cache_enabled(
                                enabled.load(Ordering::Acquire)
                                    && cache_generation == query_cache.generation(),
                            );
                            let mut outcome = if !active.load(Ordering::Acquire)
                                || epoch != activation.load(Ordering::Acquire)
                            {
                                Err(("module_inactive", "请先启用模块。".into()))
                            } else {
                                handle_method(
                                    &method,
                                    payload,
                                    &mut worker_store,
                                    &mut true,
                                    &mut runtime,
                                )
                            };
                            if epoch != activation.load(Ordering::Acquire) {
                                outcome =
                                    Err(("module_inactive", "模块已停用，旧任务已取消。".into()));
                            }
                            let _ = sender.send(Completion { id, outcome });
                        }
                    }
                }
                runtime.shutdown();
            });
        }
        // Two Shell workers: one slow shortcut/network provider must not hold
        // up unrelated metadata, navigation, or the module's lifecycle replies.
        let icon_work = Arc::new(Mutex::new(icon_work));
        for index in 0..2 {
            let input = icon_work.clone();
            let sender = sender.clone();
            let enabled = enabled.clone();
            let cache = icon_cache.clone();
            std::thread::spawn(move || {
                if index == 0 && enabled.load(Ordering::Acquire) {
                    let count = cache.warm();
                    eprintln!("[startup] icon cache warmed: {count} assets");
                }
                loop {
                    let job = input.lock().unwrap_or_else(|e| e.into_inner()).recv();
                    let Ok(IconWork {
                        id,
                        path,
                        generation,
                    }) = job
                    else {
                        break;
                    };
                    let key = cache::icon_key(&path);
                    let cached = enabled
                        .load(Ordering::Acquire)
                        .then(|| key.as_ref().and_then(|key| cache.get::<String>(key)))
                        .flatten()
                        .filter(|url| {
                            url.starts_with("data:image/png;base64,") && url.len() <= 350_000
                        });
                    let data_url = cached.or_else(|| {
                        let url = icon_data_url(&path)?;
                        if enabled.load(Ordering::Acquire) {
                            if let Some(key) = key {
                                cache.put_if_generation(&key, &url, generation);
                            }
                        }
                        Some(url)
                    });
                    let _ = sender.send(Completion {
                        id,
                        outcome: Ok(json!({"dataUrl": data_url})),
                    });
                }
            });
        }
        let mut background = Self {
            next: 0,
            tasks: HashMap::new(),
            receiver,
            runtime,
            icons,
            enabled,
            active,
            activation,
            settings,
            status,
            status_pending,
            desktop: None,
            desktop_revision: 0,
            icon_cache,
            query_cache,
            shutdown_sent: false,
        };
        // One-shot loading of persisted data/cache is allowed while loaded;
        // continuous index work starts only after lifecycle activation.
        background.refresh_desktop();
        background
    }

    pub fn sync_settings(&self, store: &LauncherStore) {
        if self.enabled.swap(store.cache_enabled, Ordering::AcqRel) != store.cache_enabled {
            self.icon_cache.invalidate_memory();
            self.query_cache.invalidate_memory();
        }
        *self.settings.lock().unwrap_or_else(|e| e.into_inner()) = store.everything_settings;
    }
    pub fn activate(&self, active: bool) {
        self.active.store(active, Ordering::Release);
        self.activation.fetch_add(1, Ordering::AcqRel);
        if active {
            *self.status.lock().unwrap_or_else(|e| e.into_inner()) =
                json!({"status":"indexing","error":"Everything 正在后台初始化。"});
            self.status_pending.store(true, Ordering::Release);
        }
        // Invalidate queued/unclaimed results rather than accepting operations
        // from a previous activation. The worker also rechecks the active flag.
        if self.runtime.try_send(RuntimeWork::Active(active)).is_err() {
            self.status_pending.store(false, Ordering::Release);
        }
    }
    pub fn drain(&mut self, store: &mut LauncherStore) {
        while let Ok(done) = self.receiver.try_recv() {
            if let Some(task) = self.tasks.get_mut(&done.id) {
                task.outcome = Some(done.outcome);
            }
        }
        if let Some(receiver) = &self.desktop {
            match receiver.try_recv() {
                Ok(items) => {
                    store.synchronize_desktop(items);
                    self.desktop = None;
                    self.desktop_revision += 1;
                    eprintln!(
                        "[startup] desktop snapshot applied revision={}",
                        self.desktop_revision
                    );
                }
                Err(mpsc::TryRecvError::Disconnected) => {
                    self.desktop = None;
                }
                Err(mpsc::TryRecvError::Empty) => {}
            }
        }
        self.tasks
            .retain(|_, task| task.created.elapsed() < Duration::from_secs(120));
    }
    pub fn startup(&self) -> Value {
        json!({"desktopRefreshing":self.desktop.is_some(),"revision":self.desktop_revision})
    }
    pub fn refresh_desktop(&mut self) {
        if self.desktop.is_some() {
            return;
        }
        let (tx, rx) = mpsc::channel();
        self.desktop = Some(rx);
        std::thread::spawn(move || {
            let start = Instant::now();
            let items = scan_desktop();
            eprintln!(
                "[startup] desktop scanned items={} elapsedMs={}",
                items.len(),
                start.elapsed().as_millis()
            );
            let _ = tx.send(items);
        });
    }
    fn receipt(&mut self) -> Result<String, (&'static str, String)> {
        if self.tasks.len() >= 96 {
            return Err(("background_busy", "后台任务较多，请稍后重试。".into()));
        }
        self.next += 1;
        let id = format!("launcher-task-{}-{}", std::process::id(), self.next);
        self.tasks.insert(
            id.clone(),
            Task {
                created: Instant::now(),
                outcome: None,
            },
        );
        Ok(id)
    }
    pub fn poll(&mut self, payload: &Value) -> Outcome {
        let id = required_string(payload, "taskId")?;
        let task = self
            .tasks
            .get(&id)
            .ok_or(("task_expired", "后台任务已过期，请重试。".into()))?;
        if task.outcome.is_none() {
            return Ok(json!({"launcherTask":id}));
        }
        self.tasks.remove(&id).unwrap().outcome.unwrap()
    }
    pub fn icon(&mut self, payload: &Value, store: &LauncherStore) -> Outcome {
        let id = required_string(payload, "id")?;
        let target = store
            .items
            .iter()
            .chain(store.desktop_items.iter())
            .find(|item| item.id == id)
            .ok_or(("item_not_found", "应用不存在。".into()))?
            .target
            .clone();
        let token = self.receipt()?;
        if self
            .icons
            .try_send(IconWork {
                id: token.clone(),
                path: PathBuf::from(target),
                generation: self.icon_cache.generation(),
            })
            .is_err()
        {
            self.tasks.remove(&token);
            return Err(("background_busy", "图标队列已满，请稍后重试。".into()));
        }
        Ok(json!({"launcherTask":token}))
    }
    pub fn everything(&mut self, method: &str, payload: Value) -> Outcome {
        let token = self.receipt()?;
        if self
            .runtime
            .try_send(RuntimeWork::Invoke(
                token.clone(),
                method.into(),
                payload,
                self.activation.load(Ordering::Acquire),
                self.query_cache.generation(),
            ))
            .is_err()
        {
            self.tasks.remove(&token);
            return Err(("background_busy", "搜索队列已满，请稍后重试。".into()));
        }
        Ok(json!({"launcherTask":token}))
    }
    pub fn status(&self) -> Value {
        if !self.status_pending.swap(true, Ordering::AcqRel)
            && self.runtime.try_send(RuntimeWork::Status).is_err()
        {
            self.status_pending.store(false, Ordering::Release);
        }
        self.status
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .clone()
    }
    pub fn clear_cache(&self) -> Outcome {
        self.icon_cache
            .clear()
            .map_err(|e| ("cache_clear_failed", e.to_string()))?;
        self.query_cache
            .clear()
            .map_err(|e| ("cache_clear_failed", e.to_string()))?;
        Ok(json!({"cleared":true}))
    }

    pub fn shutdown(&mut self) {
        if self.shutdown_sent {
            return;
        }
        self.shutdown_sent = true;
        self.active.store(false, Ordering::Release);
        self.activation.fetch_add(1, Ordering::AcqRel);
        let (tx, rx) = mpsc::channel();
        // Existing queues are bounded. Retry without ever waiting indefinitely
        // on a wedged Shell/provider; all queued invokes now fail closed.
        let until = Instant::now() + Duration::from_secs(5);
        let mut command = RuntimeWork::Shutdown(tx);
        loop {
            match self.runtime.try_send(command) {
                Ok(()) => {
                    let _ = rx.recv_timeout(until.saturating_duration_since(Instant::now()));
                    break;
                }
                Err(mpsc::TrySendError::Full(value)) if Instant::now() < until => {
                    command = value;
                    std::thread::sleep(Duration::from_millis(10));
                }
                _ => {
                    eprintln!("[shutdown] Everything worker cleanup deferred");
                    break;
                }
            }
        }
    }
}
