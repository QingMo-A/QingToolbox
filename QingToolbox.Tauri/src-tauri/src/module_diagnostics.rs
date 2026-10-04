//! Host-owned module diagnostics. Protocol payloads/hello credentials are never
//! logged; stderr is bounded, escaped as text, and rate limited per process.
use crate::SessionLogEntry;
use serde::{Deserialize, Serialize};
use std::{
    collections::VecDeque,
    fs::{self, File, OpenOptions},
    io::{self, BufRead, BufReader, Read, Write},
    path::{Path, PathBuf},
    sync::{mpsc, Arc, Mutex},
    thread,
    time::{Duration, Instant},
};

const LIMIT: usize = 512;
const FILE_LIMIT: u64 = 2 * 1024 * 1024;
const TEXT_LIMIT: usize = 2048;
const STDERR_LINE_LIMIT: usize = 8192;
const STDERR_RATE: usize = 20;
const STDERR_PANIC_RATE: usize = 3;

#[derive(Clone, Copy, Serialize, Deserialize)]
pub(crate) enum Level {
    Information,
    Warning,
    Error,
}

#[derive(Clone, Serialize, Deserialize)]
struct Entry {
    timestamp: String,
    level: Level,
    module_id: String,
    generation: Option<u64>,
    operation: String,
    code: Option<String>,
    message: String,
}

impl Entry {
    fn session(&self) -> SessionLogEntry {
        SessionLogEntry {
            timestamp: self.timestamp.clone(),
            level: match self.level {
                Level::Information => "Information",
                Level::Warning => "Warning",
                Level::Error => "Error",
            }
            .into(),
            category: format!("Module/{}", self.module_id),
            message: format!(
                "{}{}{}: {}",
                self.operation,
                self.generation
                    .map(|n| format!(" [generation={n}]"))
                    .unwrap_or_default(),
                self.code
                    .as_ref()
                    .map(|code| format!(" ({code})"))
                    .unwrap_or_default(),
                self.message,
            ),
        }
    }
}

struct Journal {
    path: PathBuf,
}

impl Journal {
    fn previous(&self) -> PathBuf {
        self.path.with_extension("log.1")
    }

    fn read(&self) -> VecDeque<Entry> {
        let mut entries = VecDeque::new();
        for path in [self.previous(), self.path.clone()] {
            if let Ok(file) = File::open(path) {
                for line in BufReader::new(file.take(FILE_LIMIT))
                    .lines()
                    .map_while(Result::ok)
                {
                    let Ok(mut entry) = serde_json::from_str::<Entry>(&line) else {
                        continue;
                    };
                    // Only host-generated ISO timestamps and bounded text may
                    // enter the existing frontend log DTO after a restart.
                    if !valid_timestamp(&entry.timestamp) {
                        continue;
                    }
                    entry.module_id = clean(&entry.module_id, 128);
                    entry.operation = clean(&entry.operation, 80);
                    entry.code = entry.code.map(|code| clean(&code, 80));
                    entry.message = clean(&entry.message, TEXT_LIMIT);
                    entries.push_back(entry);
                    if entries.len() > LIMIT {
                        entries.pop_front();
                    }
                }
            }
        }
        entries
    }

    fn append(&self, entry: &Entry) -> io::Result<()> {
        let line = serde_json::to_vec(entry)?;
        if let Some(parent) = self.path.parent() {
            fs::create_dir_all(parent)?;
        }
        if fs::metadata(&self.path)
            .is_ok_and(|meta| meta.len() + line.len() as u64 + 1 > FILE_LIMIT)
        {
            let previous = self.previous();
            if previous.exists() {
                fs::remove_file(&previous)?;
            }
            fs::rename(&self.path, previous)?;
        }
        let mut file = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&self.path)?;
        file.write_all(&line)?;
        file.write_all(b"\n")
    }
}

enum WriteRequest {
    Entry(Entry),
    Flush(mpsc::SyncSender<()>),
}

pub(crate) struct Diagnostics {
    entries: Arc<Mutex<VecDeque<Entry>>>,
    writer: Option<mpsc::SyncSender<WriteRequest>>,
}

impl Diagnostics {
    pub(crate) fn new(profile: Option<&Path>) -> Self {
        #[cfg(test)]
        let profile = {
            let _ = profile;
            None::<&Path>
        };
        Self::with_journal(profile.map(|root| Journal {
            path: root.join("Logs").join("modules.log"),
        }))
    }

