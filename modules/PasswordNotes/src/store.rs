use crate::{
    model::{Error, Result, Vault},
    platform,
};
use std::{
    fs::{self, OpenOptions},
    io::{Read, Write},
    path::{Path, PathBuf},
};
use zeroize::Zeroizing;

const HEADER: &[u8] = b"QPN1\0";
const MAX_BYTES: u64 = 8 * 1024 * 1024;
pub struct Store {
    pub root: PathBuf,
}

fn regular(path: &Path) -> Result<()> {
    match fs::symlink_metadata(path) {
        Ok(metadata) => {
            #[cfg(windows)]
            {
                use std::os::windows::fs::MetadataExt;
                if metadata.file_attributes() & 0x400 != 0 {
                    return Err(Error::new("unsafePath", "数据路径不能为链接或重解析点。"));
                }
            }
            if metadata.file_type().is_symlink() || !metadata.is_file() {
                return Err(Error::new("unsafePath", "数据路径不是普通文件。"));
            }
            Ok(())
        }
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(_) => Err(Error::new("readFailed", "无法检查数据文件。")),
    }
}
pub fn atomic_write(path: &Path, bytes: &[u8]) -> Result<()> {
    let parent = path
        .parent()
        .ok_or_else(|| Error::new("invalidPath", "保存目录无效。"))?;
    regular(path)?;
    let mut random = [0_u8; 12];
    getrandom::fill(&mut random)
        .map_err(|_| Error::new("randomUnavailable", "无法创建保存会话。"))?;
    let token: String = random.iter().map(|v| format!("{v:02x}")).collect();
    let temporary = parent.join(format!(".qing-passwordnotes-{token}.tmp"));
    let result = (|| {
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)
            .map_err(|_| Error::new("writeFailed", "无法创建保存文件，请检查目录权限。"))?;
        file.write_all(bytes)
            .and_then(|_| file.sync_all())
            .map_err(|_| Error::new("writeFailed", "写入失败，请检查磁盘空间。"))?;
        drop(file);
        platform::replace_file(&temporary, path)
    })();
    if result.is_err() {
        let _ = fs::remove_file(temporary);
    }
    result
}
impl Store {
    fn path(&self) -> PathBuf {
        self.root.join("vault.dpapi")
    }
    pub fn load(&self) -> Result<Vault> {
        regular(&self.path())?;
        let file = match fs::File::open(self.path()) {
            Ok(v) => v,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(Vault::default()),
            Err(_) => {
                return Err(Error::new(
                    "readFailed",
                    "记事本无法读取；不会创建空文件覆盖原数据。",
                ))
            }
        };
        let mut bytes = Vec::new();
        file.take(MAX_BYTES + 1)
            .read_to_end(&mut bytes)
            .map_err(|_| Error::new("readFailed", "记事本读取失败。"))?;
        if bytes.len() as u64 > MAX_BYTES || !bytes.starts_with(HEADER) {
            return Err(Error::new(
                "vaultInvalid",
                "数据格式无效或过大；原数据不会被覆盖。",
            ));
        }
        let clear = platform::protect(&bytes[HEADER.len()..], true)?;
        let vault: Vault = serde_json::from_slice(&clear).map_err(|_| {
            Error::new(
                "vaultInvalid",
                "记事本内容已损坏或版本不受支持；请保留原文件和加密备份。",
            )
        })?;
        vault.validate()?;
        Ok(vault)
    }
    pub fn save(&self, vault: &Vault) -> Result<()> {
        vault.validate()?;
        fs::create_dir_all(&self.root)
            .map_err(|_| Error::new("writeFailed", "无法创建模块的数据目录。"))?;
        let clear = Zeroizing::new(
            serde_json::to_vec(vault).map_err(|_| Error::new("writeFailed", "数据序列化失败。"))?,
        );
        if clear.len() as u64 > MAX_BYTES / 2 {
            return Err(Error::new("vaultTooLarge", "记事本数据过大。"));
        }
        let encrypted = platform::protect(&clear, false)?;
        let mut bytes = HEADER.to_vec();
        bytes.extend_from_slice(&encrypted);
        if self.path().exists() {
            regular(&self.path())?;
            let backup = self.root.join("vault.dpapi.bak");
            regular(&backup)?;
            let old = fs::read(self.path()).map_err(|_| {
                Error::new(
                    "backupFailed",
                    "无法读取旧数据进行加密备份，未保存本次修改。",
                )
            })?;
            atomic_write(&backup, &old)?;
        }
        atomic_write(&self.path(), &bytes)
    }
    pub fn export(&self, vault: &Vault, path: &Path, category_id: Option<&str>) -> Result<()> {
        if !path.is_absolute()
            || path
                .extension()
                .and_then(|v| v.to_str())
                .map_or(true, |v| !v.eq_ignore_ascii_case("txt"))
            || path.to_string_lossy().contains('\0')
        {
            return Err(Error::new("invalidPath", "请选择绝对路径的 TXT 保存位置。"));
        }
        #[cfg(windows)]
        {
            if path.components().any(|v| matches!(v,std::path::Component::Normal(s) if s.to_string_lossy().contains(':'))) { return Err(Error::new("invalidPath", "不允许写入 NTFS 附加数据流。")); }
        }
        let clear = Zeroizing::new(vault.text(category_id)?);
        let mut bytes = Zeroizing::new(vec![0xef, 0xbb, 0xbf]);
        bytes.extend_from_slice(clear.as_bytes());
        atomic_write(path, &bytes)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn test_root() -> PathBuf {
        let root = std::env::temp_dir().join(format!(
            "qing-passwordnotes-tests-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(&root).unwrap();
        root
    }
    #[test]
    #[cfg(windows)]
    fn encrypted_persistence_and_backup_roundtrip() {
        let root = test_root();
        let store = Store { root: root.clone() };
        let mut vault = Vault::default();
        vault.save_category(None, "游戏").unwrap();
        store.save(&vault).unwrap();
        vault.save_category(None, "网站").unwrap();
        store.save(&vault).unwrap();
        assert_eq!(store.load().unwrap().categories.len(), 2);
        assert!(root.join("vault.dpapi.bak").is_file());
        fs::write(store.path(), b"bad").unwrap();
        assert!(store.load().is_err());
        assert_eq!(fs::read(store.path()).unwrap(), b"bad");
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn invalid_export_never_writes() {
        let root = test_root();
        let store = Store { root: root.clone() };
        assert!(store
            .export(&Vault::default(), &root.join("bad.exe"), None)
            .is_err());
        assert!(!root.join("bad.exe").exists());
        fs::remove_dir_all(root).unwrap();
    }
}
