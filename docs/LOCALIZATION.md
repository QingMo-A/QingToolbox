# Localization

QingToolbox supports Shell localization, module manifest localization, and
module View localization.

The selected language is stored outside the repository:

```text
%APPDATA%\QingToolbox\settings.json
```

Missing or damaged settings safely fall back to `system`.

## Module manifest localization

Modules may declare localization in `module.json`:

```json
{
  "defaultLanguage": "en-US",
  "localization": {
    "basePath": "i18n",
    "resources": {
      "en-US": "i18n/en-US.json",
      "zh-CN": "i18n/zh-CN.json"
    }
  }
}
```

Resource files are flat JSON string dictionaries. Manifest metadata currently
uses these keys:

```json
{
  "module.name": "Text Tools",
  "module.description": "Lightweight text conversion and formatting tools."
}
```

The fallback order is the selected culture, its parent, the module's
`defaultLanguage`, `en-US`, the original manifest value, and finally the key.
Missing files, keys, or localization metadata do not prevent discovery. Older
modules continue to use their original `module.json` values.

Refreshing modules reads `module.json` and i18n JSON files only. It must not
load module DLLs or invoke module code.

## Module View localization

When a module is loaded, the host passes `ILocalizationService` through
`ModuleContext.Localization`. Module code should use this service instead of
reading Shell settings directly or assuming the current language.

Example:

```csharp
private ModuleContext? _context;

public Task OnLoadAsync(
    ModuleContext context,
    CancellationToken cancellationToken)
{
    _context = context;
    return Task.CompletedTask;
}

public object? CreateView()
{
    return new MyModuleView(_context!.Localization, _context.ModuleId);
}
```

Inside the View:

```csharp
title.Text = localization.GetModuleString(
    moduleId,
    "view.title",
    "My Module");
```

Recommended key groups:

- `module.name`
- `module.description`
- `view.title`
- `actions.xxx`
- `status.xxx`
- `errors.xxx`

## Refreshing open module Views

Modules that want the host to refresh an already-open View on language changes
can implement:

```csharp
using QingToolbox.Abstractions.Localization;

public sealed class MyModuleView : UserControl, ILocalizedModuleView
{
    public void RefreshLocalization()
    {
        // Update visible text from ModuleContext.Localization.
    }
}
```

The Shell detects `ILocalizedModuleView` on open module window content and calls
`RefreshLocalization()` after the user changes the language. It does not reload
the module, recreate the View, or close the window.

Modules may also subscribe to `ILocalizationService.CultureChanged` directly.
If they do, they must unsubscribe on `Unloaded`, `Dispose`, or module unload so
the module AssemblyLoadContext can still be collected.

Do not:

- read `%APPDATA%\QingToolbox\settings.json` from a module;
- hard-code UI language assumptions;
- put module UI text in Shell resources;
- load module DLLs during Refresh Modules;
- use module View i18n before the module has been loaded/opened.
