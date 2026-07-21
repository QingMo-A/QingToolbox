# Plan 003: Hybrid Web UI Modernization and Qing Surface System

You are a senior .NET / WPF / WebView2 / Vue desktop architecture engineer.

Work on the `toolbox` branch of the QingToolbox repository.

This is the approved long-term architecture plan for modernizing the QingToolbox host interface. It defines the target architecture, shared libraries, migration sequence, security boundaries, and acceptance gates.

This plan does **not** authorize a one-shot rewrite of the Shell. Every implementation phase must be delivered through a separate, narrowly scoped Codex task, review, test run, commit, and push.

## Plan status

```text
Status: Not Started
Track: UI Modernization
Branch: toolbox
Current implementation priority: B2.1 runtime lifecycle integration
```

Plan 003 is the architecture source for future Web UI work.

The repository currently treats the B1 recoverable module transaction core as:

```text
Engineering Complete — Frozen
```

The next code priority remains B2.1:

```text
Production lifecycle adapter boundary
Transaction recovery execution gate
Development-only TextTools canary
Disk/runtime version consistency
```

UI documentation may proceed now, but UI code with module lifecycle side effects must not bypass or race B2.1.

## Product objective

QingToolbox should gradually evolve into the following hybrid desktop architecture:

```text
.NET 10 / C#
├─ authoritative application and module state
├─ module discovery, validation, loading, and unloading
├─ download verification and recoverable update transactions
├─ environment isolation, settings, single-instance startup, and recovery
└─ Windows system integration

WPF
├─ native top-level window shells
├─ WindowChrome and Windows window behavior
├─ notification area and floating entry points
├─ fallback and fatal-error surfaces
└─ existing WPF module windows and UserControl hosting

WebView2 + Vue 3
├─ modern host workspace
├─ home, module center, details, settings, and download views
├─ in-window dialogs, overlays, menus, notifications, and motion
├─ content for selected independent modern windows
└─ theme and interaction presentation
```

This plan explicitly rejects replacing the application with a pure Web or Tauri rewrite.

The existing .NET core, module lifecycle, update transaction engine, Windows integration, and WPF module contract remain valuable and must be preserved.

## Non-negotiable architecture principles

1. C# is the only authoritative source of business and runtime state.
2. Vue is a projection of host state, not an independent runtime state machine.
3. Vue must not declare Load, Activate, Deactivate, Unload, Install, Update, or Recovery success before C# confirms it.
4. WPF/C# owns real Windows top-level window behavior.
5. Vue owns modern presentation and most interaction inside WebView2 content.
6. Pages express user intent; they do not select renderer classes, arbitrary routes, paths, or Windows APIs.
7. Shared visual, surface, communication, error, and operation behavior must be abstracted instead of copied between pages.
8. Existing pages migrate incrementally. Do not delete the old Shell in one change.
9. WebView2 or one independent Web surface failing must not terminate the host core.
10. Production, Development, and ModuleTest remain isolated.
11. Web UI commands must pass the same module execution and recovery gates as native commands.
12. Existing module views remain WPF `UserControl` instances hosted by WPF module windows.
13. The Shell must never reference concrete module projects.
14. Module scanning and read-only UI refresh must not load module DLLs.
15. UI modernization must not weaken B1 transaction, staging, path, identity, or recovery guarantees.

## Shared foundation model

The modern UI must be built on four parallel foundations:

```text
Qing Design System
Defines how the product looks.

Qing Surface System
Defines how an interaction appears: overlay, native window, hybrid window, or system dialog.

Qing Bridge
Defines how Vue requests host work and receives events.

Qing Contracts
Defines the versioned C# / TypeScript protocol.
```

C# services remain responsible for actual operations and authoritative state.

These foundations must remain separated. Do not create one giant framework that mixes styling, window creation, host commands, stores, and business logic.

# Part I: Qing Design System

## Design Token single source

Plan a neutral, shared token source, for example:

```text
QingToolbox.Design/
└─ tokens.json
```

The token source should eventually generate:

