# QingToolbox Plans

This directory stores Codex-ready implementation plans for the `toolbox` branch.

Read these plans in order unless the user says otherwise:

1. [`001-redesign-shell-workspace-layout.md`](001-redesign-shell-workspace-layout.md)
   - Polish the current Shell workspace layout.
   - Improve Sidebar icon rendering, summary cards, compact module cards, action button hierarchy, and Workspace empty state.

2. [`002-real-shell-navigation-and-module-windows.md`](002-real-shell-navigation-and-module-windows.md)
   - Add real Home / Modules / Running / Settings page switching.
   - Move module cards into the Modules page.
   - Hide module details by default.
   - Open module views in standalone child windows instead of embedding them in the main window.

## Branch rule

These plans are for the `toolbox` branch only.

Do not run these plans on the `modules` branch.

## Safety rules

- Do not change module loader core behavior unless a plan explicitly asks for it.
- Do not make Shell reference concrete module projects.
- Do not load module DLLs during Shell startup or Refresh Modules.
- Do not commit `bin/`, `obj/`, `.dll`, `.exe`, or `.pdb` files.
- Do not use `git push --force`.
