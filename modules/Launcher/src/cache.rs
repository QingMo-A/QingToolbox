//! Disposable, bounded caches; never store user configuration in this directory.
use serde::{de::DeserializeOwned, Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::HashMap,
    fs, io,
    path::{Path, PathBuf},
    sync::{Arc, Mutex},
    time::{SystemTime, UNIX_EPOCH},
};

#[derive(Debug, Clone)]
pub struct DiskCache {
    directory: PathBuf,
    ttl: u64,
    budget: u64,
    entries: usize,
    memory: Arc<Mutex<Memory>>,
    io: Arc<Mutex<()>>,
}

#[derive(Debug, Default)]
struct Memory {
    generation: u64,
    bytes: usize,
    values: HashMap<PathBuf, Arc<Vec<u8>>>,
}

#[derive(Serialize, Deserialize)]
struct Entry<T> {
    created: u64,
    value: T,
}

fn now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}
fn plain_directory(path: &Path) -> bool {
    fs::symlink_metadata(path).is_ok_and(|m| {
        #[cfg(windows)]
        {
            use std::os::windows::fs::MetadataExt;
            m.is_dir() && m.file_attributes() & 0x400 == 0
        }
        #[cfg(not(windows))]
        {
            m.is_dir() && !m.file_type().is_symlink()
        }
    })
}

impl DiskCache {
    pub fn new(data: &Path, kind: &str) -> Self {
        assert!(matches!(kind, "icons" | "everything"));
        Self {
            directory: data.join("launcher-cache-v1").join(kind),
            // A reboot must not throw away unchanged Shell icons. Their keys
            // already include source size/mtime and the extraction version.
            // Query pages retain the short freshness window (not the index DB).
            ttl: if kind == "icons" {
                30 * 24 * 60 * 60
            } else {
                60
            },
            budget: if kind == "icons" {
                64 * 1024 * 1024
            } else {
                16 * 1024 * 1024
            },
            entries: if kind == "icons" { 512 } else { 64 },
            memory: Arc::default(),
            io: Arc::default(),
        }
    }

    fn safe(&self) -> bool {
        self.directory.parent().is_some_and(plain_directory) && plain_directory(&self.directory)
    }

    fn path(&self, key: &str) -> PathBuf {
        self.directory
            .join(format!("{:x}.json", Sha256::digest(key.as_bytes())))
    }

    pub fn get<T: DeserializeOwned>(&self, key: &str) -> Option<T> {
        let path = self.path(key);
        let bytes = self
            .memory
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .values
            .get(&path)
            .cloned();
        if let Some(bytes) = bytes {
            if let Some(value) = self.decode(&bytes) {
                return Some(value);
            }
            let mut memory = self.memory.lock().unwrap_or_else(|e| e.into_inner());
            if let Some(old) = memory.values.remove(&path) {
                memory.bytes = memory.bytes.saturating_sub(old.len());
            }
        }
        let _io = self.io.lock().unwrap_or_else(|e| e.into_inner());
        if !self.safe() {
            return None;
        }
        let metadata = fs::symlink_metadata(&path).ok()?;
        if !metadata.is_file()
            || metadata.file_type().is_symlink()
            || metadata.len() > 2 * 1024 * 1024
        {
            return None;
        }
        let bytes = fs::read(&path).ok()?;
        let value = self.decode(&bytes)?;
        self.remember(path, bytes);
        Some(value)
    }

    fn decode<T: DeserializeOwned>(&self, bytes: &[u8]) -> Option<T> {
        let entry: Entry<T> = serde_json::from_slice(bytes).ok()?;
        if entry.created > now() || now().saturating_sub(entry.created) >= self.ttl {
            return None;
        }
        Some(entry.value)
    }

    fn remember(&self, path: PathBuf, bytes: Vec<u8>) {
        let mut memory = self.memory.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(old) = memory.values.remove(&path) {
            memory.bytes = memory.bytes.saturating_sub(old.len());
        }
        // Memory is a disposable acceleration layer, bounded independently of disk.
        while !memory.values.is_empty()
            && (memory.values.len() >= self.entries
                || memory.bytes + bytes.len() > self.budget as usize)
        {
            let oldest = memory.values.keys().next().cloned().unwrap();
            if let Some(old) = memory.values.remove(&oldest) {
                memory.bytes = memory.bytes.saturating_sub(old.len());
            }
        }
        memory.bytes += bytes.len();
        memory.values.insert(path, Arc::new(bytes));
    }

