using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QingToolbox.Abstractions.Modules;

namespace QingToolbox.Modules.Hello;

public sealed class HelloModule : IToolModule
{
    public string Id => "qing.hello";

    public string Name => "Hello Module";

    public string Description => "A minimal test module for QingToolbox.";

    public Task OnLoadAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
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
        return Task.CompletedTask;
    }

    public object? CreateView()
    {
        return new Border
        {
            Padding = new Thickness(28),
            Background = Brushes.White,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "Hello Module",
                        FontSize = 28,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromRgb(23, 32, 51))
                    },
                    new TextBlock
                    {
                        Text = "This view is provided by QingToolbox.Modules.Hello.",
                        Margin = new Thickness(0, 10, 0, 0),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105))
                    }
                }
            }
        };
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
