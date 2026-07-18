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

The package, selected official Release identity, `qmod.json`, and `module.json` must agree on module ID and version. The package API must match the current host API. Before opening the ZIP, staging rechecks package size and SHA256 while holding the package file open without write or delete sharing; that handle remains open through archive validation and extraction.

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
```

All validation, extraction, hashing, and metadata creation happens in `Incoming`. After the tree and stable file list are rechecked, same-volume `Directory.Move` publishes it. A final directory is therefore absent or complete. Failure and underlying transaction cancellation remove only their own partial directory.

Host-generated `qmod-staging.json` records source/environment identity, package identity, counts, and a sorted size/SHA256 list for every extracted file. It does not contain private absolute paths, signed query URLs, authorization data, or package text.

In-flight work is shared only when the complete immutable package identity matches: canonical package path, module ID, target version, file name, expected size, SHA256, module API, official source identity, and the service-bound execution environment. SHA equality alone never permits sharing. Static trust checks run before operation lookup, and each distinct package path receives its own final size/SHA and open-handle TOCTOU verification.

The execution environment is bound when the staging service is constructed; callers cannot select it per request. A reference-counted per-module/version asynchronous gate protects in-process publication, while a user-session named kernel semaphore—derived only from stable hashes of the environment root and module/version identity—protects the same staging root across service instances and processes. Kernel handle lifetime avoids persistent lock files and crash-created filesystem deadlocks. Locks are scoped per module/version, so unrelated modules remain parallel. For the same module/version, concurrent different-SHA packages deterministically produce one verified result and one `StagingConflict`; publication never leaves two SHA directories.

Cancelling one caller's wait does not cancel other callers. Explicit transaction cancellation uses the complete operation identity and also cancels lock waits. Lock handles are released on cancellation and disposal, stale unlocked files are recoverable, and partial directories remain transaction-owned.

An existing same-SHA result is reused only after strict validation of every `qmod-staging.json` property and file record, including source/environment/package identity, timestamp, counts, sizes, and hashes. Unknown, duplicate, malformed, BOM-prefixed, colliding, or unsupported metadata is rejected as `StagingMetadataInvalid`. A non-recursive, level-by-level tree walk rejects missing or extra files/directories, type changes, collisions, and every reparse boundary before descent or hashing. Tree corruption is reported as `VerifiedStagingInvalid`. A modified verified directory is never deleted, repaired, or overwritten automatically.

## Test boundary

`QingToolbox.DevTools.ModulePackageStagingSmokeTest` builds archives at runtime and covers valid packages, hostile paths, collisions, special entry types, bomb limits, strict manifests, complete-identity sharing, same-SHA identity separation, same- and cross-service SHA conflicts, unrelated-module parallelism, lock cancellation/recovery, strict metadata tampering, exact-tree tampering, idempotency, and concurrent callers. Its payload probe uses a module initializer that would create a sentinel if loaded; staging completes without the assembly entering the default load context and without creating the sentinel.

Verified Staging is still not module installation. This phase does not close or unload modules, replace production module directories, apply pending updates, or implement rollback.
