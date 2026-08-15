using System.Text.Json.Serialization;

namespace QingToolbox.Modules.Launcher;

public sealed record LauncherHotkeySpec(
    bool Ctrl = true,
    bool Alt = true,
    bool Shift = false,
    bool Win = false,
    int VirtualKey = 0x20,
    string KeyLabel = "Space")
{
    public static LauncherHotkeySpec Default { get; } = new();

    public bool IsValid => VirtualKey is > 0 and <= 0xFF && (Ctrl || Alt || Shift || Win);

    public LauncherHotkeySpec Normalize() =>
        new(Ctrl, Alt, Shift, Win, Math.Clamp(VirtualKey, 1, 0xFF),
            string.IsNullOrWhiteSpace(KeyLabel) ? "Key" : KeyLabel.Trim()[..Math.Min(32, KeyLabel.Trim().Length)]);
}

public sealed record LauncherItem(
    string Id,
    string Name,
    string Target,
    string Arguments,
    string WorkingDirectory,
    string? IconKey,
    DateTimeOffset? LastLaunchedAt,
    string? IconSourcePath = null);

public sealed record ResolvedLauncherItem(
    string Name,
    string Target,
    string Arguments,
    string WorkingDirectory,
    string? IconSourcePath);

public sealed record LauncherItemView(
    string Id,
    string Name,
    string? IconKey,
    DateTimeOffset? LastLaunchedAt);

public sealed record LauncherHotkeyView(
    bool Ctrl,
    bool Alt,
    bool Shift,
    bool Win,
    int VirtualKey,
    string KeyLabel);

public sealed record LauncherStateView(
    string SortMode,
    IReadOnlyList<LauncherItemView> Items,
    IReadOnlyList<LauncherItemView> Recent,
    LauncherHotkeyView Hotkey,
    string HotkeyStatus,
    bool Active);

internal sealed class LauncherStoreDocument
{
    public string? SortMode { get; set; }
    public LauncherHotkeySpec? Hotkey { get; set; }
    public List<LauncherItem>? Items { get; set; }
}
