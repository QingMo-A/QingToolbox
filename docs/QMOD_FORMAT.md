# QingToolbox `.qmod` Package Format

Version: `0.2.0-alpha` Preview 2

## Container

A `.qmod` file is a ZIP archive with a different extension. The archive root
must contain exactly one `module.json`. Packages intended for the secure
Verified Staging pipeline must also contain exactly one `qmod.json`. Do not
wrap the package contents in an extra top-level directory.

Secure staging `qmod.json` schema 1 contains `schemaVersion`, `moduleId`,
`version`, `moduleApiVersion`, and `entryManifest`; `entryManifest` must be the
root `module.json`. See [`QMOD_STAGING_SECURITY.md`](QMOD_STAGING_SECURITY.md).

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

The Preview importer extracts into a temporary directory under
`%LOCALAPPDATA%\QingToolbox\Modules`. It moves the completed directory into
place only after validation. Failed imports remove the temporary directory.
Existing module IDs are rejected; remove the old module manually before
importing another package with the same ID.

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
