//! Resolve the current Shell desktop, including redirected and public folders.
//! Never append "Desktop" to USERPROFILE/OneDrive: those can be stale after
//! moving the desktop, and OneDrive's folder name can also be localized.
use std::{io, path::PathBuf};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Folder {
    User,
    Public,
}

pub fn roots() -> Vec<PathBuf> {
    roots_with(known_folder)
}

fn roots_with(mut lookup: impl FnMut(Folder) -> io::Result<PathBuf>) -> Vec<PathBuf> {
    let mut roots = Vec::new();
    let mut seen = std::collections::BTreeSet::new();
    for folder in [Folder::User, Folder::Public] {
        match lookup(folder) {
            Ok(path) if !path.as_os_str().is_empty() => {
                // Canonicalization also deduplicates junctions pointing at the
                // same directory. Preserve the original Shell path for scans.
                if seen.insert(super::identity_key(&super::normalize_path(&path))) {
                    eprintln!("[desktop] resolved {folder:?} desktop: {}", path.display());
                    roots.push(path);
                }
            }
            Ok(_) => eprintln!("[desktop] {folder:?} desktop resolution returned an empty path"),
            Err(error) => eprintln!("[desktop] cannot resolve {folder:?} desktop: {error}"),
        }
    }
    roots
}

#[cfg(windows)]
fn known_folder(folder: Folder) -> io::Result<PathBuf> {
    use std::{ffi::OsString, os::windows::ffi::OsStringExt, ptr};
    use windows_sys::Win32::{
        System::Com::CoTaskMemFree,
        UI::Shell::{FOLDERID_Desktop, FOLDERID_PublicDesktop, SHGetKnownFolderPath},
    };

    struct ShellPath(*mut u16);
    impl Drop for ShellPath {
        fn drop(&mut self) {
            // The API requires freeing its output even on a failed HRESULT.
            unsafe { CoTaskMemFree(self.0.cast()) }
        }
    }

    let id = match folder {
        Folder::User => &FOLDERID_Desktop,
        Folder::Public => &FOLDERID_PublicDesktop,
    };
    let mut buffer = ShellPath(ptr::null_mut());
    // Flags 0 follow the current redirected location (not DEFAULT_PATH), and
    // do not create a folder or modify the user's desktop configuration.
    let status = unsafe { SHGetKnownFolderPath(id, 0, ptr::null_mut(), &mut buffer.0) };
    if status < 0 {
        return Err(io::Error::other(format!("HRESULT 0x{:08X}", status as u32)));
    }
    if buffer.0.is_null() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "null Shell path",
        ));
    }
    // SHGetKnownFolderPath owns and returns a NUL-terminated UTF-16 string.
    // Keep it in OsString, rather than converting through an ANSI code page.
    let mut length = 0;
    while unsafe { *buffer.0.add(length) } != 0 {
        length += 1;
    }
    let wide = unsafe { std::slice::from_raw_parts(buffer.0, length) };
    Ok(PathBuf::from(OsString::from_wide(wide)))
}

#[cfg(not(windows))]
fn known_folder(_folder: Folder) -> io::Result<PathBuf> {
    Err(io::Error::new(
        io::ErrorKind::Unsupported,
        "Windows Shell is required",
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn redirected_desktop_keeps_shell_path_and_includes_public_desktop() {
        let user = PathBuf::from(r"D:\用户文件\桌面");
        let public = PathBuf::from(r"C:\Users\Public\Desktop");
        let mut requests = Vec::new();
        let roots = roots_with(|folder| {
            requests.push(folder);
            Ok(match folder {
                Folder::User => user.clone(),
                Folder::Public => public.clone(),
            })
        });
        assert_eq!(requests, [Folder::User, Folder::Public]);
        assert_eq!(roots, [user, public]);
    }

    #[test]
    fn onedrive_redirection_does_not_add_guessed_old_desktop() {
        let actual = PathBuf::from(r"D:\OneDrive - Work\桌面");
        let roots = roots_with(|_| Ok(actual.clone()));
        assert_eq!(roots, [actual]);
    }

    #[test]
    fn duplicate_root_aliases_are_only_scanned_once() {
        let directory = std::env::temp_dir();
        let roots = roots_with(|folder| {
            Ok(match folder {
                Folder::User => directory.clone(),
                Folder::Public => directory.join("."),
            })
        });
        assert_eq!(roots, [directory]);
    }

    #[test]
    fn failed_user_lookup_does_not_block_public_desktop() {
        let public = PathBuf::from(r"D:\公共桌面");
        let roots = roots_with(|folder| match folder {
            Folder::User => Err(io::Error::new(io::ErrorKind::PermissionDenied, "denied")),
            Folder::Public => Ok(public.clone()),
        });
        assert_eq!(roots, [public]);
    }

    #[test]
    fn failed_or_empty_lookup_does_not_guess_a_directory() {
        let roots = roots_with(|folder| match folder {
            Folder::User => Err(io::Error::new(io::ErrorKind::NotFound, "missing")),
            Folder::Public => Ok(PathBuf::new()),
        });
        assert!(roots.is_empty());
    }

    #[cfg(windows)]
    #[test]
    fn windows_shell_returns_absolute_desktop_paths() {
        assert!(known_folder(Folder::User)
            .expect("current user desktop")
            .is_absolute());
        assert!(known_folder(Folder::Public)
            .expect("public desktop")
            .is_absolute());
    }
}