    fn with_journal(journal: Option<Journal>) -> Self {
        let entries = Arc::new(Mutex::new(
            journal.as_ref().map(Journal::read).unwrap_or_default(),
        ));
        let writer = journal.map(|journal| {
            let (sender, receiver) = mpsc::sync_channel::<WriteRequest>(LIMIT);
            let entries = Arc::clone(&entries);
            thread::spawn(move || {
                let mut warned = false;
                while let Ok(request) = receiver.recv() {
                    match request {
                        WriteRequest::Entry(entry) => {
                            if journal.append(&entry).is_err() && !warned {
                                warned = true;
                                push(&entries, Entry {
                                    timestamp: crate::now_rfc3339(), level: Level::Warning,
                                    module_id: "host".into(), generation: None, operation: "logStorage".into(),
                                    code: Some("storageUnavailable".into()),
                                    message: "Module log could not be saved; memory logging remains available.".into(),
                                });
                            }
                        }
                        WriteRequest::Flush(ack) => { let _ = ack.send(()); }
                    }
                }
            });
            sender
        });
        Self { entries, writer }
    }

    pub(crate) fn record(
        &self,
        level: Level,
        module_id: &str,
        generation: Option<u64>,
        operation: &str,
        code: Option<&str>,
        message: &str,
    ) {
        let entry = Entry {
            timestamp: crate::now_rfc3339(),
            level,
            module_id: clean(module_id, 128),
            generation,
            operation: clean(operation, 80),
            code: code.map(|code| clean(code, 80)),
            message: clean(message, TEXT_LIMIT),
        };
        push(&self.entries, entry.clone());
        if let Some(writer) = &self.writer {
            // Never block the UI/process pipe on slow disk I/O.
            if writer.try_send(WriteRequest::Entry(entry)).is_err() {
                let mut entries = self.entries.lock().unwrap_or_else(|e| e.into_inner());
                if !entries
                    .iter()
                    .rev()
                    .take(16)
                    .any(|last| last.operation == "logQueueFull")
                {
                    if entries.len() >= LIMIT {
                        entries.pop_front();
                    }
                    entries.push_back(Entry {
                        timestamp: crate::now_rfc3339(),
                        level: Level::Warning,
                        module_id: "host".into(),
                        generation: None,
                        operation: "logQueueFull".into(),
                        code: Some("entriesDropped".into()),
                        message:
                            "Module log persistence queue is full; some disk entries were dropped."
                                .into(),
                    });
                }
            }
        }
    }

    pub(crate) fn snapshot(&self) -> Vec<SessionLogEntry> {
        self.entries
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .iter()
            .map(Entry::session)
            .collect()
    }

    pub(crate) fn flush(&self) {
        if let Some(writer) = &self.writer {
            let (sender, receiver) = mpsc::sync_channel(1);
            if writer.try_send(WriteRequest::Flush(sender)).is_ok() {
                let _ = receiver.recv_timeout(Duration::from_millis(500));
            }
        }
    }
}

fn push(entries: &Mutex<VecDeque<Entry>>, entry: Entry) {
    let mut entries = entries.lock().unwrap_or_else(|e| e.into_inner());
    entries.push_back(entry);
    if entries.len() > LIMIT {
        entries.pop_front();
    }
}

fn clean(text: &str, limit: usize) -> String {
    // Do not retain host nonces, bearer/session credentials, or credential
    // assignments accidentally echoed by an untrusted module.
    let lower = text.to_ascii_lowercase();
    let assignments = lower.replace([' ', '"'], "");
    if [
        "nonce:",
        "token:",
        "password:",
        "secret:",
        "apikey:",
        "apikey=",
    ]
    .iter()
    .any(|marker| assignments.contains(marker))
    {
        return "[credential-bearing output omitted]".into();
    }
    if [
        "authorization:",
        "bearer ",
        "nonce=",
        "nonce:",
        "\"nonce\"",
        "token=",
        "token:",
        "\"token\"",
        "password=",
        "password:",
        "secret=",
        "cookie:",
    ]
    .iter()
    .any(|marker| lower.contains(marker))
    {
        return "[credential-bearing output omitted]".into();
    }
    let mut result = String::new();
    let mut hex = String::new();
    let flush = |result: &mut String, hex: &mut String| {
        if hex.len() >= 32 {
            result.push_str("[redacted]");
        } else {
            result.push_str(hex);
        }
        hex.clear();
    };
    for c in text.chars().take(limit) {
        if c.is_ascii_hexdigit() {
            hex.push(c);
        } else {
            flush(&mut result, &mut hex);
            if c.is_control() {
                result.push(' ');
            } else {
                result.push(c);
            }
        }
    }
    flush(&mut result, &mut hex);
    if text.chars().count() > limit {
        result.push_str(" …[truncated]");
    }
    result
}

