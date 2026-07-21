# Module update transaction core

Phase B1 adds the recoverable module-program replacement core intended for the future
`0.3.0-alpha` line. It is engineering infrastructure, not a production update feature.
There is no production update button, no automatic installation after download, and no
TextTools canary update in this phase.

## Trust boundary

The transaction service accepts only an immutable `QmodVerifiedStagingAttestation`
created by `QmodPackageStagingService`. It is constructed with the host's
`IQmodVerifiedStagingAttestor`; consequently an attestation is not trusted merely because
its fields claim a matching environment or root. Before mutation the trusted attestor
must bind it to the one configured Verified root and re-attest that root's physical path
and Windows directory File ID, the staged directory, environment, module API,
release identity, package SHA256, exact file list, lengths, hashes, and reparse boundaries.
It never accepts an arbitrary package or directory path and never loads a candidate DLL.

Execution is currently restricted to `Development` and `ModuleTest`. A production WPF
lifecycle adapter and user-facing update workflow remain deliberately disconnected.

## Storage and isolation

The environment-scoped cache contains durable journals and independent transaction locks:

```text
<EnvironmentCacheRoot>/ModuleTransactions/
  Journal/
  Locks/
```

Atomic rename work is kept on the module-program volume:

```text
<UserModulesRoot>/.qing-transactions/<transaction-id>/
  candidate/
  backup/
  failed-candidate/
```

Module data, module cache, settings, startup registration, and other modules are outside
the transaction unit. The engine moves only the selected module program directory.
Ownership markers are required before transaction-owned recursive cleanup.

## Journal and commit point

Schema 4 of the versioned, strict JSON journal records safe identities rather than private absolute
paths. It contains the schema and transaction IDs, environment and module IDs, source and
target versions, module API and package SHA256, hashed Verified/UserModules physical
identities, previous runtime state, attempt, state, failure code, timestamps, the old
program-tree fingerprint, and the volume serial plus 128-bit File ID of the installed,
candidate, backup, and promoted directory objects.
Unknown, duplicate, missing, or invalid fields are rejected.

Each transition is serialized to a uniquely named temporary file. One non-write/non-delete-shared
handle writes, flushes, rereads, and atomically renames that same object over the journal. The
rename uses the held Journal Namespace handle plus a validated relative leaf; it does not close
the temp and resolve `File.Move(temp, final)` by path. States distinguish preparation, quiescing,
backup, promotion, verification, runtime restoration, commit, cleanup, rollback, and recovery.

The previous incompatible journal shape retains its real schema number 3 and has a separate,
strict legacy reader. Recovery uses only a minimal validated envelope to select the module lock,
rereads the complete journal and validates its owned on-disk site under that lock, then atomically
persists schema 4 before continuing. The old `runtimeRestored` field and an uncertain
`RollbackStarted` state are mapped conservatively. An ambiguous or unsafe legacy journal is
preserved rather than guessed or deleted. A transitional schema 3 document carrying the schema 4
lifecycle shape is rejected instead of accepting two formats under one number.

The transaction ownership marker and backup remain present while the promoted directory is
verified and the previous runtime intent is restored. The engine verifies the payload and
marker again, atomically persists `Committed`, and only then removes the marker and cleans
the backup/work tree. Once `Committed` is durable, no exception can enter rollback. Marker,
work, journal, or even `CleanupPending` journal-write failures return committed success with
cleanup pending; recovery retries cleanup without replacing the new module.

## Replacement and recovery

The candidate is copied from stable, re-attested staging handles. Source files are opened
once with `OPEN_REPARSE_POINT`, cannot be written or deleted while open, have their final
physical paths checked against the Verified root, and are hashed while the same handle is
copied. Destinations use `CreateNew`, `FileShare.None`, `WriteThrough`, and `Flush(true)`.
Strict tree verification rejects additional files or empty directories, case/Unicode
collisions, reparse entries, and manifest/entry mismatches. `qmod.json` and
`qmod-staging.json` do not enter the installed payload.

Content integrity and directory ownership are independent proofs. Failures before
`Committed` attempt rollback. A promoted candidate whose files were corrupted can still
be isolated when its marker and File ID prove that it is the same transaction-created
directory object; the verified old directory is then restored. A candidate or backup that
was deleted and recreated has a different File ID, is preserved in place, and requires
manual recovery. A promoted candidate is isolated, the old
directory is restored when present, and prior runtime intent is requested again. If safe
ownership or restoration cannot be proven, the journal and backup are preserved as
`RecoveryRequired`; the engine does not pretend success.

All four security-critical directory transitions use a source-directory handle opened with
`DELETE`, directory semantics, and `FILE_FLAG_OPEN_REPARSE_POINT`. The native rename receives
the held destination-parent handle in `FILE_RENAME_INFORMATION.RootDirectory` and only a
validated relative leaf name. The parent handle is add-referenced for the complete synchronous
native call. Source File ID, destination-parent File ID, physical-root membership, target
absence, and the source handle's post-rename File ID/name are checked around the operation.
Replacing a string-path ancestor therefore cannot redirect the rename into a substitute parent.
There is no `Directory.Move` fallback for installed-to-backup, candidate-to-installed,
installed-to-failed-candidate, or backup-to-installed.

