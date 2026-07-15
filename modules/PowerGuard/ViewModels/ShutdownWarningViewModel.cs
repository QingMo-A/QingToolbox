using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace QingToolbox.Modules.PowerGuard.ViewModels;
public sealed class ShutdownWarningViewModel : INotifyPropertyChanged
{
    private int _seconds;
    public bool IsTestMode { get; init; }
    public int Seconds { get => _seconds; set { _seconds = Math.Max(0, value); PropertyChanged?.Invoke(this, new(nameof(Seconds))); PropertyChanged?.Invoke(this, new(nameof(Countdown))); } }
    public string Countdown => TimeSpan.FromSeconds(Seconds).ToString(@"mm\:ss");
    public event PropertyChangedEventHandler? PropertyChanged;
}