fn valid_timestamp(text: &str) -> bool {
    let bytes = text.as_bytes();
    if bytes.len() != 24 {
        return false;
    }
    for (i, byte) in bytes.iter().enumerate() {
        let valid = match i {
            4 | 7 => *byte == b'-',
            10 => *byte == b'T',
            13 | 16 => *byte == b':',
            19 => *byte == b'.',
            23 => *byte == b'Z',
            _ => byte.is_ascii_digit(),
        };
        if !valid {
            return false;
        }
    }
    let number = |range: std::ops::Range<usize>| text[range].parse::<u32>().unwrap_or(0);
    let year = number(0..4);
    let month = number(5..7);
    let day = number(8..10);
    let days = match month {
        1 | 3 | 5 | 7 | 8 | 10 | 12 => 31,
        4 | 6 | 9 | 11 => 30,
        2 if year % 4 == 0 && (year % 100 != 0 || year % 400 == 0) => 29,
        2 => 28,
        _ => 0,
    };
    year >= 1970
        && day >= 1
        && day <= days
        && number(11..13) < 24
        && number(14..16) < 60
        && number(17..19) < 60
}

struct StderrCapture {
    diagnostics: Arc<Diagnostics>,
    module_id: String,
    generation: u64,
    window: Instant,
    count: usize,
    panic_count: usize,
    dropped: usize,
}

impl StderrCapture {
    fn summary(&mut self) {
        if self.dropped != 0 {
            self.diagnostics.record(
                Level::Warning,
                &self.module_id,
                Some(self.generation),
                "stderr",
                Some("rateLimited"),
                &format!("{} additional stderr lines suppressed.", self.dropped),
            );
            self.dropped = 0;
        }
    }
    fn line(&mut self, bytes: &[u8], truncated: bool) {
        if self.window.elapsed() >= Duration::from_secs(1) {
            self.summary();
            self.window = Instant::now();
            self.count = 0;
            self.panic_count = 0;
        }
        let mut text = String::from_utf8_lossy(bytes).trim().to_string();
        if text.is_empty() {
            return;
        }
        let panicked = text.to_ascii_lowercase().contains("panic");
        if (self.count >= STDERR_RATE && !panicked)
            || (panicked && self.panic_count >= STDERR_PANIC_RATE)
        {
            self.dropped = self.dropped.saturating_add(1);
            return;
        }
        self.count += 1;
        self.panic_count += usize::from(panicked);
        if truncated {
            text.push_str(" …[line truncated]");
        }
        let level = if panicked {
            Level::Error
        } else {
            Level::Warning
        };
        self.diagnostics.record(
            level,
            &self.module_id,
            Some(self.generation),
            "stderr",
            None,
            &text,
        );
    }
}

pub(crate) fn capture_stderr(
    pipe: Option<impl Read + Send + 'static>,
    diagnostics: Arc<Diagnostics>,
    module_id: String,
    generation: u64,
) {
    if let Some(pipe) = pipe {
        thread::spawn(move || read_stderr(pipe, diagnostics, module_id, generation));
    }
}