```text
tokens.json
├─ Vue CSS variables
├─ TypeScript token constants
├─ Tailwind theme integration
└─ WPF ResourceDictionary values
```

The token model should cover at least:

- application background;
- base, raised, overlay, hover, and selected surfaces;
- borders and separators;
- brand color;
- primary, secondary, muted, and disabled text;
- success, warning, danger, and information states;
- typography families, sizes, weights, and line heights;
- spacing scale;
- control height and density scale;
- corner radius scale;
- shadows and backdrop behavior;
- animation duration and easing;
- z-index and overlay levels;
- dialog, panel, and floating-window sizing rules.

Do not maintain separate handwritten theme constants for WPF and Vue.

Pages and feature components must not scatter arbitrary:

- hexadecimal colors;
- one-off radii;
- one-off shadows;
- one-off animation durations;
- unregistered z-index values.

## Qing UI shared component library

Qing UI is the reusable visual component layer. It must not know module loading, download verification, or settings persistence details.

Planned primitives and components include:

```text
QButton
QIconButton
QInput
QTextarea
QSelect
QSwitch
QSlider
QCheckbox
QTooltip
QPopover
QDialog
QAlertDialog
QDrawer
QSheet
QContextMenu
QToast
QProgress
QSpinner
QBadge
QCard
QPanel
QDivider
QEmptyState
QSkeleton
QLoadingOverlay
```

Planned layout components include:

```text
QPage
QPageHeader
QPageContent
QSection
QSectionHeader
QSettingsSection
QSettingsRow
QSidebarLayout
QDetailLayout
QSplitView
```

All reusable interactive components must define and test:

```text
Default
Hover
Pressed
Focus Visible
Disabled
Loading
Error
Reduced Motion
Keyboard interaction
Accessibility attributes
Dark theme
Light theme
```

Reka UI may be used internally as an unstyled interaction foundation.

The dependency direction should be:

```text
Pages and features
→ Qing UI
→ Reka UI
```

Pages must not repeatedly import low-level Reka primitives to build separate Dialog, Menu, Toast, Select, or Tooltip implementations.

Pages must not recreate buttons with copied Tailwind class lists when a Qing UI component exists.

## Feature component boundaries

Business-aware components stay inside their feature folders.

Examples:

```text
features/modules/
├─ ModuleCard
├─ ModuleStateBadge
├─ ModuleActionBar
├─ ModuleVersionInfo
├─ ModuleUpdateBanner
└─ ModuleRecoveryNotice

features/downloads/
├─ DownloadTaskCard
├─ DownloadProgressBar
├─ DownloadSpeedLabel
└─ DownloadFailureNotice

features/settings/
├─ SettingsSection
├─ SettingsRow
├─ StartupStatusCard
└─ ThemeSelector
```

State color, icon, label, and severity mapping must be centralized.

Pages must not separately decide:

- the color of `Active`;
- the icon for `Failed`;
- the warning treatment for `RecoveryRequired`;
- the retryability or presentation of a host error.

# Part II: Qing Surface System

## Surface definition

A Surface is any interaction layer presented to the user.

Examples include:

```text
Toast
Tooltip
Popover
Context Menu
Dialog
Alert Dialog
Drawer
Sheet
Command Palette
Loading Overlay
Floating Panel
Tool Window
Diagnostics Window
Module Window
File Picker
Folder Picker
System Dialog
```

The Qing Surface System must have four implementation layers:

```text
Web Overlay Layer
Native Window Layer
Hybrid Web Window Layer
System Dialog Layer
```

The calling page expresses a typed intent. The Surface policy selects the implementation.

## Web Overlay Layer

Use Vue for most surfaces that remain inside the main WebView2 content:

```text
QDialog
QAlertDialog
QToast
QPopover
QTooltip
QContextMenu
QDrawer
QSheet
QCommandPalette
QLoadingOverlay
```

Plan one global API, for example:

```ts
const surface = useSurface()

const accepted = await surface.confirm({
  title: 'Unload module',
  description: 'Module data will be preserved.',
  confirmText: 'Unload',
  tone: 'danger'
})
```

