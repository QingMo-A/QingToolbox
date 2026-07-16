# QingToolbox local development environments

QingToolbox separates the installed product, host development, and module testing so source builds never compete with a released installation for its mutex, activation pipe, settings, modules, module data, or Windows login-startup registration.

## Environments

- **Production / Default** is the installed or portable product. It keeps `%APPDATA%\QingToolbox` for settings and module data and `%LOCALAPPDATA%\QingToolbox\Modules` for imported modules. A Release build with no arguments remains Production-compatible.
- **Development** runs the current source host in `<RepoRoot>\.qingtoolbox\development\<Profile>`. It discovers the build output `Modules` directory and its sandbox `local\modules` directory.
- **ModuleTest** runs the current source host in `<RepoRoot>\.qingtoolbox\module-test\<Profile>`. It discovers only its sandbox `local\modules` directory.

Both sandbox environments store settings and module data under `roaming`, imported modules, logs and cache under `local`, and temporary files under `temp`. They never copy or fall back to Production data. `.qingtoolbox` is local state and must not be committed.

The project-local layout is an enforced contract, not a convention. A Development data root must be exactly `<RepoRoot>\.qingtoolbox\development\<Profile>` and a ModuleTest data root must be exactly `<RepoRoot>\.qingtoolbox\module-test\<Profile>`. The repository may be on any drive, but the environment folder and final directory name must match the selected environment and Profile. Arbitrary absolute data roots are rejected rather than corrected or guessed.

Existing `.qingtoolbox`, environment, and Profile path segments must be ordinary directories. Directory symbolic links, junctions, and other reparse points in these segments are rejected before startup and checked again after directory creation. Profile reset also refuses a Profile containing directory reparse points instead of following them outside the sandbox.

## Instances and visible identity

Production preserves the released single-instance identity. Sandbox instances derive a bounded SHA256 scope from environment kind, validated profile name, and normalized sandbox path. The same environment/profile/path remains single-instance, while different profiles, roots, or kinds can run together.

The main window, notification icon, and floating badge identify sandbox instances as `QingToolbox [DEV: <Profile>]` or `QingToolbox [MODULE TEST: <Profile>]`. The full sandbox path is not displayed.

## Start a development host

```powershell
.\scripts\start-dev-host.ps1
.\scripts\start-dev-host.ps1 -Profile Shell2
```

Debug builds intentionally reject an unqualified launch. Do not press F5 without explicit environment arguments; use the script above.

## Start a module-test host

```powershell
.\scripts\start-module-test-host.ps1 -Profile PowerGuard
```

Manually place the module payload in `.qingtoolbox\module-test\PowerGuard\local\modules`. This phase does not build, deploy, download, or automatically load a module. It uses the current source host only; versioned host caching is planned for a later phase.

## Reset a local profile

```powershell
.\scripts\reset-local-profile.ps1 -Environment Development -Profile Shell
.\scripts\reset-local-profile.ps1 -Environment ModuleTest -Profile PowerGuard -Force
```

The reset script accepts only validated project-local profiles and cannot target Production data. Development and ModuleTest also disable Windows login startup; an accidental `LaunchAtLogin=true` in sandbox settings does not touch the registry.

The reset command supports PowerShell's standard `-WhatIf` and `-Confirm` behavior. `-Force` suppresses the additional high-impact confirmation only; it still calls `ShouldProcess`, so `-Force -WhatIf` never deletes the Profile.

ModuleTest currently runs the host built from the current source checkout. Versioned test-host caching remains a later-phase capability.
