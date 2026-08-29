# QingToolbox `.qmod` Package Format

> This document contains both the existing WPF package profile and the new
> Tauri process profile. New modules should use the Tauri profile below; the
> WPF DLL profile is retained only for the current host while migration is in
> progress.

Version: `0.2.0-alpha` Preview 2

## Container

A `.qmod` file is a ZIP archive with a different extension. The archive root
must contain exactly one `module.json`. Packages intended for the secure
Verified Staging pipeline must also contain exactly one `qmod.json`. Do not
wrap the package contents in an extra top-level directory.

Secure staging `qmod.json` schema 1 contains `schemaVersion`, `moduleId`,
`version`, `moduleApiVersion`, and `entryManifest`; `entryManifest` must be the
root `module.json`. See [`QMOD_STAGING_SECURITY.md`](QMOD_STAGING_SECURITY.md).

## Existing WPF profile (legacy)

The following profile is read only by the current WPF host during migration.
It is not loaded by `QingToolbox.Tauri` and has no compatibility adapter in the
new host.

Required manifest fields:

- `id`
- `name`
- `description`
- `version`
- `entry`
- `runtimeType`
- `loadMode`
- `defaultLanguage`
- `localization`

`entry` must be a relative path to a DLL contained in the package. A package
should also contain:

```text
module.json
QingToolbox.Modules.Example.dll
icon.svg
i18n/
  en-US.json
  zh-CN.json
```

## Path and size rules

- Absolute paths are forbidden.
- `..` path traversal is forbidden.
- Drive-qualified paths and entries containing `:` are forbidden.
- Import validates that every extracted path remains inside a temporary module
  directory before creating the final installation directory.
- A package may contain at most 2,048 entries and expand to at most 256 MB.
- The manifest must be valid and its entry DLL must exist before installation.

The legacy Preview importer extracts into a temporary directory under
`%LOCALAPPDATA%\QingToolbox\Modules`. The Tauri host has a separate importer
for the process profile: it validates the complete ZIP first, rejects unsafe
entries (including duplicate case-insensitive names, encrypted entries and
symlinks), extracts below a random staging directory, runs the new-host
manifest validator, and moves the finished directory into place only after
validation. Failed imports remove the staging directory. Existing module IDs
are rejected; the importer never replaces or executes a package during import.

Import and Refresh only read and validate files. They do not load the entry DLL.
Loading remains an explicit user action.

## Creating a package

From a prepared module output directory whose root contains `module.json`:

```powershell
Compress-Archive -Path .\ModuleOutput\* -DestinationPath .\Example.zip
Rename-Item .\Example.zip Example.qmod
```

Test both a valid package and rejected packages using the manual release
checklist in [`releases/0.2.0-alpha.md`](releases/0.2.0-alpha.md).

## Preview security notice

QingToolbox `0.2.0-alpha` does not verify module signatures. A loaded module
runs in the user process and has the current user's permissions. Only import
packages from sources you trust.

Verified staging remains isolated cache state and is not module installation.
Future work includes package signing, a module marketplace, transactional
replacement and rollback, richer permission declarations, and dependency resolution.

## Tauri process profile

The Tauri host does not load a module DLL. A new module package must declare an
independent executable and communicate with the host through the versioned
JSON-lines contract in [`../protocol/README.md`](../protocol/README.md):

```text
module.json
bin/
  qing-example.exe
ui/
  index.html
  assets/...
icon.svg
```

The minimum process-profile fields are:

```json
{
  "id": "qing.example",
  "name": "Example",
  "version": "0.1.0",
  "entry": "bin/qing-example.exe",
  "runtimeType": "Process",
  "runtimeIsolation": "OutOfProcess",
  "uiKind": "Web",
  "webEntry": "ui/index.html",
  "loadMode": "Manual",
  "permissions": [],
  "operations": ["getState"]
}
```

`entry` and `webEntry` are relative paths. The Tauri backend canonicalizes and
rechecks them against the module directory before a process can be started;
the Vue frontend never receives or submits those paths. The host starts only
the executable recorded by a validated manifest, sends a `module.hello`
request, and owns the shutdown timeout. A failed or unsupported process does
not affect discovery of other modules.

The process profile is intentionally Windows-executable-only in its first
implementation. Script wrappers and arbitrary command lines are not accepted;
if a module needs a helper runtime, package that helper as a fixed sidecar and
keep its lifecycle under the module process.

`operations` is an optional allowlist for the versioned `module.invoke` bridge.
The host rejects calls that are not declared by the manifest; an empty or
missing list keeps the module UI read-only until a later contract is added.
