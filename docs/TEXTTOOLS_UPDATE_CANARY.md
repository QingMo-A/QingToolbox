# TextTools update canary

The B2.1 canary uses real TextTools source pinned to modules commit
`bc0e57b5a77e3526de157d92a3d300bf3d267e8b`. Its PowerShell driver exports that commit into an OS
temporary directory, adds uncommitted v1/v2 metadata markers, and builds both real variants. No DLL,
qmod, ZIP, journal, or module data is committed or uploaded.

```powershell
./scripts/test-texttools-module-update-canary.ps1 `
  -Configuration Release `
  -TargetEnvironment Both
```

Every scenario has an isolated Development or ModuleTest UserModules, Data, Cache, Staging, and
Transactions tree. Production paths and settings are never used. Development refuses a shared
TextTools deployment that would shadow its fixture.

The canary uses the real Shell adapter with a narrow fault-injection decorator. It verifies commit
to matching disk/runtime v2, rollback to matching disk/runtime v1, and a module-scoped
RecoveryRequired result retaining installed v2, backup v1, and journal. Data, cache, authorization,
and neighboring sentinels remain unchanged.

Observable identity comes from the real loaded TextTools type plus pinned source and v1/v2 assembly
metadata. The canary avoids instantiating TextTools BAML UI while measuring collectible ALC cleanup,
because WPF can retain generated UI metadata beyond that lifecycle; real window/Dispatcher behavior
is covered separately by the runtime-adapter smoke test.

This remains a Development/ModuleTest gate. It does not enable Production transactions or automatic
module installation.
