# Changelog

## 0.1.0-alpha

### Added

- Modular Shell with manual module discovery, loading, activation, deactivation and unload.
- In-process module loader with collectible `AssemblyLoadContext`.
- Module manifest discovery without loading DLLs.
- Module windows.
- Shell localization and module localization.
- Module templates with en-US and zh-CN resources.
- `.qmod` package import preview.
- TextTools, ScreenPin and WindowTopmost preview modules.

### Known Issues

- This is a Preview release, not a stable release.
- `.qmod` packages are not signed; only import modules from trusted sources.
- ScreenPin geometry, DPI and resize behavior still need refinement.
- There is no installer or uninstaller yet.
- Windows SmartScreen may warn because the binaries are unsigned.
