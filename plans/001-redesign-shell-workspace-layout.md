# Plan 001: Redesign Shell Workspace Layout

You are a senior C# / WPF / desktop software UI engineer.

Work on the `toolbox` branch of the QingToolbox repository.

The current Shell UI works functionally, but visually it still feels like an engineering/debug panel. This plan focuses only on visual and layout improvements. Do not change module loading, unloading, `CreateView`, `ModuleRuntimeManager`, or `ModuleLoader` core logic.

## Branch requirement

Confirm the current branch:

```bash
git branch --show-current
```

It must be:

```text
toolbox
```

If not:

```bash
git switch toolbox
git pull origin toolbox
```

Do not run this plan on the `modules` branch.

## Commit message

Use this commit message:

```text
[style] redesign shell workspace layout
```

## Current UI problems

1. The left Sidebar icons look like white blocks and are not semantic enough.
2. The main workspace is too loose; summary cards, module cards, and the right workspace area lack visual hierarchy.
3. Module cards expose too much developer/debug information, such as full `Module directory` and `Manifest` paths.
4. The right Workspace / Active Module panel has a weak empty state and too much blank space.
5. Lifecycle buttons do not have a clear action hierarchy.
6. The whole UI lacks modern desktop product polish.

## Goals

1. Redesign `MainWindow.xaml` main layout.
2. Improve Sidebar icon rendering.
3. Improve module list layout.
4. Move module path / manifest path / debug details into a collapsed Details area.
5. Improve the right Workspace / Active Module panel empty state.
6. Improve action button hierarchy.
7. Preserve all current functionality.
8. Do not change module loading, unloading, `CreateView`, or runtime manager core logic.
9. Do not make Shell reference concrete module projects.
10. Build successfully and push to `origin/toolbox`.

## Overall layout direction

Refactor the Shell into three clear layers:

1. Left Sidebar.
2. Top Header / Toolbar.
3. Main Workspace area.

The main Workspace can be split into two columns:

- Left: Modules panel.
- Right: Workspace / Active Module panel.

Suggested proportions:

```text
Modules panel: 420px ~ 560px
Workspace panel: remaining width
```

Keep the Grid adaptive when the window width changes.

## Sidebar improvements

The current Sidebar icons look like white blocks. Fix this.

Requirements:

1. Check whether `SvgViewbox` renders SVG correctly.
2. Check whether the selected SVG files are too blocky or have invisible paths.
3. Replace unsuitable icons with clearer semantic SVG files if needed.
4. Collapsed Sidebar icons must be clearly recognizable.
5. Hover expand and Pin must still work.
6. Settings must remain at the bottom.
7. Modules must remain selected if that is the current selected navigation item.

Visual requirements:

1. Sidebar background may remain dark, e.g. `#0F172A` or `#111827`.
2. Selected item should use a blue line or blue background block.
3. Icon size: `20` to `22`.
4. Icon container size: about `40`.
5. Icons must not render as solid white squares.
6. Expanded text must align cleanly with icons.

## Header improvements

The current header is too empty.

Header should contain:

Left side:

- `Qing Toolbox`
- `Modular Windows toolbox`

Right side:

- `Refresh Modules` button.
- Optional small status text such as last scan result.

The Refresh button should remain usable and look like a primary toolbar action.

## Summary cards

Current summary cards are too wide and flat.

Improve them:

1. More compact.
2. Keep Total / Valid / Failed / Not Loaded / Loaded / Running.
3. Make numbers visually stronger.
4. Make labels smaller.
5. Failed should be orange/red but not harsh.
6. Running should be green or blue.
7. Suggested card height: `64` to `76`.

Put styles in `QingToolbox.Shell/Resources/ShellTheme.xaml`, for example:

- `SummaryCardStyle`
- `SummaryNumberStyle`
- `SummaryLabelStyle`

Do not move large style blocks back into `MainWindow.xaml`.

## Module card redesign

Current module cards expose too much debug information.

Refactor cards into two layers.

Default visible area:

1. Module icon.
2. Module name.
3. Version.
4. Short description.
5. Runtime State badge.
6. Short RuntimeType / LoadMode / Permissions summary if it fits.
7. Lifecycle action buttons.

Move these into an `Expander` titled `Details`:

1. RuntimeType.
2. LoadMode.
3. Permissions.
4. Author.
5. Entry.
6. Module directory.
7. Manifest path.
8. Minimum host version.
9. Manifest issues.
10. Runtime error.

Requirements:

1. Default card height should be much lower.
2. Users should understand module name, state, and available actions at a glance.
3. Debug path information must not dominate the card.
4. Preserve existing bindings.
5. Do not change `DiscoveredModuleViewModel` core logic unless adding small display-only properties.

## Lifecycle button hierarchy

Current action buttons are all visually similar.

Make the hierarchy clearer:

Primary actions:

- `Load` when the module is `NotLoaded` / `Unloaded`.
- `Open` when the module is `Loaded` / `Running` / `Deactivated`.

Secondary actions:

- `Activate`
- `Deactivate`

Danger action:

- `Unload`

Visual requirements:

