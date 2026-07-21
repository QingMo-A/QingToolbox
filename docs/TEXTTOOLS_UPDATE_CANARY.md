# TextTools update canary

The B2.1 canary uses real TextTools source pinned to modules commit
`bc0e57b5a77e3526de157d92a3d300bf3d267e8b`. The script fetches that object only into the dedicated
temporary ref `refs/qingtoolbox/canary/texttools/<commit>` and deletes the ref afterwards. It never
moves, commits to, or pushes `origin/modules`.

```powershell
./scripts/test-texttools-module-update-canary.ps1 `
  -Configuration Release `
  -TargetEnvironment Both
```

The driver exports the pinned source to an OS temporary directory and adds uncommitted v1/v2
assembly metadata plus `runtimeIsolation: OutOfProcess` and `uiKind: Wpf` to the temporary manifests.
No generated DLL, qmod, ZIP, journal, module data, or temporary ref is committed or uploaded.

Each Development and ModuleTest scenario has isolated UserModules, Data, Cache, Staging, and
Transactions roots. The canary starts real, distinct ModuleHost processes for real TextTools v1 and
v2 assemblies. Each worker creates the actual TextTools `UserControl` in its own top-level WPF
window. Handshake metadata proves the expected source commit, variant, manifest/API/tree identity,
and replacement process generation; the old process handle must be signaled before program files
are replaced.

The matrix verifies:

- success: v1 window closes, v1 exits, v2 loads/activates/opens under the tree lease, then commits;
- rollback: v2 really loads/activates/opens, injected restore failure quiesces and exits v2, disk
  returns to v1, and a new v1 process restores active/window intent;
- RecoveryRequired: an injected inability to prove v2 exit prevents directory replacement and
  retains the journal and backup while unrelated modules remain available.

All canary worker processes are disposed after each scenario. This is Development/ModuleTest gate
coverage only and does not enable Production transactions or automatic module installation.