Installed, candidate, promoted, and backup validation uses `SecureTreeLease`. Both passes record
the exact entry set, object File IDs, lengths, and SHA256 values. The retained lease owns the root,
ordinary child-directory, and ordinary-file handles; ordinary files deny write/delete sharing,
directories deny delete sharing, and all opens use `FILE_FLAG_OPEN_REPARSE_POINT`. The bytes used
to parse `module.json` and the transaction owner marker are captured while hashing those same
handles, so manifest identity and the recorded tree cannot come from different reads. File and
directory counts are bounded and partial construction always releases acquired handles.

Windows does not permit renaming a directory while descendant handles deny delete sharing. At
the final rename boundary the engine therefore verifies the entire held lease once more, releases
only descendant handles, retains the identity-bound root handle, performs the parent-relative
atomic rename, and immediately reacquires and compares the exact snapshot at the new name. A
mutation attempted before that boundary is blocked; a namespace addition in the unavoidable
native rename interval is detected by the post-rename comparison and cannot be committed as a
trusted payload. This is the implementable Windows boundary; this document does not claim that
mutually incompatible descendant share modes remain open during the directory rename.

Runtime progress distinguishes restore-started, promoted-restored, promoted-quiesced,
previous-restore-started, and previous-restored states. Process-local
`promotedRuntimeRestoreAttempted`/`promotedRuntimeMayBeRunning` flags are set before invoking
the runtime coordinator and are not contingent on a progress-journal write succeeding. If a
restore returns false, throws after a partial side effect, or succeeds before its progress write
fails, rollback queries the actual current runtime, closes/deactivates/unloads what is present,
and verifies it is unloaded before moving program directories. Only then does it restore the old
directory and reapply previous runtime intent. Once previous-runtime restoration is durably
complete, recovery does not mistake v1 for promoted v2. A failure to prove quiescence preserves
installed v2, the backup, and the journal as `RecoveryRequired`.

Journals are isolated below a namespace hash of physical UserModules root, environment, and
module ID, so a corrupt journal for one module cannot block another. Journal filenames are
bound to their transaction IDs; strict UTF-8 JSON rejects BOM, missing/duplicate/unknown
fields, invalid hashes, versions, times, state, and namespace identity. Writes use unique
temporary files, disk flush, and same-volume atomic replacement.

Recovery takes the same crash-recoverable exclusive Windows file-handle lock used by
Verified Staging. It follows an explicit state/layout matrix. Moving an installed candidate
during rollback requires the exact transaction marker, matching directory File ID,
ordinary physical-root membership, and an identity- and fingerprint-verified backup/work
layout; journal state alone never proves
ownership. Unknown installed directories and ambiguous orphan temp journals are preserved
as `RecoveryRequired`. Committed recovery only validates the target and performs safe
cleanup; it never rolls back.

Verified staging, candidates, installed trees, backups, and owned cleanup use the shared
level-by-level no-follow walker rather than `SearchOption.AllDirectories`. Each directory
is opened and identified before recursion and checked again afterwards. Reparse entries
are removed as links, and every ordinary child remains bounded by the attested work root.
Transaction and staging locks are checked against the constructor-attested physical
LocksRoot and its File ID after opening. Journal reads use one stable handle; writes use
`CreateNew`, same-handle flush/content verification, parent-handle-relative atomic replacement,
and namespace/root File-ID checks before and after. Exact verification takes two complete
snapshots and retains the second pass as a bounded tree lease. Each entry records type, reparse
state, object File ID, length, and SHA256; any entry addition, removal, type change,
child-directory replacement, or file change rejects the tree. LocksRoot and Journal namespace
child operations retain stable parent-directory leases for their entire create/write/replace
interval.
Module identities share a strict lower-case Windows path-segment validator and reject device
names plus the `.qing-` host namespace, including `.qing-transactions`.

## Verification

`QingToolbox.DevTools.ModuleUpdateTransactionSmokeTest` builds disposable packages and
installed modules at runtime. It covers successful update/new install, lifecycle and file
failure injection, exact-tree and marker attacks, reserved identities, root overlap,
rogue Verified roots, File-ID-preserving rename and replacement, module-isolated journal
corruption, live lock/journal temp and namespace replacement, destination-parent/ancestor
replacement, deterministic source-path replacement, early installed-tree rejection,
post-runtime commit-write rollback, promoted-runtime unload failure, cancellation after runtime
restoration, post-second-pass exact-tree mutation and handle-capacity cleanup,
progress-persistence failure, partial runtime restore, strict schema 3 migration,
lock/link boundaries, external cleanup sentinels,
committed cleanup failures, and data/cache isolation. Five real child processes fail fast
during a genuinely started candidate copy, after backup move, after candidate move, after runtime restoration, and after durable
commit. The candidate-move crash case corrupts the promoted payload before recovery and
still restores v1 by directory identity. The first four recover v1; the committed window keeps v2 and only cleans ownership
state. Windows CI runs this required step without `continue-on-error`.

Phase A remains **Engineering Complete**. The B1 transaction core described here is
**Engineering Complete — Frozen**: it is not extended further unless a concrete security defect
is found. B1 remains restricted to Development/ModuleTest. B2.1 now connects the real Shell
lifecycle adapter, startup recovery gate, and pinned Development/ModuleTest TextTools canary.
Production transaction execution, automatic installation, and update UI remain disabled. Preview 2
manual acceptance items that were not run remain `Not Run`; this work is not evidence that those
checks passed.
