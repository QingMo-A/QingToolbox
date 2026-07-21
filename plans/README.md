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

3. [`003-hybrid-web-ui-modernization-and-surface-system.md`](003-hybrid-web-ui-modernization-and-surface-system.md)
   - Define the long-term .NET / WPF / WebView2 / Vue hybrid UI architecture.
   - Establish Qing Design System, Qing Surface System, Qing Bridge, and Qing Contracts boundaries.
   - Plan staged migration without replacing the frozen B1 transaction core or bypassing B2 lifecycle integration.

Plan 003 is a master architecture plan. It does not authorize implementing every UI phase in one task. Future UI work should use additional numbered plans referencing this architecture.

## Branch rule

These plans are for the `toolbox` branch only.

Do not run these plans on the `modules` branch.

## Safety rules

- Do not change module loader core behavior unless a plan explicitly asks for it.
- Do not make Shell reference concrete module projects.
- Do not load module DLLs during Shell startup or Refresh Modules.
- Do not commit `bin/`, `obj/`, `.dll`, `.exe`, or `.pdb` files.
- Do not use `git push --force`.
- UI plans must not bypass module lifecycle recovery gates.
- Vue must not become the authoritative runtime state source.
- Host capabilities must not expose arbitrary paths, commands, routes, or window creation.