Pages must not each maintain separate copies of:

```text
isDialogOpen
dialogTitle
dialogDescription
resolvePromise
overlayZIndex
focusTrap
focusRestore
```

Those behaviors belong to the shared Surface store, overlay host, and Qing UI components.

Most dialogs, toasts, menus, tooltips, sheets, and command palettes inside the main workspace should be Vue surfaces.

## Native Window Layer

Real Windows top-level behavior remains in C#/WPF.

Plan a Shell service similar to:

```csharp
public interface IWindowSurfaceService
{
    Task<SurfaceResult> OpenAsync(
        SurfaceRequest request,
        CancellationToken cancellationToken);

    Task<bool> CloseAsync(
        SurfaceId surfaceId,
        CancellationToken cancellationToken);
}
```

The native window layer owns:

```text
Owner
TopMost
ShowInTaskbar
Alt+Tab participation
Window size and constraints
Window position
Multi-monitor placement
DPI behavior
Dragging
Activation
Focus
Closing
Singleton and deduplication policy
State persistence
Lifecycle cleanup
```

Business code should use typed requests such as:

```text
OpenModuleWindowRequest
OpenFloatingDownloadRequest
OpenDiagnosticsWindowRequest
OpenUpdateProgressWindowRequest
```

Do not expose a generic API that lets Vue choose arbitrary WPF window classes, routes, owner handles, or flags.

## Hybrid Web Window Layer

Independent modern windows should use a shared host:

```text
QWebSurfaceWindow
├─ WPF native Window shell
├─ WebView2
└─ approved Vue Surface route
```

The shared host should eventually own:

- WebView2 initialization;
- approved Surface route selection;
- Bridge session creation;
- theme synchronization;
- locale synchronization;
- Window policy;
- browser process failure recovery;
- navigation restrictions;
- environment isolation;
- User Data Folder isolation;
- close and disposal cleanup.

Appropriate uses include:

```text
Independent download floating window
Desktop status floating window
Diagnostics window
Update progress window
Notification-area expansion panel
Independent module-details window
```

Vue controls the content and visual presentation.

WPF/C# controls Windows window behavior.

Do not write a separate WebView2 initialization, security, theme, and Bridge stack for every independent window.

## Floating-window abstraction

Plan reusable floating-window infrastructure:

```text
QFloatingWindowHost
FloatingSurfaceDefinition
FloatingWindowPolicy
FloatingWindowStateStore
```

The native host owns:

```text
TopMost
ShowInTaskbar
Owner
Multi-monitor behavior
DPI
Dragging
Optional edge snapping
Focus policy
Close policy
Position persistence
```

The Vue surface owns:

```text
Card layout
Modern translucent or solid visual treatment
Corners
Icons
Progress animation
Expand and collapse behavior
Hover feedback
State transitions
Theme
```

Do not build a new WPF window-management implementation for each floating tool.

## System Dialog Layer

Continue to use Windows-native dialogs for capabilities where system integration matters:

```text
Open file
Save file
Select folder
System permission confirmation
Credentials
Required system-level error presentation
```

Plan one typed service, for example:

```csharp
public interface ISystemDialogService
{
    Task<FilePickResult> PickFileAsync(...);
    Task<FolderPickResult> PickFolderAsync(...);
    Task ShowErrorAsync(...);
}
```

Vue only calls the typed Bridge client:

```ts
await host.dialogs.pickFolder()
```

Vue does not need to know the selected Windows API implementation.

## Surface Intent and registry

Plan typed intents, for example:

```ts
type SurfaceIntent =
  | 'confirm'
  | 'module-details'
  | 'download-details'
  | 'settings-dialog'
  | 'floating-download'
  | 'floating-status'
  | 'diagnostics-window'
  | 'module-window'
  | 'file-picker'
  | 'folder-picker'
```

The host policy maps intent to implementation:

```text
confirm
→ Web Overlay

module-details
→ Web Overlay or approved detail route

floating-download
→ Hybrid Web Window

diagnostics-window
→ Hybrid Web Window

module-window
→ WPF ModuleWindow

file-picker
→ Windows System Dialog
```

