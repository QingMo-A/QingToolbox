//! Disposable, bounded caches; never store user configuration in this directory.
use serde::{de::DeserializeOwned, Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    fs, io,
    path::{Path, PathBuf},
    time::{SystemTime, UNIX_EPOCH},
};

#[derive(Debug)]
pub struct DiskCache {
    directory: PathBuf,
    ttl: u64,
    budget: u64,
    entries: usize,
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
            ttl: if kind == "icons" { 600 } else { 60 },
            budget: if kind == "icons" {
                64 * 1024 * 1024
            } else {
                16 * 1024 * 1024
            },
            entries: if kind == "icons" { 512 } else { 64 },
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
        if !self.safe() {
            return None;
        }
        let path = self.path(key);
        let metadata = fs::symlink_metadata(&path).ok()?;
        if !metadata.is_file()
            || metadata.file_type().is_symlink()
            || metadata.len() > 2 * 1024 * 1024
        {
            return None;
        }
        let entry: Entry<T> = serde_json::from_slice(&fs::read(&path).ok()?).ok()?;
        if entry.created > now() || now().saturating_sub(entry.created) >= self.ttl {
            return None;
        }
        Some(entry.value)
    }

    pub fn put<T: Serialize>(&self, key: &str, value: &T) {
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
        for (path, _) in self.files()? {
            fs::remove_file(path)?;
        }
        Ok(())
    }
}

pub fn icon_key(path: &Path) -> Option<String> {
    let metadata = fs::metadata(path).ok()?;
    Some(format!(
        "icon256-v1:{}:{}:{}",
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
        fs::write(&icon, "longer").unwrap();
        assert_ne!(before, icon_key(&icon).unwrap());
        fs::remove_dir_all(&dir).unwrap();
    }
}
