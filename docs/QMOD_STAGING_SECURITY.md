# Secure offline `.qmod` staging

QingToolbox validates an already downloaded and SHA256-verified `.qmod` before it can become a verified staging directory. Staging is not installation: it never writes `UserModulesDirectory`, replaces a module, imports a package, loads an assembly, activates a module, or executes package code.

## Package identity

Secure staging requires root-level `qmod.json` and `module.json`. `qmod.json` schema 1 has exactly these properties:

```json
{
  "schemaVersion": 1,
  "moduleId": "qing.example",
  "version": "1.0.0",
  "moduleApiVersion": "experimental-0.1",
  "entryManifest": "module.json"
}
```

The package, selected official Release identity, `qmod.json`, and `module.json` must agree on module ID and version. The package API must match the current host API. Complete operation identity also contains a normalized hash of the validated official Release URL; `LocalVersion` is deliberately excluded because it is UI selection context rather than asset trust identity. Before opening the ZIP, every existing source parent is rejected if it is a reparse point. The opened package handle is resolved with `GetFinalPathNameByHandle`, checked against the physical staging and user-module roots, and then retained for size, SHA256, ZIP validation, and extraction without reopening by path.

The Preview manual importer still accepts the older root `module.json` format. It is a separate trusted-user workflow and does not grant the stronger “verified staging” state.

## Archive limits

| Limit | Default |
|---|---:|
| Entries | 2,048 |
| One uncompressed file | 128 MiB |
| Total uncompressed bytes | 256 MiB |
| Path depth | 16 segments |
| Relative path | 240 characters |
| One-entry compression ratio | 200:1 |
| Overall compression ratio | 100:1 |
| Each manifest | 64 KiB |

Metadata is checked before extraction. A counting copy loop checks declared length, per-file size, and remaining total size again while bytes are written. A non-empty entry with zero compressed bytes is rejected instead of bypassing ratio checks.

## Path and entry safety

Raw names are validated before target paths are resolved. The validator rejects absolute, drive, UNC, rooted, traversal, empty/dot segment, ADS, control/invalid character, trailing dot/space, and reserved Windows device names. Both slash styles are normalized. Case-insensitive and Unicode Form C collisions, file/directory collisions, and file parents are rejected.

Unix symlinks, devices, FIFO and sockets, Windows reparse entries, and other non-regular entries are rejected from ZIP attributes. Existing staging roots and parents are rechecked for reparse points before writes. Every target is independently constrained below the random partial root.

## Isolated and atomic layout

Each execution environment supplies its own cache root:

```text
<environment cache>/ModulePackages/Staging/
  Incoming/<guid>.partial/
  Verified/<module-id>/<version>/<package-sha256>/
  Rejected/<transaction-id>.invalid/
  Locks/<root-environment-module-version-hash>.lock
```

All validation, extraction, hashing, and metadata creation happens in `Incoming`. After the tree and stable file list are rechecked, same-volume `Directory.Move` begins the publication commit. Success is logged only after strict final attestation. A deterministic post-move failure is quarantined only when host metadata proves the directory carries the current transaction ID; it is atomically moved to `Rejected`, never left in the normal Verified namespace. Existing directories of unknown ownership are not deleted or overwritten.

Host-generated `qmod-staging.json` records source/environment identity, package identity, counts, and a sorted size/SHA256 list for every extracted file. It does not contain private absolute paths, signed query URLs, authorization data, or package text.

In-flight work is shared only when the complete immutable package identity matches: canonical package path, module ID, target version, file name, expected size, SHA256, module API, official source identity, service-bound execution environment, and normalized official Release identity hash. SHA equality alone never permits sharing. Static trust checks run before operation lookup, and each distinct package path receives its own stable-handle TOCTOU verification.

The execution environment is bound when the staging service is constructed; callers cannot select it per request. The local asynchronous gate includes canonical staging root, environment, module and version. Cross-process ownership uses a persistent, empty-or-owner-marked lock host opened through Win32 with `FILE_FLAG_OPEN_REPARSE_POINT`, read/write access and no sharing. The file name is a stable hash of the same logical identity. The OS closes the exclusive handle when a process crashes; the next process can reopen it, observe the non-sensitive stale PID marker, record crash recovery, and clear it on normal release. Tests launch independent worker processes, kill one while it owns the handle, and verify recovery without deleting the lock file.

Publication locks are acquired before extraction capacity. A request waiting for one module/version therefore cannot consume the bounded ZIP/extraction slot needed by an unrelated module. Locks remain scoped per root/environment/module/version, so unrelated staging roots and environments remain parallel.

Cancelling one caller's wait does not cancel other callers. Explicit transaction cancellation uses the complete operation identity and also cancels lock waits. Lock handles are released on cancellation and disposal, stale unlocked files are recoverable, and partial directories remain transaction-owned.

An existing same-SHA result is reused only after strict validation of every schema-2 `qmod-staging.json` property and file record, including source/environment/Release/package identity, transaction identity, timestamp, counts, sizes, and hashes. Unknown, duplicate, malformed, BOM-prefixed, colliding, or unsupported metadata is rejected as `StagingMetadataInvalid`. Tree verification retains no-delete directory handles while enumerating, opens files once, validates each final handle path inside the physical Verified root, and hashes from that same handle while checking length before and after. It rejects missing or extra files/directories, type changes, collisions, and every reparse boundary without reading an external target. Tree corruption is reported as `VerifiedStagingInvalid` and is never automatically repaired or overwritten.

## Test boundary

`QingToolbox.DevTools.ModulePackageStagingSmokeTest` builds archives at runtime and also runs itself in restricted worker mode as genuine child processes. Coverage includes two-process SHA conflicts, forced holder-process termination and recovery, cross-process cancellation, lock-handle release, source/root reparse checks, root overlap rejection, OfficialUrl identity isolation, extraction-capacity ordering, failed publication quarantine, idempotent disposal, strict metadata/tree tampering, and unrelated-root parallelism. Its payload probe still proves no package assembly is loaded or executed.

Verified Staging is still not module installation. This phase does not close or unload modules, replace production module directories, apply pending updates, or implement rollback.