Plan the registry flow:

```text
Surface Intent
→ Surface Definition
→ Surface Policy
→ Renderer
```

Allowed renderer kinds:

```text
WebOverlay
HybridWebWindow
NativeWpfWindow
SystemDialog
```

A Surface definition should eventually describe:

```text
Intent
Renderer
Approved route
Singleton policy
Window policy
Permission policy
Environment policy
Theme policy
Focus policy
Close policy
```

Vue must not choose:

- renderer;
- WPF window class;
- arbitrary Vue route;
- TopMost;
- Owner;
- ShowInTaskbar.

This is intent abstraction, not exposure of window implementation parameters.

# Part III: Qing Bridge

## Single Bridge entry

Only the Bridge transport may directly access:

```ts
window.chrome.webview
```

All other frontend code must use typed clients:

```ts
await host.modules.load(moduleId)
await host.surfaces.openModuleDetails(moduleId)
await host.dialogs.pickFolder()
```

Plan this structure:

```text
bridge/
├─ transport/
│  ├─ WebViewTransport
│  └─ MockTransport
├─ protocol/
│  ├─ RequestClient
│  ├─ EventDispatcher
│  ├─ ErrorMapping
│  └─ TimeoutPolicy
└─ clients/
   ├─ AppClient
   ├─ ModuleClient
   ├─ SettingsClient
   ├─ DownloadClient
   ├─ SurfaceClient
   ├─ WindowClient
   └─ DiagnosticsClient
```

The Bridge foundation should own:

```text
Request ID
Protocol version
Timeout
Cancellation
JSON serialization
Response matching
Host events
Structured error mapping
Log redaction
WebView reconnection
Mock transport
Surface session
```

Pages must not directly call:

```ts
window.chrome.webview.postMessage(...)
window.open(...)
```

## C# command handlers

Do not grow one unbounded message `switch` in the Shell.

Plan typed handlers:

```csharp
IWebCommandHandler<TRequest, TResponse>
```

The shared host Bridge owns:

```text
Navigation and source validation
Protocol version validation
Command allowlist
Request ID validation
DTO deserialization
Payload validation
Environment permission checks
Handler lookup
CancellationToken propagation
Structured error mapping
Safe logging
Response serialization
```

Each handler calls existing services. It must not copy module loading, unloading, download, settings, or Surface behavior.

# Part IV: Qing Contracts

## Versioned contracts

Plan these base message categories:

```text
Request
Response
Event
Snapshot
Error
Surface Request
Surface Result
```

A Request should contain at least:

```text
protocolVersion
requestId
command
payload
```

A Response should contain at least:

```text
protocolVersion
requestId
success
payload
error
```

Do not expose:

```text
C# service objects
AssemblyLoadContext
WPF Window
SafeHandle
Journal
Transaction locks
Absolute paths
Arbitrary assemblies
Arbitrary command lines
Reflection method names
```

Prefer:

```text
C# DTO or neutral schema
→ generated TypeScript contracts
```

Contract changes must either remain compatible or increase the protocol version, and must be covered by contract tests.

# Part V: Shared abstractions and state

## Qing Shared

Plan reusable composables:

```text
useHostOperation
useHostEvent
useSurface
useToast
useDialog
useConfirm
useTheme
useLocale
useReducedMotion
useKeyboardShortcut
useElementSize
usePersistentUiState
```

Do not create unbounded dumping-ground files such as:

```text
utils.ts
helpers.ts
common.ts
```

Use this extraction rule:

```text
Infrastructure known to be global
→ abstract from the first implementation

Ordinary business code
→ implement clearly once
→ compare semantics on the second occurrence
→ extract after the third equivalent use
```

Similar-looking code with different business meaning must not be merged merely to reduce line count.

## State authority

The state model is:

```text
C# = authoritative state
Pinia = frontend projection
```

Potential stores include:

```text
appStore
moduleStore
downloadStore
settingsStore
notificationStore
surfaceStore
windowStore
```

