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

Schema 3 of the versioned, strict JSON journal records safe identities rather than private absolute
paths. It contains the schema and transaction IDs, environment and module IDs, source and
target versions, module API and package SHA256, hashed Verified/UserModules physical
identities, previous runtime state, attempt, state, failure code, timestamps, the old
program-tree fingerprint, and the volume serial plus 128-bit File ID of the installed,
candidate, backup, and promoted directory objects.
Unknown, duplicate, missing, or invalid fields are rejected.

Each transition is serialized to a temporary file, flushed to disk, and atomically moved
over the journal on the same volume. States distinguish preparation, quiescing, backup,
promotion, verification, runtime restoration, commit, cleanup, rollback, and recovery.

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
`CreateNew`, flush, atomic replacement, and namespace/root File-ID checks before and after.
Module identities share a strict lower-case Windows path-segment validator and reject device
names plus the `.qing-` host namespace, including `.qing-transactions`.

## Verification

`QingToolbox.DevTools.ModuleUpdateTransactionSmokeTest` builds disposable packages and
installed modules at runtime. It covers successful update/new install, lifecycle and file
failure injection, exact-tree and marker attacks, reserved identities, root overlap,
rogue Verified roots, File-ID-preserving rename and replacement, module-isolated journal
corruption, lock/journal namespace replacement, early installed-tree rejection,
lock/link boundaries, external cleanup sentinels,
committed cleanup failures, and data/cache isolation. Four real child processes fail fast
after backup move, after candidate move, after runtime restoration, and after durable
commit. The candidate-move crash case corrupts the promoted payload before recovery and
still restores v1 by directory identity. The first three recover v1; the committed window keeps v2 and only cleans ownership
state. Windows CI runs this required step without `continue-on-error`.

Phase A remains **Engineering Complete**. Phase B has started and only the B1 transaction
core described here is complete. Preview 2 manual acceptance items that were not run remain
`Not Run`; this work is not evidence that those checks passed.