    /// Called only on background workers, before a WebView requests any icons.
    pub fn warm(&self) -> usize {
        let _io = self.io.lock().unwrap_or_else(|e| e.into_inner());
        let mut files = self.files().unwrap_or_default();
        files.sort_by_key(|(_, m)| std::cmp::Reverse(m.modified().ok()));
        let mut count = 0;
        let mut total = 0;
        for (path, metadata) in files {
            if count >= self.entries
                || total + metadata.len() > self.budget
                || metadata.len() > 2 * 1024 * 1024
            {
                continue;
            }
            if let Ok(bytes) = fs::read(&path) {
                if self.decode::<serde_json::Value>(&bytes).is_some() {
                    total += metadata.len();
                    self.remember(path, bytes);
                    count += 1;
                }
            }
        }
        count
    }

    pub fn generation(&self) -> u64 {
        self.memory
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .generation
    }

    pub fn invalidate_memory(&self) {
        let mut memory = self.memory.lock().unwrap_or_else(|e| e.into_inner());
        memory.generation += 1;
        memory.bytes = 0;
        memory.values.clear();
    }

    pub fn put<T: Serialize>(&self, key: &str, value: &T) {
        self.put_if_generation(key, value, self.generation());
    }

    pub fn put_if_generation<T: Serialize>(&self, key: &str, value: &T, generation: u64) {
        let _io = self.io.lock().unwrap_or_else(|e| e.into_inner());
        if self.generation() != generation {
            return;
        }
        // Cache failures must not prevent searching, opening apps or reading icons.
        if let Err(error) = self.write(key, value) {
            eprintln!("Launcher cache write skipped: {error}");
        }
    }

    fn write<T: Serialize>(&self, key: &str, value: &T) -> io::Result<()> {
        let root = self.directory.parent().unwrap();
        fs::create_dir_all(root)?;
        if !plain_directory(root) {
            return Err(io::Error::other("cache root is not a plain directory"));
        }
        fs::create_dir_all(&self.directory)?;
        if !self.safe() {
            return Err(io::Error::other("cache directory is not a plain directory"));
        }
        let bytes = serde_json::to_vec(&Entry {
            created: now(),
            value,
        })?;
        if bytes.len() > 2 * 1024 * 1024 {
            return Ok(());
        }
        let path = self.path(key);
        let temporary = path.with_extension("tmp");
        // Do not follow replacement links. Only our hash-named regular files are owned.
        for file in [&path, &temporary] {
            if fs::symlink_metadata(file).is_ok() {
                fs::remove_file(file)?;
            }
        }
        use std::io::Write;
        let mut file = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)?;
        file.write_all(&bytes)?;
        drop(file);
        fs::rename(&temporary, &path)?;
        self.remember(path, bytes);
        let mut entries = self.files()?;
        entries.sort_by_key(|(_, m)| m.modified().ok());
        let mut total: u64 = entries.iter().map(|(_, m)| m.len()).sum();
        let mut count = entries.len();
        for (path, m) in entries {
            if total <= self.budget && count <= self.entries {
                break;
            }
            fs::remove_file(path)?;
            total = total.saturating_sub(m.len());
            count -= 1;
        }
        Ok(())
    }

    fn files(&self) -> io::Result<Vec<(PathBuf, fs::Metadata)>> {
        if !self.directory.exists() {
            return Ok(vec![]);
        }
        if !self.safe() {
            return Err(io::Error::other("unsafe cache directory"));
        }
        let mut files = vec![];
        for entry in fs::read_dir(&self.directory)? {
            let entry = entry?;
            let name = entry.file_name().to_string_lossy().into_owned();
            let (stem, extension) = name.rsplit_once('.').unwrap_or_default();
            if stem.len() != 64
                || !stem.bytes().all(|b| b.is_ascii_hexdigit())
                || !matches!(extension, "json" | "tmp")
            {
                continue;
            }
            let metadata = fs::symlink_metadata(entry.path())?;
            if metadata.is_file() && !metadata.file_type().is_symlink() {
                files.push((entry.path(), metadata));
            }
        }
        Ok(files)
    }

    pub fn clear(&self) -> io::Result<()> {
        let _io = self.io.lock().unwrap_or_else(|e| e.into_inner());
        {
            let mut memory = self.memory.lock().unwrap_or_else(|e| e.into_inner());
            memory.generation += 1;
            memory.bytes = 0;
            memory.values.clear();
        }
        for (path, _) in self.files()? {
            fs::remove_file(path)?;
        }
        Ok(())
    }
}

