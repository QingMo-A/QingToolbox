# Verified module package downloads

This phase downloads and verifies an official `.qmod` package; it does not install, extract, import, replace, load, or activate a module. A user must explicitly start every transfer.

Before package transfer, QingToolbox repeats the official index and module-manifest check. The fresh result must still select the same immutable release and package URL, file name, byte length, and SHA256 shown to the user. Stale metadata and changed metadata never start a package request.

The initial URL must be an HTTPS GitHub Release Asset under `QingMo-A/QingToolbox`, with no query, fragment, user information, non-default port, mutable `latest` path, or unsafe file name. Redirects are followed explicitly, at most three times, and only to `release-assets.githubusercontent.com` or `objects.githubusercontent.com`. Signed redirect queries are not persisted or shown.

Packages are limited to 256 MiB. A present `Content-Length` must exactly equal the signed metadata size, but streaming byte count remains authoritative and must also match exactly. Content encoding other than `identity` is rejected. The transfer writes each 64 KiB block to a same-directory partial file while feeding `IncrementalHash` SHA256. Expected and actual digest bytes are compared with `CryptographicOperations.FixedTimeEquals`.

Only after size and hash verification is the partial file atomically moved to:

```text
<CacheDirectory>/ModuleUpdates/Packages/Verified/<moduleId>/<version>/<sha256>/<fileName>
```

Existing directories and files are checked for reparse points and every full path is constrained to the Verified root. Production and Development profiles therefore use separate caches. ModuleTest performs no metadata request, package request, or package-directory creation. Partial files are uniquely named `.<fileName>.partial-<guid>` and are removed after cancellation or failure.

An existing package is never trusted from `package-record.json` alone: its length and SHA256 are streamed and checked again. A matching file yields `AlreadyVerified` without a package request; a mismatching controlled file is isolated and removed before a fresh transfer. The record stores only official immutable metadata, never a signed redirect URL.

SHA256 proves that the bytes match the currently unsigned official metadata; it is not a publisher digital signature. A later phase may add `.qmod` structure validation, staging, transactional installation, rollback, and pending-update handling. None of those operations are implemented here, and startup authorization is not changed automatically.
