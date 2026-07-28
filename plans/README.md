# QingToolbox Plans

This directory stores Codex-ready implementation plans for the `toolbox` branch.

Read these plans in order unless the user says otherwise:

1. [`001-redesign-shell-workspace-layout.md`](001-redesign-shell-workspace-layout.md)
   - **Historical — Implemented by the native WPF Shell baseline.**
   - Polish the current Shell workspace layout.
   - Improve Sidebar icon rendering, summary cards, compact module cards, action button hierarchy, and Workspace empty state.

2. [`002-real-shell-navigation-and-module-windows.md`](002-real-shell-navigation-and-module-windows.md)
   - **Historical — Implemented by the native WPF Shell baseline.**
   - Add real Home / Modules / Running / Settings page switching.
   - Move module cards into the Modules page.
   - Hide module details by default.
   - Open module views in standalone child windows instead of embedding them in the main window.

3. [`003-hybrid-web-ui-modernization-and-surface-system.md`](003-hybrid-web-ui-modernization-and-surface-system.md)
   - Define the long-term .NET / WPF / WebView2 / Vue hybrid UI architecture.
   - Establish Qing Design System, Qing Surface System, Qing Bridge, and Qing Contracts boundaries.
   - Plan staged migration without replacing the frozen B1 transaction core or bypassing B2 lifecycle integration.

4. [`004-close-b2-1-runtime-trust-and-gate-boundary.md`](004-close-b2-1-runtime-trust-and-gate-boundary.md)
   - Close the B2.1 verified runtime identity, tree lease, discovery, gate, and shutdown boundaries.
   - **Engineering Complete — Frozen**; UI-1 is now the next planned stage.

5. [`005-development-only-web-shell-foundation.md`](005-development-only-web-shell-foundation.md)
   - Establish the Development-only WebView2 + Vue 3 workspace and read-only Qing Bridge.
   - Preserve native WPF as the Production/ModuleTest default and failure fallback.

6. [`006-web-shell-readiness-and-asset-integrity-gate.md`](006-web-shell-readiness-and-asset-integrity-gate.md)
   - Gate Web Shell visibility on trusted protocol-v2 readiness, snapshot, and ping.
   - Bind bridge generations and packaged assets to deterministic identities.
   - **Engineering Complete** after a real non-Mock navigation/ready/snapshot/ping canary.

7. [`007-web-shell-activation-attestation-and-runtime-asset-trust.md`](007-web-shell-activation-attestation-and-runtime-asset-trust.md)
   - Require a generation-scoped challenge and acknowledged nonce ping before workspace activation.
   - Anchor runtime assets to manifest identity compiled into the Shell.
   - **Engineering Complete — Frozen**; Plan 008 completed the post-freeze correctness correction.

8. [`008-web-shell-session-semantics-and-immutable-asset-serving.md`](008-web-shell-session-semantics-and-immutable-asset-serving.md)
   - Separate one-use activation nonces from generation-scoped read-only session tokens.
   - Serialize recovery and serve verified Web assets only from an immutable memory snapshot.
   - **Engineering Complete — Frozen**; UI-1 is frozen and UI-2 is next.

9. [`009-ui-2a-read-only-module-center-and-visual-foundation.md`](009-ui-2a-read-only-module-center-and-visual-foundation.md)
   - Establish the first Qing design tokens and reusable visible workspace primitives.
   - Add Home, read-only Modules and details, retained Diagnostics, and local theme preview.
   - **Implementation Complete**; no module lifecycle or settings mutation was introduced.

10. [`010-development-web-module-operations.md`](010-development-web-module-operations.md)
    - Add host-confirmed Development Web module lifecycle entry points.
    - Reuse frozen B2.1 execution, recovery, and module-window boundaries.
    - Deliver Load/Activate, Open, Deactivate/Unload, and startup authorization as separate tasks.
    - **Implementation Complete — Load/Activate, Open/Focus, Deactivate/Unload, and startup authorization delivered.**

11. [`011-development-web-everyday-workspace.md`](011-development-web-everyday-workspace.md)
    - Deliver a useful host-backed Home dashboard.
    - Reorganize Settings into focused everyday-workspace sections.
    - **Implementation Complete — Home Dashboard and Settings information architecture delivered.**

12. [`012-development-web-localization.md`](012-development-web-localization.md)
    - Establish host-confirmed language selection and reactive localization across the Development Web workspace.
    - Deliver the work as three bounded slices: language setting, Vue localization foundation, and complete workspace coverage.
    - Keep module-summary correctness as an independent P1 prerequisite instead of hiding the Bug inside the localization phase.
    - **Active — User approved; prerequisite P1, UI-5A1 and UI-5A2 are implementation complete. Next: UI-5A3 Complete Development workspace localization.**

Plan 003 is a master architecture plan. It does not authorize implementing every UI phase in one task. Future UI work should use additional numbered plans referencing this architecture.

A plan marked `Draft — Pending User Approval` is review material only. Codex must not implement its slices until the user explicitly approves the plan and an execution prompt selects one bounded slice. An active plan still authorizes only the single slice selected by the current Codex prompt.

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
