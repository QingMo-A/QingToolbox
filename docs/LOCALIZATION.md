# Localization

QingToolbox localizes the Shell and module manifest metadata. The first phase
supports `system`, `zh-CN`, and `en-US`.

The selected language is stored outside the repository:

```text
%APPDATA%\QingToolbox\settings.json
```

Missing or damaged settings safely fall back to `system`.

## Module resources

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

Resource files are flat JSON string dictionaries:

```json
{
  "module.name": "文本工具",
  "module.description": "轻量文本转换与格式化工具。"
}
```

Use standard culture codes and stable dotted keys. Metadata currently uses
`module.name` and `module.description`.

The fallback order is the selected culture, its parent, the module's
`defaultLanguage`, `en-US`, the original manifest value, and finally the key.
Missing files, keys, or localization metadata do not prevent discovery. Older
modules continue to use their original `module.json` values.

Localization JSON is read with the manifest. Refreshing modules does not load
module DLLs or invoke module code. Module-view localization is planned for a
later phase.

The first phase covers Shell navigation, page headings, module management
actions, status messages, module detail labels, and module manifest name and
description metadata. It does not localize controls inside a module View.

Missing, invalid, or unsafe module localization resources are reported in the
module card's collapsed **Details** issue list. These diagnostics do not stop
the module from being discovered, and modules without localization metadata
remain fully compatible.
