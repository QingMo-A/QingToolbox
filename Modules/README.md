# Local modules

This directory is reserved for local module packages during development.

Compiled module outputs should not be committed here unless explicitly needed.

The Shell scans `Modules` relative to its runtime output directory, not this source
directory. Use `scripts/deploy-dev-modules.ps1` to deploy development modules to the
Shell output. Do not commit DLL, EXE, PDB, `bin`, or `obj` artifacts.
