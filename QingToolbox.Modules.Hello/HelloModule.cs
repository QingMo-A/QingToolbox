using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Abstractions.Modules;

namespace QingToolbox.Modules.Hello;

public sealed class HelloModule : IToolModule
{
    private ModuleContext? _context;

    public string Id => "qing.hello";

    public string Name => "Hello Module";

    public string Description => "A minimal test module for QingToolbox.";

    public Task OnLoadAsync(
        ModuleContext context,
        CancellationToken cancellationToken = default)
    {
        _context = context;
        return Task.CompletedTask;
    }

    public Task OnActivateAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task OnDeactivateAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task OnUnloadAsync(CancellationToken cancellationToken = default)
    {
        _context = null;
        return Task.CompletedTask;
    }

    public object? CreateView()
    {
        if (_context is null)
        {
            throw new InvalidOperationException("Module context is not available.");
        }

        var title = new TextBlock
        {
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(23, 32, 51))
        };
        var message = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105))
        };
        var localizer = new HelloViewLocalizer(
            _context.Localization,
            _context.ModuleId,
            title,
            message);
        var view = new Border
        {
            Padding = new Thickness(28),
            Background = Brushes.White,
            Tag = localizer,
            Child = new StackPanel
            {
                Children =
                {
                    title,
                    message
                }
            }
        };
        localizer.RefreshLocalization();
        return view;
    }

    public ValueTask DisposeAsync()
    {
        _context = null;
        return ValueTask.CompletedTask;
    }
}

internal sealed class HelloViewLocalizer(
    ILocalizationService localization,
    string moduleId,
    TextBlock title,
    TextBlock message) : ILocalizedModuleView
{
    public void RefreshLocalization()
    {
        title.Text = localization.GetModuleString(
            moduleId,
            "view.title",
            "Hello Module");
        message.Text = localization.GetModuleString(
            moduleId,
            "view.message",
            "This view is localized through ModuleContext.Localization.");
    }
}
