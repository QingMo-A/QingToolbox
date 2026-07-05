# Plan 002: Real Shell Navigation and Module Windows

You are a senior C# / WPF / MVVM / desktop software architecture engineer.

Work on the `toolbox` branch of the QingToolbox repository.

The current Shell has a structural UX problem: Home / Modules / Running / Settings only change selected state, but the main content remains the same. Module cards are shown in the wrong place, details are too visible, and module views are embedded in the main window. The user wants real page switching and standalone module child windows.

This plan restructures Shell UX without changing `ModuleLoader`, `AssemblyLoadContext`, or the core lifecycle behavior of `ModuleRuntimeManager`.

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
[style] add real shell navigation and module windows
```

## Current problems

1. Sidebar Home / Modules / Running / Settings only update selected state; they do not switch pages.
2. Module card list appears in the main default view instead of only inside the Modules page.
3. Module cards show too much description, path, manifest, and runtime information by default.
4. Users should choose whether to display detailed module information.
5. Module Open currently embeds the module view into the main window Workspace.
6. The user wants modules to open in standalone child windows.

## Goals

1. Implement real Shell page switching.
2. Home page must not show module cards.
3. Modules page must show module cards.
4. Running page must show loaded/running/open modules.
5. Settings page must show a settings placeholder.
6. Module cards should be compact by default.
7. Module details should be hidden by default and expandable by the user.
8. Opening a module should create a standalone child window.
9. Closing a module child window should release the module view reference.
10. Unloading a module must close the corresponding module child window first.
11. Shell startup and Refresh Modules must still not load module DLLs.
12. Shell must still not reference concrete module projects.
13. Build successfully and push to `origin/toolbox`.

## Navigation design

`MainWindowViewModel` may already have:

```csharp
SelectedNavigationKey = "Modules";
```

Change the default to:

```csharp
SelectedNavigationKey = "Home";
```

Add read-only properties:

```csharp
public bool IsHomeSelected => SelectedNavigationKey == "Home";
public bool IsModulesSelected => SelectedNavigationKey == "Modules";
public bool IsRunningSelected => SelectedNavigationKey == "Running";
public bool IsSettingsSelected => SelectedNavigationKey == "Settings";
```

When `SelectedNavigationKey` changes, notify these properties.

Keep `SelectNavigationCommand`.

Requirements:

1. Home click shows Home page.
2. Modules click shows Modules page.
3. Running click shows Running page.
4. Settings click shows Settings page.
5. Do not keep showing the same page for every navigation item.
6. Home should be the default page.
7. Navigation switching must not load module DLLs.

## MainWindow.xaml page structure

Refactor the main content area.

Suggested root structure:

```text
Root Grid
├─ Sidebar
└─ Main Content
   ├─ Header
   ├─ StatusMessage
   └─ Page Content
      ├─ HomePageContent
      ├─ ModulesPageContent
      ├─ RunningPageContent
      └─ SettingsPageContent
```

Use `Visibility` bindings to show/hide page sections.

You may use WPF built-in `BooleanToVisibilityConverter`:

```xml
<BooleanToVisibilityConverter x:Key="BooleanToVisibilityConverter" />
```

Do not add a third-party UI framework.

## Header behavior

Header title should depend on the selected page:

- Home: `Qing Toolbox`
- Modules: `Modules`
- Running: `Running Modules`
- Settings: `Settings`

Subtitle should also change per page.

`Refresh Modules` may show only on Modules / Running pages, or remain global if it does not dominate the UI.

## Home page

Home must not display module cards.

Home page should show:

1. Welcome title:

```text
Welcome to Qing Toolbox
```

2. Description:

```text
A modular Windows toolbox where tools are loaded only when needed.
```

3. Three small summary cards:

- Installed modules: bind to `TotalModuleCount`.
- Loaded modules: bind to `LoadedModuleCount`.
- Running modules: bind to `RunningModuleCount`.

4. Main button:

```text
Browse Modules
```

Clicking it should execute `SelectNavigationCommand` with parameter `Modules`.

5. Hint:

```text
Modules are discovered from the runtime Modules folder and loaded manually.
```

Requirements:

1. Home is an overview page.
2. Home does not show module detail cards.
3. Home does not create module views.
4. Home does not load module DLLs.

## Modules page

Modules page shows module management UI.

It should include:

1. Summary cards:
   - Total
   - Valid
   - Failed
   - Not Loaded
   - Loaded
   - Running

2. Refresh Modules button.

3. Module card list.

Default module card content:

1. Module icon or default icon.
2. Module name.
3. Version.
4. Runtime State badge.
5. Short description, one or two lines.
6. Action buttons:
   - Load
   - Open
   - Activate
   - Deactivate
   - Unload
7. Short error prompt if there is a manifest or runtime error.

Default module card must hide:

1. RuntimeType.
2. LoadMode.
3. Permissions.
4. Author.
5. Entry.
6. ModuleDirectory.
7. ManifestPath.
8. MinimumHostVersion.
9. Detailed manifest issues.
10. Detailed runtime error.

Place hidden details inside an `Expander` titled:

```text
Details
```

Requirements:

1. Module cards are visually compact by default.
2. The default card should not look like a debug panel.
3. `Details` must use `IsExpanded=false` by default.
4. Users can expand details manually.
5. Action buttons must remain visible.
6. Preserve existing command bindings and lifecycle behavior.
7. Do not change Load / Activate / Deactivate / Unload logic.

## Running page

Running page shows runtime modules.

It should not scan modules again. It should filter the current `Modules` collection.

Display modules where `RuntimeState` is one of:

```text
Loaded
Running
Deactivated
Unloading
Failed
```

Running card content:

1. Module name.
2. RuntimeState.
3. Open button.
4. Deactivate button.
5. Unload button.

If none exist, show empty state:

```text
No modules are currently loaded.
```

Requirements:

1. Running page must not automatically load modules.
2. Running page must not create separate state outside the current runtime status.
3. Running page is only a runtime status view.
4. Close / unload actions still call existing `MainWindowViewModel` commands.

## Settings page

Settings page is a placeholder for now.

Show:

```text
Settings

