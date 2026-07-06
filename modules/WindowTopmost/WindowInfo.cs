namespace QingToolbox.Modules.WindowTopmost;

public sealed record WindowInfo(
    nint Handle,
    string Title,
    string ProcessName,
    bool IsTopmost)
{
    public string HandleText => $"0x{Handle.ToInt64():X}";
    public string TopmostText => IsTopmost ? "Yes" : "No";
}