fn read_stderr(pipe: impl Read, diagnostics: Arc<Diagnostics>, module_id: String, generation: u64) {
    let mut capture = StderrCapture {
        diagnostics,
        module_id,
        generation,
        window: Instant::now(),
        count: 0,
        panic_count: 0,
        dropped: 0,
    };
    let mut reader = BufReader::new(pipe);
    let mut line = Vec::with_capacity(STDERR_LINE_LIMIT);
    let mut truncated = false;
    loop {
        let buffer = match reader.fill_buf() {
            Ok(buffer) => buffer,
            Err(error) => {
                capture.diagnostics.record(
                    Level::Error,
                    &capture.module_id,
                    Some(generation),
                    "stderr",
                    Some("readFailed"),
                    &error.to_string(),
                );
                break;
            }
        };
        if buffer.is_empty() {
            if !line.is_empty() {
                capture.line(&line, truncated);
            }
            break;
        }
        let newline = buffer.iter().position(|byte| *byte == b'\n');
        let consumed = newline.map(|i| i + 1).unwrap_or(buffer.len());
        let retained = consumed.min(STDERR_LINE_LIMIT.saturating_sub(line.len()));
        line.extend_from_slice(&buffer[..retained]);
        truncated |= retained != consumed;
        reader.consume(consumed);
        if newline.is_some() {
            capture.line(&line, truncated);
            line.clear();
            truncated = false;
        }
    }
    capture.summary();
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn corrupt_timestamps_cannot_break_the_frontend_log_contract() {
        assert!(valid_timestamp(&crate::now_rfc3339()));
        assert!(valid_timestamp("2024-02-29T23:59:59.999Z"));
        for text in [
            "2026-02-29T01:00:00.000Z",
            "2026-10-04T25:00:00.000Z",
            "xxxx-10-04T01:00:00.000Z",
            "invalid",
        ] {
            assert!(!valid_timestamp(text));
        }
    }

    #[test]
    fn stderr_is_bounded_rate_limited_and_keeps_module_identity() {
        let diagnostics = Arc::new(Diagnostics::new(None));
        let source = format!(
            "{}\n{}panic on exit",
            "x".repeat(100_000),
            "warning\n".repeat(30)
        );
        read_stderr(
            source.as_bytes(),
            Arc::clone(&diagnostics),
            "qing.launcher".into(),
            7,
        );
        let entries = diagnostics.snapshot();
        assert!(entries.len() <= STDERR_RATE + STDERR_PANIC_RATE + 1);
        assert!(entries
            .iter()
            .all(|entry| entry.category == "Module/qing.launcher"));
        assert!(entries[0].message.contains("truncated"));
        assert!(entries.last().unwrap().message.contains("rateLimited"));
        assert!(entries
            .iter()
            .any(|entry| entry.level == "Error" && entry.message.contains("panic on exit")));
        assert!(entries.iter().all(|entry| entry.message.len() < 3000));
    }

    #[test]
    fn captures_unicode_panic_and_final_line_without_newline() {
        let diagnostics = Arc::new(Diagnostics::new(None));
        read_stderr(
            "错误：无法启动\nthread panicked: test".as_bytes(),
            Arc::clone(&diagnostics),
            "qing.launcher".into(),
            2,
        );
        let entries = diagnostics.snapshot();
        assert_eq!(entries.len(), 2);
        assert!(entries[0].message.contains("错误：无法启动"));
        assert_eq!(entries[1].level, "Error");
        assert!(entries[1].message.contains("generation=2"));
    }

    #[test]
    fn credentials_and_control_characters_do_not_enter_logs() {
        assert!(!clean(&"a".repeat(64), TEXT_LIMIT).contains(&"a".repeat(64)));
        assert_eq!(
            clean("Authorization: Bearer secret", TEXT_LIMIT),
            "[credential-bearing output omitted]"
        );
        assert_eq!(
            clean("token=private", TEXT_LIMIT),
            "[credential-bearing output omitted]"
        );
        assert_eq!(
            clean("{\"sessionToken\": \"private-uuid\"}", TEXT_LIMIT),
            "[credential-bearing output omitted]"
        );
        assert_eq!(
            clean("first\r\nsecond\u{1b}[31m", TEXT_LIMIT),
            "first  second [31m"
        );
    }

    #[test]
    fn memory_snapshot_is_bounded() {
        let diagnostics = Diagnostics::new(None);
        for n in 0..LIMIT + 5 {
            diagnostics.record(
                Level::Information,
                "qing.test",
                Some(1),
                "load",
                None,
                &format!("event-{n}"),
            );
        }
        assert_eq!(diagnostics.snapshot().len(), LIMIT);
        assert!(diagnostics.snapshot()[0].message.ends_with("event-5"));
    }

    #[test]
    fn file_journal_survives_restart_and_rotates_at_its_limit() {
        let root = std::env::temp_dir().join(format!(
            "qing-module-log-test-{}-{}",
            std::process::id(),
            crate::now_rfc3339().replace([':', '.'], "-")
        ));
        fs::create_dir_all(&root).unwrap();
        let path = root.join("modules.log");
        let diagnostics = Diagnostics::with_journal(Some(Journal { path: path.clone() }));
        diagnostics.record(
            Level::Error,
            "qing.launcher",
            Some(3),
            "activate",
            Some("hotkeyUnavailable"),
            "Shortcut registration failed.",
        );
        diagnostics.flush();
        let restored = Diagnostics::with_journal(Some(Journal { path: path.clone() }));
        assert!(restored.snapshot()[0].message.contains("hotkeyUnavailable"));
        let journal = Journal { path: path.clone() };
        File::create(&path).unwrap().set_len(FILE_LIMIT).unwrap();
        let entry = diagnostics.entries.lock().unwrap()[0].clone();
        journal.append(&entry).unwrap();
        assert!(journal.previous().exists());
        assert!(fs::metadata(&path).unwrap().len() < FILE_LIMIT);
        drop(restored);
        drop(diagnostics);
        assert!(root.starts_with(std::env::temp_dir()));
        fs::remove_dir_all(root).unwrap();
    }
}
