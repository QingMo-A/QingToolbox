# Module UI localization

Modules should localize their own UI through `ModuleContext.Localization`.
Do not read QingToolbox settings files from a module and do not reference Shell
or Core.

Recommended pattern:

```csharp
private ModuleContext? _context;

public Task OnLoadAsync(ModuleContext context, CancellationToken cancellationToken = default)
{
    _context = context;
    return Task.CompletedTask;
}

public object? CreateView()
{
    return new MyView(_context!.Localization, _context.ModuleId);
}
```

Views that need live language refresh should implement
`ILocalizedModuleView`:

```csharp
public partial class MyView : UserControl, ILocalizedModuleView
{
    public void RefreshLocalization()
    {
        TitleText.Text = _localization.GetModuleString(
            _moduleId,
            "view.title",
            "My Module");
    }
}
```

`RefreshLocalization()` should only update visible text. It should not clear
user input, clear output, reload data, enumerate windows, capture screenshots,
or rerun business logic.
