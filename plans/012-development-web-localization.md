# Plan 012: Development Web Localization

```text
Status: Active — User Approved; prerequisite P1 pending
Track: UI Modernization / UI-5
Environment: Development only
```

Depends on:

- UI-1 Engineering Complete — Frozen
- UI-2A / Plan 009 Implementation Complete
- UI-3 / Plan 010 Implementation Complete
- UI-4A / Plan 011 Implementation Complete
- Existing native localization through `LocalizationManager`
- Existing protocol-v4 Web Shell session and immutable asset-serving boundaries

## 1. Product problem

The Development Web workspace currently reads the host language as display-only metadata, while the Vue workspace itself is still written with hard-coded English strings.

The native WPF workspace already supports:

```text
Follow system
Simplified Chinese (`zh-CN`)
English (`en-US`)
```

Changing the native language updates the persisted user setting, native Shell text, module localization, and open module-window localization. The Development Web workspace does not yet expose the same user choice and does not reactively translate its own pages.

This creates three visible inconsistencies:

1. The Settings page shows the host language but cannot change it.
2. Selecting Chinese in the native workspace does not make the Development Web workspace Chinese.
3. Module names and descriptions may follow the host language while surrounding Web UI text remains English.

## 2. Product goal

Deliver one coherent language preference across the native host, Development Web workspace, and localized module metadata without replacing the existing localization authority.

A user must be able to select:

```text
Follow system
简体中文
English
```

After a successful host-confirmed change:

- the setting is persisted by the existing host localization system;
- the Vue workspace updates immediately without restarting;
- module names and descriptions are refreshed from a new authoritative Module Snapshot;
- native WPF and open module windows continue to use the existing localization path;
- failure leaves the last confirmed language and UI state intact.

## 3. Why this is a numbered Plan

This work is larger than a single Settings mutation because it crosses:

```text
host settings persistence
Settings Snapshot contract
Development-only Web command handling
Vue application-wide reactive state
all user-visible pages
module localization refresh
Mock behavior and tests
```

It requires multiple independently reviewable commits and affects the whole Development Web product surface. It therefore qualifies as a new UI phase rather than an ordinary Bug or isolated button task.

## 4. Mandatory pre-plan defect fix

Before implementing any Plan 012 slice, fix the existing Development Web module-summary counting defect as an independent P1 task.

That defect is not a Plan 012 slice and must not expand the localization implementation.

Required summary semantics:

```text
Total
- every module in the authoritative Module Snapshot

Valid
- `isValid === true`

Issues
- invalid modules
- modules with `errorCount > 0`
- modules whose runtime state is `Failed`

Not loaded
- `NotLoaded`
- `Unloaded`

Loaded
- `Loaded`
- `Deactivated`

Running
- `Running`
```

Lifecycle summaries must always be derived from the latest complete host-confirmed Module Snapshot. The frontend must not optimistically increment or decrement counters.

The recommended independent commit is:

```text
[fix] correct Development Web module state summaries

[test] cover complete lifecycle state transitions
```

Completing this prerequisite does not change Plan 012 from Draft to Active.

## 5. Architecture boundary

### 5.1 Host authority

The host remains authoritative for:

```text
configured language code
resolved effective language
persistence
native WPF localization
module localization registration
localized module names and descriptions
```

Plan 012 must reuse the existing `LocalizationManager` and user settings. It must not create a second host language setting or duplicate native localization resources inside C# Web contracts.

### 5.2 Vue authority

Vue owns only Development Web interface strings such as:

```text
navigation labels
page headings
button labels
empty states
status notices
Toast messages
Quick Open labels
accessibility labels
```

Vue must not become authoritative for module runtime state, module localization files, host settings, or native WPF strings.

### 5.3 Contract boundary

The Settings Snapshot may project the minimum language state required by the Web workspace, including conceptually:

```text
configured language code
resolved effective language code
current display name
supported language choices
```

Exact field names must be reviewed in the first slice and kept small.

The contract must not expose:

```text
localization file paths
arbitrary resource keys
complete native resource dictionaries
module localization directories
filesystem lookup rules
culture internals unrelated to the UI
```

Protocol version remains v4 unless an actual compatibility requirement proves otherwise.

## 6. Supported languages

Initial supported values are limited to:

```text
system
zh-CN
en-US
```

Rules:

- `system` is the persisted user choice for following the operating-system language.
- The host also projects the resolved effective language used for the current session.
- Unsupported or malformed values are rejected by the host.
- The Vue workspace falls back to English for a missing translation key.
- Adding additional languages later requires complete resource coverage and tests; it is not part of this Plan.

## 7. Delivery slices

