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

Validated payloads, source URL, ETag, Last-Modified, and fetch time are stored below `<ApplicationPaths.CacheDirectory>/ModuleUpdates/Official`, so profiles do not share writable state. Manual checks ignore the 24-hour gate but retain conditional requests. A `304` requires valid cache. Network failure may use valid older metadata marked stale; damaged cache is ignored. Temporary-file replacement protects the previous cache from incomplete writes.

The host version comes from `AssemblyInformationalVersion`. A future phase may add package download, hash verification, and transactional installation; none is part of this phase.