1. `Open` should use a primary blue button.
2. `Load` can use blue or light blue.
3. `Activate` / `Deactivate` should use secondary light buttons.
4. `Unload` should use a light orange/red danger style.
5. Disabled states must be obvious but not harsh.
6. Buttons keep SVG icon + text.
7. Do not show icon-only buttons for now.

Add styles in `ShellTheme.xaml`:

- `PrimaryActionButtonStyle`
- `SecondaryActionButtonStyle`
- `DangerActionButtonStyle`

Existing `LifecycleButtonStyle` / `LifecycleDangerButtonStyle` may be reused or refactored.

## Workspace / Active Module panel

The right panel currently feels like a blank debug region.

Rename the panel title to:

```text
Workspace
```

Subtitle:

```text
Open a loaded module to start working.
```

If `ActiveModuleView` is not null:

1. Header shows `ActiveModuleTitle`.
2. Right side has a `Close` button.
3. A `ContentControl` displays `ActiveModuleView`.
4. Panel has rounded corners and a border.
5. Content area has sensible padding.

If `ActiveModuleView` is null:

Show a better empty state:

1. A large default module/toolbox icon.
2. Main text:

```text
Select a loaded module and click Open.
```

3. Hint text:

```text
Modules are loaded manually and unloaded when no longer needed.
```

Requirements:

1. Empty state should be centered.
2. Avoid meaningless empty white space.
3. Close button should be hidden or disabled when no view is open.
4. Do not change `ActiveModuleView` lifecycle logic.
5. Close still clears `ActiveModuleView`.

## Status message

The status message should look like a small notification bar.

Requirements:

1. Display under the Header.
2. Use light blue or light gray background.
3. Rounded corners around `10`.
4. Keep height compact.
5. If no success/error classification exists yet, use a single style for now.
6. Do not create a complex notification system.

## ShellTheme.xaml cleanup

Add or adjust styles in:

```text
QingToolbox.Shell/Resources/ShellTheme.xaml
```

Possible styles:

- `WorkspacePanelStyle`
- `WorkspaceEmptyStateStyle`
- `ModuleListPanelStyle`
- `ModuleCardCompactStyle`
- `ModuleDetailsExpanderStyle`
- `PrimaryActionButtonStyle`
- `SecondaryActionButtonStyle`
- `DangerActionButtonStyle`
- `SummaryCardStyle`
- `StatusMessageStyle`

Requirements:

1. `MainWindow.xaml` should remain layout-focused.
2. Styles should live in `ShellTheme.xaml`.
3. Do not add WPF-UI, MaterialDesign, or MahApps.
4. Do not add a second SVG library.

## Preserve existing behavior

Must keep:

1. `Refresh Modules` only scans manifests.
2. Shell startup does not load module DLLs.
3. `Load` click is the first point where a module DLL is loaded.
4. `Open` click is the first point where `CreateView()` is called.
5. `Close` clears `ActiveModuleView`.
6. `Unload` clears the corresponding active view before unloading.
7. App exit still unloads modules through `ModuleRuntimeManager`.
8. Shell does not reference `QingToolbox.Modules.Hello`.
9. Shell does not reference `TextTools`.
10. `MainWindowViewModel` does not directly use `InProcessModuleLoader`.
11. `MainWindowViewModel` does not directly access `AssemblyLoadContext`.

## Forbidden

Do not:

1. Switch to the `modules` branch.
2. Modify TextTools module source.
3. Put module functionality into Shell.
4. Make Shell reference any concrete module project.
5. Change `ModuleRuntimeManager` core lifecycle logic.
6. Change `ModuleLoader` loading logic.
7. Implement a plugin store.
8. Implement `.qmod` packaging.
9. Add WPF-UI.
10. Add MaterialDesignThemes.
11. Add MahApps.
12. Commit `bin/`, `obj/`, `.dll`, `.exe`, or `.pdb` files.
13. Use `git push --force`.
14. Rewrite history.

## Build and verification

Run:

```bash
dotnet build
```

If possible, also run:

```bash
dotnet run --project QingToolbox.Shell
```

Manual verification:

1. Shell starts.
2. Sidebar icons are clear and no longer white blocks.
3. Hover expand works.
4. Pin works.
5. Modules selected state works.
6. Refresh Modules works.
7. Module cards are more compact.
8. Paths and manifest details are hidden in Details.
9. Load / Open / Unload button states remain correct.
10. Workspace empty state looks better.
11. Load then Open displays module view.
12. Close clears module view.
13. Unload clears module view before unloading.
14. Refresh and startup still do not load module DLLs.

## Submit

```bash
git status
git add .
git commit -m "[style] redesign shell workspace layout"
git push origin toolbox
```

Never use:

```bash
git push --force
git push -f
```

## Final output

Report:

1. Current branch.
2. Whether `dotnet build` passed.
3. Whether `dotnet run --project QingToolbox.Shell` passed.
4. Main files changed.
5. Whether Sidebar icons were fixed.
6. Whether module cards are compact.
7. Whether debug/path information is hidden in Details.
8. Whether Workspace empty state was improved.
9. Whether Load / Open / Unload still work.
10. Whether startup and Refresh still avoid loading module DLLs.
11. Whether Shell still does not reference concrete modules.
12. Whether no build artifacts were committed.
13. Commit message used.
14. Whether push to `origin/toolbox` succeeded.
15. Latest commit hash.