Plan 012 is delivered in three bounded slices. Each slice requires a separate ordinary commit and review before the next begins.

---

## UI-5A1: Host-confirmed language setting

### Goal

Expose the existing host language preference in Development Web Settings without translating the whole Vue workspace yet.

### Scope

Implement:

```text
Settings Snapshot language projection
supported language choices
`settings.setLanguage`
SettingsClient method
Settings Store mutation state
Development Web language selector
Mock transport behavior
host and frontend tests
```

### Command semantics

Add one Development-only command:

```text
settings.setLanguage
```

Payload shape:

```json
{
  "languageCode": "system | zh-CN | en-US"
}
```

Requirements:

- Activated Web session required.
- Reject missing, unsupported, incorrectly typed, or extra payload fields.
- Reuse the existing host localization manager and persistence path.
- Do not optimistically update the Settings Snapshot.
- Return one complete, host-confirmed Settings Snapshot.
- On failure, preserve the previous confirmed Snapshot.
- Return fixed safe Web errors; do not expose file paths, resource-loading exceptions, stack traces, or unsupported culture internals.
- Native WPF language behavior must continue to work.

### Settings UI

Replace the current read-only language card with a real selector for:

```text
Follow system
简体中文
English
```

The selector must:

- show the configured preference, not guess from browser language;
- show the resolved current language when `system` is selected;
- be disabled while disconnected, loading, or saving;
- use host-confirmed state after success;
- preserve the previous selection after failure;
- show safe success and failure feedback;
- remain keyboard and screen-reader accessible.

### UI-5A1 non-goals

Do not yet:

```text
translate every Vue page
add a new i18n package without reviewing the UI-5A2 design
refresh module localization automatically
add arbitrary locale import support
modify native module localization formats
```

### Suggested commit

```text
[+] add host-confirmed Development Web language selection

[test] cover language settings persistence and validation
```

---

## UI-5A2: Reactive Vue localization foundation

### Goal

Create one maintainable localization path for Development Web interface strings and apply it to the application shell.

### Scope

Implement a small project-wide localization layer covering:

```text
App shell
Sidebar navigation
page route titles
Quick Open
common buttons
common status labels
common accessibility labels
Toast primitives where applicable
```

### Design rules

Use one reactive translation entry point such as:

```text
t(key)
t(key, parameters)
currentLocale
```

The implementation must:

- react to the host-confirmed effective language;
- support parameter substitution needed by current UI text;
- use one source of truth for each translation key;
- provide deterministic English fallback;
- surface missing keys during tests or development;
- avoid copying the same translations into individual components;
- avoid direct browser-language authority when the host has already resolved `system`.

A standard Vue localization dependency may be introduced only if it clearly reduces custom infrastructure and is limited to this purpose. Otherwise use a small typed project-local adapter. Do not create a custom template language, ICU implementation, remote translation loader, or runtime localization compiler.

### Resource organization

Use explicit resources for:

```text
zh-CN
en-US
```

Keep keys grouped by product surface, for example:

```text
common.*
navigation.*
quickOpen.*
home.*
modules.*
running.*
logs.*
settings.*
diagnostics.*
```

Do not use whole English sentences as keys.

### Application shell coverage

At minimum translate:

```text
Home
Modules
Running modules
Session logs
Settings
Development diagnostics
Quick Open
search placeholders
keyboard hints
common Refresh / Retry / Details / Close actions
bridge and snapshot notices shared by shell components
```

### UI-5A2 non-goals

Do not yet translate every page-specific paragraph or every module-operation message. Those belong to UI-5A3.

Do not change:

```text
Router structure
Bridge session protocol
module runtime
native fallback
Production Web UI
```

### Suggested commit

```text
[+] add reactive localization to the Development Web shell

[test] cover locale switching and translation fallback
```

---

## UI-5A3: Complete Development workspace localization

### Goal

Translate every currently delivered Development Web workspace and keep host-localized module metadata synchronized.

### Required page coverage

Translate user-visible text in:

```text
Home
Modules
Running modules
Session logs
Settings
Development diagnostics
Quick Open
QEmptyState content
snapshot and disconnected notices
module operation success and failure Toasts
startup and window preference feedback
accessibility names and descriptions
```

### Module localization refresh

After a successful language change:

1. accept the complete host-confirmed Settings Snapshot;
2. update the Vue effective locale;
3. request one new Module Snapshot through the existing Module Client;
4. replace module data only after the full Snapshot validates;
5. preserve the previous Module Snapshot if refresh fails;
6. show safe feedback that interface language changed while module metadata could not be refreshed;
7. do not repeat the request, poll, or reload the entire WebView.

This refresh exists only to obtain newly localized host module names and descriptions. The Vue layer must not translate module manifest strings itself.

