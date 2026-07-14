# Module Templates

`ModuleTemplate` is the standard starter for a localized WPF in-process module.
Copy the directory to `modules/<YourModule>` and rename the project, namespace,
module class, and view as needed.

Before its first build, update the placeholder values in `module.json`:

- `id` — keep it unique; do not reuse `qing.template`.
- `entry` — change it to the renamed module DLL.
- `name` and `description` — update their English fallback values.

The template always carries both `i18n/en-US.json` and `i18n/zh-CN.json`. Keep
their keys identical and add every new UI key to both files. `module.name` and
`module.description` are used on module cards; `view.*`, `actions.*`,
`status.*`, and `errors.*` are for the module UI.

`TemplateModule` saves `ModuleContext` during `OnLoadAsync`, and `TemplateView`
receives `ModuleContext.Localization` through `CreateView()`. The view implements
`ILocalizedModuleView`; its `RefreshLocalization()` only changes visible text, so
it must not reset user input or rerun business logic. Modules must not read Shell
`settings.json` or reference Shell/Core projects.

Before committing, run:

```powershell
./scripts/verify-modules.ps1 -QingToolboxHostRoot "D:\Path\To\QingToolboxToolboxWorktree"
```