Correct flow:

```text
Vue sends Command
→ C# validates and executes
→ C# returns Response or pushes Event
→ Pinia updates the projection
```

Forbidden flow:

```text
Vue first sets a module to Loaded
→ asks C# to make the assumption true
```

WebView2 initialization and reload should follow:

```text
web.ready
→ C# sends app.snapshot
→ Vue rebuilds projected state
```

An independent Hybrid Surface may have a Surface session, but business facts still come from C#.

## Proposed frontend structure

Start with logical separation inside one WebUI project:

```text
QingToolbox.WebUI/
└─ src/
   ├─ app/
   ├─ design-system/
   │  ├─ tokens/
   │  ├─ styles/
   │  ├─ icons/
   │  ├─ motion/
   │  ├─ primitives/
   │  ├─ components/
   │  └─ layouts/
   ├─ surfaces/
   │  ├─ registry/
   │  ├─ policies/
   │  ├─ overlays/
   │  ├─ floating/
   │  ├─ windows/
   │  └─ composables/
   ├─ bridge/
   │  ├─ transport/
   │  ├─ protocol/
   │  ├─ clients/
   │  ├─ events/
   │  └─ mock/
   ├─ contracts/
   ├─ shared/
   ├─ features/
   │  ├─ modules/
   │  ├─ downloads/
   │  ├─ settings/
   │  └─ diagnostics/
   └─ pages/
```

Dependency direction:

```text
pages
↓
features
↓
surfaces / design-system / bridge / shared / contracts
```

Forbidden dependencies:

```text
design-system → features
shared → pages
bridge → concrete page or Pinia implementation
contracts → Vue components
surface registry → concrete business stores
```

Do not create a complex npm monorepo before a second real consumer exists. Split packages only when reusable consumers justify it.

# Part VI: Migration roadmap

## B2.1 dependency

B2.1 remains the first code priority.

It must establish:

```text
Real IModuleUpdateRuntimeCoordinator
Module-window close behavior
Deactivate
Unload
VerifyUnloaded
Previous runtime-intent restoration
Transaction recovery execution gate
Development-only TextTools canary
Disk/runtime version consistency
```

Before B2.1 is stable, Vue must not trigger:

```text
Load
Activate
Deactivate
Unload
Module replacement
Automatic module update
```

Static Design System exploration may proceed independently, but it must not bypass runtime boundaries.

## UI-0: Architecture plan

```text
Status: Engineering Complete when Plan 003 is approved and committed
```

UI-0 changes planning only. It adds no runtime behavior.

## UI-1: Development-only Web Shell

```text
Status: Not Started
Priority: P1 after B2.1 Shell lifecycle interfaces are stable
```

Planned stack:

```text
Vue 3
TypeScript
Vite
Tailwind CSS
Reka UI
Pinia
Vue Router
Motion
Lucide
```

The first Development-only prototype should contain only:

```text
WebView2 initialization
Bundled local resources
Environment name
Host version
Module count
Bridge Ping/Pong
C# event push
WebView reload recovery
Mock transport
Release resource packaging
WebView2 Runtime missing handling
```

Initial window structure:

```text
WPF MainWindow
├─ native WindowChrome
├─ native window behavior
└─ WebView2
   └─ Vue content area
```

Do not rewrite non-client behavior, Snap Layout, or the system menu in UI-1.

## UI-2: Design System, Surface System, and read-only module center

```text
Status: Not Started
Priority: P1
```

Planned scope:

```text
Design Tokens
Qing UI
Web Overlay Layer
Surface Store
Surface Registry prototype
Qing Bridge
Qing Contracts
Home shell
Sidebar
Read-only module list
Module details
State badges
Toast
Dialog
Drawer
Command Palette
Theme preview
```

UI-2 may read C# state only.

It must not perform:

```text
Module Load
Module Unload
Module deletion
qmod installation
Settings writes
Arbitrary filesystem changes
```

## UI-2.5: Development-only Hybrid Surface prototype

```text
Status: Not Started
Priority: P1
```

Use one isolated prototype:

