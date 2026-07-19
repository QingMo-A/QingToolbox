# Module update transaction core

Phase B1 adds the recoverable module-program replacement core intended for the future
`0.3.0-alpha` line. It is engineering infrastructure, not a production update feature.
There is no production update button, no automatic installation after download, and no
TextTools canary update in this phase.

## Trust boundary

The transaction service accepts only an immutable `QmodVerifiedStagingAttestation`
created by `QmodPackageStagingService`. Before mutation it acquires the module transaction
lock and re-attests the physical Verified root and directory, environment, module API,
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

The versioned, strict JSON journal records safe identities rather than private absolute
paths. It contains the schema and transaction IDs, environment and module IDs, source and
target versions, module API and package SHA256, hashed Verified/UserModules physical
identities, previous runtime state, attempt, state, failure code, and timestamps.
Unknown, duplicate, missing, or invalid fields are rejected.

Each transition is serialized to a temporary file, flushed to disk, and atomically moved
over the journal on the same volume. States distinguish preparation, quiescing, backup,
promotion, verification, runtime restoration, commit, cleanup, rollback, and recovery.

The commit point is reached only after the promoted directory passes exact static
verification and the previous runtime intent is restored. The transaction ownership
marker is then removed, the exact installed tree is verified again, and `Committed` is
persisted. Backup cleanup failure becomes `CleanupPending`; it does not rewrite a committed
update as failed.

## Replacement and recovery

The candidate is copied from stable, re-attested staging files with `CreateNew`, per-file
SHA256 checks, exact-tree verification, and no `qmod.json` or `qmod-staging.json` in the
installed payload. An existing module directory is atomically renamed to `backup`, then
the candidate is atomically renamed into its place.

Failures before `Committed` attempt rollback. A promoted candidate is isolated, the old
directory is restored when present, and prior runtime intent is requested again. If safe
ownership or restoration cannot be proven, the journal and backup are preserved as
`RecoveryRequired`; the engine does not pretend success.

Recovery scans only the current environment journal directory and takes the same
physical-root/environment/module lock. Incomplete work is rolled back, committed cleanup
is retried, and an explicit `RecoveryRequired` journal is preserved for diagnosis. No
recovery path loads a DLL.

## Verification

`QingToolbox.DevTools.ModuleUpdateTransactionSmokeTest` builds disposable packages and
installed modules at runtime. It covers successful update and new install, lifecycle and
filesystem failure injection, exact-tree tamper rejection, rollback, cleanup recovery,
same-module contention, data/cache isolation, and a real child-process crash after the old
module has been backed up followed by recovery. The Windows CI workflow runs this test as
a required step without `continue-on-error`.

Phase A remains **Engineering Complete**. Phase B has started and only the B1 transaction
core described here is complete. Preview 2 manual acceptance items that were not run remain
`Not Run`; this work is not evidence that those checks passed.
