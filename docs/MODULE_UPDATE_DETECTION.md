# Read-only official module update detection

QingToolbox currently detects official module updates but does not download, install, unpack, or replace `.qmod` packages. Detection never loads or activates a module DLL, and failure never prevents an installed module from running.

## Official metadata

The trusted index is `https://raw.githubusercontent.com/QingMo-A/QingToolbox/modules/modules/index.json`. It is a thin `moduleId` to `update.json` mapping. The host fetches the index once and requests a module manifest only for an installed, discovered module present in that index. Local manifests cannot supply or override update URLs, and uninstalled entries are not requested.

The mapped path is resolved against the fixed official modules base URL. HTTPS, `raw.githubusercontent.com`, safe relative path segments, and absence of query strings and fragments are enforced. Package URLs are parsed as immutable GitHub Release Asset metadata but never contacted.

## Selection and status

Protocol JSON is strict and size limited. Versions use SemVer 2.0 precedence. A prerelease host follows Preview and may select preview or stable releases; a stable host accepts only stable releases. Releases must match the centralized temporary Module API identity `experimental-0.1` and the inclusive minimum/exclusive maximum host range.

Results distinguish not checked, checking, non-official, no published release, up to date, update available, host update required, Module API incompatible, newer local version, invalid local version, unavailable/invalid source, and ModuleTest-disabled states. Empty releases mean no version is currently published through the official source.

## HTTP cache and environments

Production schedules a non-blocking check after UI and module discovery initialization. Valid metadata is fresh for 24 hours. Development does not auto-check but permits manual checking. ModuleTest disables checks, makes no network request, and creates no cache.

Automatic and manual checks pass through one coordinator, so only one complete check can run at a time. A manual request queued behind an automatic request performs its own conditional request rather than reusing the automatic result. An empty installed-module snapshot returns immediately without requesting the index or creating cache. Every result carries the exact local module version from its discovery snapshot; refresh discards results when that version changes or the module disappears.

Validated payloads, source URL, ETag, Last-Modified, and fetch time are stored below `<ApplicationPaths.CacheDirectory>/ModuleUpdates/Official`, so profiles do not share writable state. Each key is one versioned JSON cache envelope written through a temporary file and atomically replaced. Manual checks ignore the 24-hour gate but retain conditional requests. A `304` revalidates the payload, refreshes `FetchedAt`, and atomically saves the envelope; the following 24-hour automatic check stays offline. A future `FetchedAt` or malformed/oversized envelope is invalid. Network failure may use valid older metadata marked stale; damaged cache is ignored.

Minimum-host failure means QingToolbox must be upgraded. `HostVersionIncompatible` instead means the current host is at or above the release's exclusive maximum and that release no longer supports it. Module API mismatch remains a separate state. Release notes are retained bilingually and selected from the current effective UI language, with English and then any non-empty note as fallbacks.

Metadata paths are ASCII-only and reject percent encoding, whitespace, control characters, colons, backslashes, empty/dot segments, queries, and fragments. The resolved HTTPS URI must remain on the fixed official host and default port.

The host version comes from `AssemblyInformationalVersion`. Detection still never requests `package.url`, downloads `.qmod`, or installs a module. Manual UI acceptance is required before a later package-download, hash-verification, or transactional-installation phase begins.