```text
Diagnostics window
or
Simulated download floating window
```

Validate:

```text
WPF native window shell
WebView2 content
Approved Vue route
Theme synchronization
Surface session
Multiple monitors
DPI
TopMost policy
Focus
Close behavior
Browser process failure recovery
Environment isolation
```

Do not connect the prototype to real Production update behavior.

## UI-3: Module lifecycle operations

```text
Status: Blocked by B2.1
Priority: P2
```

Future scope:

```text
Open module
Load
Activate
Deactivate
Unload
Module-window state
RecoveryRequired
Operation progress
Cancellation
Structured errors
```

Existing module content remains:

```text
WPF ModuleWindow
+
WPF UserControl
```

Vue provides the entry point and state projection, not the module view renderer.

## UI-4: Settings, home, downloads, updates, and floating surfaces

```text
Status: Blocked by earlier UI phases and Stage B Production boundaries
Priority: P2
```

Suggested migration order:

```text
Settings
Home
Download tasks
Independent download floating window
Update checks
Update details
Diagnostics
```

Automatic module installation must not be exposed until the complete Production-safe Stage B boundary exists.

## UI-5: Default Shell migration evaluation

```text
Status: Deferred
Priority: P3
```

Only after prior phases, evaluate:

```text
Whether Vue owns the full sidebar
Whether Vue draws the visual title bar
Whether WebView2CompositionControl is needed
Whether old WPF pages can be removed
Whether Production Web Shell should be enabled
Which native fallback pages remain
Whether Vue becomes the default host workspace
```

Do not promise complete WPF removal in earlier phases.

# Part VII: Recommended Surface mapping

Use this default policy unless a later reviewed plan changes it:

| User-facing surface | Default implementation |
|---|---|
| Main-window confirmation | Vue Web Overlay |
| Toast / Tooltip / Popover | Vue Web Overlay |
| Drawer / Command Palette | Vue Web Overlay |
| Module details | Vue route or Web Overlay |
| Independent download floating window | WPF shell + Vue |
| Desktop status floating window | WPF shell + Vue |
| Independent diagnostics window | WPF shell + Vue |
| Existing module window | WPF ModuleWindow + UserControl |
| File and folder selection | Windows System Dialog |
| Notification-area icon | Native C#/WPF |
| Notification-area expansion panel | Hybrid Web Window or native WPF after evaluation |

# Part VIII: WebView2 security and isolation

Production Web UI must load only bundled local application resources by default.

Default-deny:

```text
Arbitrary external navigation
Unapproved New Window requests
Arbitrary downloads
Arbitrary Host Objects
Arbitrary file-path parameters
Arbitrary assembly execution
PowerShell or command-line execution
Remote websites invoking host commands
Production CDN scripts
```

Commands and Surface intents must use allowlists.

Vue may send business identifiers such as:

```text
moduleId
settingKey
downloadTaskId
surfaceIntent
```

Vue must not send host-trusted:

```text
Absolute paths
WPF window classes
Arbitrary Vue routes
Assembly names
Command lines
Reflection targets
```

Production, Development, and ModuleTest require isolated:

```text
WebView2 User Data Folder
Caches
Bridge debugging policy
DevTools policy
Surface state
```

Production should disable browser capabilities that are not required by the product.

# Part IX: Native fallback and recovery

The application must retain native fallback surfaces for:

```text
Fatal Shell errors
WebView2 initialization failure
Missing WebView2 Runtime
Corrupt bundled Web resources
Open diagnostics
Open log location
Close application
Development reload
```

Failure of an independent Surface should close or recover that Surface without terminating the main Shell.

Do not delete all old WPF pages at once.

A migrated page may replace its old implementation only after verification of:

```text
Functional equivalence
State consistency
Keyboard behavior
Accessibility
DPI behavior
Installer packaging
WebView2 reload and process recovery
Navigation security
```

# Part X: Future tests

Plan these test groups:

```text
Contract Tests
Bridge Tests
Design System Tests
Surface Tests
Host Integration Tests
Page State Tests
```