pub fn icon_key(path: &Path) -> Option<String> {
    let metadata = fs::metadata(path).ok()?;
    Some(format!(
        "icon-detail-v2:{}:{}:{}",
        super::identity_key(&path.to_string_lossy()),
        metadata.len(),
        metadata
            .modified()
            .ok()?
            .duration_since(UNIX_EPOCH)
            .ok()?
            .as_nanos()
    ))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn restart_ttl_corruption_and_clear_are_disposable() {
        let dir = std::env::temp_dir().join(super::super::unique_id("launcher-cache-test"));
        let mut cache = DiskCache::new(&dir, "everything");
        cache.put("query", &vec!["synthetic"]);
        let reopened = DiskCache::new(&dir, "everything");
        assert_eq!(reopened.get::<Vec<String>>("query").unwrap(), ["synthetic"]);
        cache.ttl = 0;
        assert!(cache.get::<Vec<String>>("query").is_none());
        // Corruption is observed on the next disk read, not through an already
        // warmed immutable in-memory copy.
        reopened.memory.lock().unwrap().values.clear();
        fs::write(reopened.path("query"), b"broken").unwrap();
        assert!(reopened.get::<Vec<String>>("query").is_none());
        fs::write(dir.join("launcher.json"), "keep").unwrap();
        reopened.clear().unwrap();
        assert_eq!(
            fs::read_to_string(dir.join("launcher.json")).unwrap(),
            "keep"
        );
        fs::remove_dir_all(&dir).unwrap();
    }
    #[test]
    fn cache_is_bounded_and_icon_changes_invalidate_keys() {
        let dir = std::env::temp_dir().join(super::super::unique_id("launcher-cache-test"));
        let mut cache = DiskCache::new(&dir, "icons");
        cache.entries = 2;
        for key in ["a", "b", "c"] {
            cache.put(key, &"fixture");
        }
        assert_eq!(cache.files().unwrap().len(), 2);
        let icon = dir.join("fixture.url");
        fs::write(&icon, "a").unwrap();
        let before = icon_key(&icon).unwrap();
        assert!(before.starts_with("icon-detail-v2:"));
        fs::write(&icon, "longer").unwrap();
        assert_ne!(before, icon_key(&icon).unwrap());
        fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn warm_survives_reboot_age_and_clear_rejects_inflight_repopulation() {
        let dir = std::env::temp_dir().join(super::super::unique_id("launcher-cache-test"));
        let cache = DiskCache::new(&dir, "icons");
        cache.put("icon", &"data:image/png;base64,fixture");
        let bytes = serde_json::to_vec(&Entry {
            created: now() - 24 * 3600,
            value: "data:image/png;base64,fixture",
        })
        .unwrap();
        fs::write(cache.path("icon"), bytes).unwrap();
        let reopened = DiskCache::new(&dir, "icons");
        assert_eq!(reopened.warm(), 1);
        assert_eq!(reopened.memory.lock().unwrap().values.len(), 1);
        let epoch = reopened.generation();
        reopened.clear().unwrap();
        reopened.put_if_generation("icon", &"obsolete", epoch);
        assert!(reopened.get::<String>("icon").is_none());
        assert!(reopened.memory.lock().unwrap().values.is_empty());
        fs::remove_dir_all(dir).unwrap();
    }
}