Qing Toolbox settings will appear here.
```

Optional disabled placeholders:

1. Theme.
2. Module directory.
3. Startup behavior.

Requirements:

1. Do not implement persistent settings yet.
2. Do not change configuration system.
3. Do not add new dependencies.

## Module details hidden by default

Move these fields into the module card `Expander`:

1. RuntimeType.
2. LoadMode.
3. PermissionsText.
4. Author.
5. Entry.
6. ModuleDirectory.
7. ManifestPath.
8. MinimumHostVersion.
9. ErrorSummary.
10. Manifest error list.
11. RuntimeError details.

Main visible area should keep only:

1. Icon.
2. Name.
3. Version.
4. Description.
5. Runtime state.
6. Main actions.

If there is an error, show only a short prompt in the visible card:

```text
Manifest issues found.
```

or

```text
Runtime error occurred.
```

Detailed errors belong inside `Details`.

## Open module as standalone child window

Current logic may do something like:

```csharp
ActiveModuleView = runtimeManager.CreateView(moduleId);
```

Change this behavior. Opening a module must create a standalone WPF child window.

Create:

```text
QingToolbox.Shell/Views/ModuleHostWindow.xaml
QingToolbox.Shell/Views/ModuleHostWindow.xaml.cs
```

Responsibilities:

1. Display module title.
2. Host the module `CreateView()` result.
3. Clear `Content` reference when closing/closed.
4. Do not unload the module.
5. Do not access `ModuleRuntimeManager`.
6. Do not access `AssemblyLoadContext`.
7. Do not reference concrete module types.

Suggested constructor:

```csharp
public ModuleHostWindow(string moduleId, string title, object moduleView)
```

Inside:

```csharp
Title = title;
ModuleId = moduleId;
ModuleContent.Content = moduleView;
```

Add property:

```csharp
public string ModuleId { get; }
```

On Closing or Closed:

```csharp
ModuleContent.Content = null;
```

## ModuleWindowManager service

Recommended new Shell-layer service:

```text
QingToolbox.Shell/Services/ModuleWindowManager.cs
```

Responsibilities:

1. Manage `moduleId -> ModuleHostWindow`.
2. `OpenWindow(moduleId, title, view, owner)`.
3. `CloseWindow(moduleId)`.
4. `CloseAll()`.
5. `IsWindowOpen(moduleId)`.

Requirements:

1. If a module window is already open, focus/activate the existing window instead of creating another one.
2. When the window closes, remove it from the dictionary.
3. When the window closes, clear Content reference.
4. Before unloading a module, close its window.
5. When MainWindow closes, close all module windows.
6. This service is Shell-layer and may reference WPF `Window`.
7. Core / ModuleRuntimeManager must not reference WPF windows.

A concrete class is enough for now:

```csharp
public sealed class ModuleWindowManager
```

Do not make `MainWindowViewModel` messy with all window dictionary logic.

## MainWindowViewModel Open logic

Update `OpenModuleAsync`:

1. Find the module ViewModel.
2. If module is not loaded, do not auto-load; show status message.
3. If the module window is already open, activate existing window.
4. Otherwise call `_runtimeManager.CreateView(moduleId)`.
5. If the returned view is null, show a message that the module did not provide a View.
6. If view is non-null, pass it to `ModuleWindowManager` to open a child window.
7. Do not set `ActiveModuleView`.
8. Do not display module view in main window `ContentControl`.
9. Set status:

```text
Opened module '{module.Name}'.
```

Before `UnloadModuleAsync`:

1. Call `ModuleWindowManager.CloseWindow(moduleId)`.
2. Then call `_runtimeManager.UnloadAsync(moduleId)`.

When MainWindow is closing:

1. Call `ModuleWindowManager.CloseAll()`.
2. Actual unloading remains handled by App.OnExit / ModuleRuntimeManager.

Requirements:

1. `MainWindowViewModel` must not access `AssemblyLoadContext`.
2. `MainWindowViewModel` must not directly use `InProcessModuleLoader`.
3. `MainWindowViewModel` must not hold `LoadedModuleHandle`.
4. `MainWindowViewModel` must not cache module view objects.
5. Module view references are owned by `ModuleHostWindow` and cleared when the child window closes.

## Remove or deprecate ActiveModuleView main-window hosting

If these exist:

```csharp
ActiveModuleView
ActiveModuleTitle
ActiveModuleId
HasActiveModuleView
CloseModuleViewCommand
```

Prefer removing them to avoid confusion.

Remove the right-side main-window Workspace `ContentControl` host from `MainWindow.xaml`.

Replace it with real Home / Modules / Running / Settings page content.

## ShellTheme.xaml styles

Update:

```text
QingToolbox.Shell/Resources/ShellTheme.xaml
```

Possible styles:

- `HomeHeroCardStyle`
- `PagePanelStyle`
- `ModuleCardCompactStyle`
- `ModuleDetailsExpanderStyle`
- `RunningModuleCardStyle`
- `SettingsPlaceholderCardStyle`
- `ModuleHostWindowStyle`
- `WindowTitleBarButtonStyle`

Requirements:

1. Styles stay in `ShellTheme.xaml`.
2. `MainWindow.xaml` should not contain large inline styles.
3. Do not add WPF-UI, MaterialDesign, or MahApps.
4. Do not change the SVG icon library.

## Closing behavior

Check:

```text
QingToolbox.Shell/MainWindow.xaml.cs
QingToolbox.Shell/App.xaml.cs
```

Requirements:

1. MainWindow Closing closes all module child windows.
2. App OnExit still unloads modules through `ModuleRuntimeManager.DisposeAsync()` or `UnloadAllAsync()`.
3. Do not unload modules directly in MainWindow code-behind.
4. Closing a module child window only clears UI references.
5. If the user closes a module child window, the module remains Loaded / Running until Unload is clicked or app exits.

## DI registration

If adding `ModuleWindowManager`, register it in `App.xaml.cs`:

```csharp
services.AddSingleton<ModuleWindowManager>();
```

Inject it into `MainWindowViewModel`.

Do not register concrete module projects.

## Forbidden

Do not:

1. Switch to the `modules` branch.
2. Modify TextTools module source.
3. Put module functionality into Shell.
4. Make Shell reference TextTools.
5. Make Shell reference Hello.
6. Load module DLLs during Shell startup.
7. Load module DLLs during Refresh Modules.
8. Let UI directly use `InProcessModuleLoader`.
9. Let UI access `AssemblyLoadContext`.
10. Change `ModuleLoader` loading logic.
11. Change `ModuleRuntimeManager` core lifecycle logic.
12. Implement a plugin store.
13. Implement `.qmod` packaging.
14. Add WPF-UI.
15. Add MaterialDesignThemes.
16. Add MahApps.
17. Commit `bin/`, `obj/`, `.dll`, `.exe`, or `.pdb` files.
18. Use `git push --force`.
19. Rewrite history.

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

1. Shell starts with Home selected.
2. Home does not show module cards.
3. Clicking Modules shows module cards.
4. Clicking Running shows running modules or an empty state.
5. Clicking Settings shows settings placeholder.
6. Refresh Modules still works.
7. Module cards show core information and actions by default.
8. Paths / manifest / runtime details are hidden in Details.
9. Load changes module state to Loaded.
10. Open creates a standalone module child window.
11. Clicking Open again activates the existing child window instead of creating a duplicate.
12. Closing the module child window does not unload the module.
13. Unload closes the corresponding module child window first.
14. Closing MainWindow closes all module child windows.
15. App exit still unloads loaded modules.
16. Startup and Refresh still do not load module DLLs.
17. Shell still does not reference concrete module projects.

## Submit

```bash
git status
git add .
git commit -m "[style] add real shell navigation and module windows"
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
5. Whether Home / Modules / Running / Settings are truly separated.
6. Whether Home no longer shows module cards.
7. Whether Modules shows module cards.
8. Whether module details are collapsed by default.
9. Whether Open now uses standalone child windows.
10. Whether repeated Open activates an existing window.
11. Whether Unload closes the corresponding module window first.
12. Whether MainWindow close closes all module windows.
13. Whether startup and Refresh still avoid loading module DLLs.
14. Whether Shell still does not reference concrete modules.
15. Whether no build artifacts were committed.
16. Commit message used.
17. Whether push to `origin/toolbox` succeeded.
18. Latest commit hash.