They should cover at least:

```text
C# and TypeScript field parity
Enum and error-code parity
Surface Intent mapping
Unknown Intent rejection
Request IDs
Timeout
Cancellation
WebView reload
Mock transport
Token generation parity
WPF and CSS theme parity
Focus trap and focus restore
Reduced Motion
Multiple monitors
High DPI
Window singleton behavior
TopMost and Owner policy
Navigation rejection
New Window rejection
Browser process failure
Production DevTools policy
Independent Surface isolation from the main Shell
```

# Part XI: Explicit non-goals

Plan 003 does not authorize:

```text
Rewriting QingToolbox in Tauri
Deleting the .NET core
Deleting WPF
Changing the existing WPF module UI contract
Embedding WPF UserControl content into the Web DOM
Ordinary-user automatic module update
A Production module transaction button
A Web module marketplace
Remote websites invoking host capabilities
Production CDN dependencies
Migrating every page in one task
Allowing pages to create arbitrary windows
Allowing pages to provide arbitrary routes
Allowing pages to choose TopMost or Owner
A complex npm monorepo before it has consumers
Large collections of unused abstraction interfaces
Host self-update
```

# Part XII: Execution order

```text
P0  B2.1 Runtime Adapter + Recovery Gate + Canary
P0  UI-0 Plan 003 architecture documentation
P1  UI-1 Development-only Web Shell
P1  UI-2 Design System + Surface System + read-only module center
P1  UI-2.5 Development-only Hybrid Surface
P2  UI-3 module lifecycle Web UI
P2  Stage B Production-safe integration
P2  UI-4 settings, home, downloads, updates, and floating surfaces
P3  UI-5 default Shell migration evaluation
P3  optional Web module protocol in Module API/SDK
P4  host self-update
```

B2.1 and UI-1 must not both perform large, uncontrolled changes to the Shell startup lifecycle.

Stabilize the Recovery Gate first, then integrate Web Shell startup.

Static Design System work may proceed in parallel, but commands with side effects wait for stable C# services.

# Part XIII: Status vocabulary

Use only:

```text
Not Started
In Progress
Engineering Complete
Blocked
Deferred
Archived
```

Do not claim Production completion because a prototype exists.

B1 remains:

```text
Engineering Complete — Frozen
```

Unexecuted Preview 2 manual acceptance remains:

```text
Not Run
```

UI modernization is not evidence that Preview 2 manual acceptance passed.

# Part XIV: Rules for follow-up plans

Plan 003 is a master architecture plan. Do not execute all phases in one Codex task.

Each implementation phase must receive a separate numbered plan that references Plan 003 instead of copying it.

Suggested follow-up plans, subject to the repository's actual next plan number:

```text
Plan 004: Development-only Web Shell Foundation
Plan 005: Qing Design and Overlay Foundations
Plan 006: Hybrid Surface Prototype
Plan 007: Module Lifecycle Web UI Integration
```

Every follow-up plan must define:

```text
Scope
Non-goals
Dependencies
Security boundary
Files expected to change
Tests
Acceptance criteria
Commit and push rules
```

# Definition of Plan 003 completion

The Plan 003 documentation task is complete when:

```text
This file exists in plans/
plans/README.md indexes it
Plan 001 and Plan 002 remain unchanged
No runtime code changes
No dependency changes
No product behavior changes
```

This does **not** mean:

```text
WebView2 is integrated
Vue is scaffolded
Qing Design System is implemented
Qing Surface System is implemented
Production UI is migrated
```

## Branch and repository safety

This plan is for the `toolbox` branch only.

Do not execute it on `modules`.

Do not:

```text
git reset --hard
git clean -fd
git commit --amend
git push --force
git push --force-with-lease
--no-verify
```

Do not commit:

```text
bin/
obj/
artifacts/
publish/
node_modules/
dist/
*.dll
*.exe
*.pdb
*.qmod
*.zip
logs
runtime journals
real user settings
real caches
```

Every future implementation plan must fetch and compare `HEAD` with `origin/toolbox` before modifying the repository.