### Translation completeness

For both `zh-CN` and `en-US`:

- no major page heading remains in the wrong language;
- no primary action remains hard-coded;
- no normal empty state remains hard-coded;
- no ordinary success or failure Toast remains hard-coded;
- pluralized module and log counts are grammatically acceptable;
- date and time display uses the active effective locale where practical;
- internal runtime identifiers such as `NotLoaded` may still exist in contracts but should be presented through localized display labels.

### Suggested commit

```text
[+] localize the Development Web workspaces

[test] cover complete Chinese and English workspace text
```

## 8. Testing strategy

### Host and contract tests

Cover:

```text
supported language choices
system / zh-CN / en-US persistence
unsupported value rejection
extra payload field rejection
same-value no-op behavior
complete Settings Snapshot after mutation
failure preserving the previous setting
native localization refresh path
safe Web errors
```

### Frontend unit tests

Cover:

```text
initial locale from Settings Snapshot
system choice using host-resolved locale
reactive switching without restart
English fallback
parameter interpolation
missing-key detection
Settings selector busy and failure behavior
Quick Open and Sidebar localization
page heading localization
Toast localization
module runtime-state display localization
module Snapshot refresh after language change
refresh failure preserving prior module data
```

### Mock behavior

Mock transport must:

```text
validate exact language payloads
update configured and resolved language coherently
return complete Settings Snapshots
support failure without optimistic UI mutation
provide deterministic system-language behavior for tests
```

### Manual checks

Check at least:

```text
Follow system with Chinese OS preference
Follow system with English OS preference
explicit Simplified Chinese
explicit English
switch Chinese → English → system without restart
module names and descriptions after switching
open module windows continuing to localize through native behavior
1440×900
1280×720
900×720
650×720
light theme
dark theme
```

## 9. Scope and file guidance

Likely affected areas across the three slices include:

```text
existing native localization manager adapter
MainWindowViewModel language mutation adapter
Web Settings Snapshot provider
Development Web Settings command handler
Web Settings smoke tests
Settings contract / client / store / Mock transport
Vue localization resources and adapter
App shell and current pages
page-specific tests
CHANGELOG
```

Each slice must keep its own practical file list. Do not modify every page in UI-5A1 or mix all three slices into one Codex task.

## 10. Explicitly deferred and not authorized

Plan 012 does not authorize:

```text
module-summary Bug implementation beyond the independent prerequisite task
additional module lifecycle buttons on cards
open module directory
module deletion
module update checks or downloads
module installation
automatic updates
host self-update
new module runtime states
new module lifecycle commands
module runtime refactoring
Bridge session or activation changes
Startup test or other startup-health parity commands
Production Web UI
Hybrid Web Windows
floating surfaces
arbitrary third-party language packs
remote translation services
```

These items must be reviewed independently after localization work.

## 11. Related post-plan candidates

The following have been observed but are not Plan 012 slices:

### Module-card lifecycle visibility

Expose all currently available host-confirmed lifecycle actions on module cards without adding new Bridge commands:

```text
NotLoaded / Unloaded → Load, Details
Loaded → Activate, Open, Unload, Details
Running → Open, Deactivate, Unload, Details
Deactivated → Activate, Open, Unload, Details
```

This should be a separate user-visible task after module-summary correctness is restored.

### Targeted module-operation deduplication

After behavior and translations stabilize, consider extracting only proven duplication such as:

```text
module state classification and summaries
operation labels and safe messages
single resynchronization path
host-operation availability
```

Do not pre-authorize a universal Module Card, Command Bus, new Store, or runtime abstraction.

### Module management commands

Discuss separately and in order:

```text
open module directory
remove a user-installed module
module update check and verified package download
```

Destructive removal and path-related commands require dedicated host validation and must not be bundled into localization.

## 12. Completion criteria

Plan 012 is complete only when:

- the user can select `system`, `zh-CN`, or `en-US` in Development Web Settings;
- the host persists and confirms the choice;
- native WPF localization behavior remains intact;
- Vue updates immediately without restarting;
- all current Development Web workspaces have complete Chinese and English primary UI coverage;
- localized module names and descriptions refresh from the host after a language change;
- failures preserve the last confirmed Settings and Module Snapshots;
- tests cover both languages, fallback, command validation, and module refresh behavior;
- no frozen runtime, session, asset, or native-fallback boundary was expanded.

## 13. Stop rule

After UI-5A3 passes targeted tests and manual language checks, stop localization work.

Do not continue adding theoretical locale infrastructure, remote language packs, complex plural engines, arbitrary culture discovery, or translation-management services unless a real product requirement appears.

The next product task must be selected independently from observed user-visible needs.