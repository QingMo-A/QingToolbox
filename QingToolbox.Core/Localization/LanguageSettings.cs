namespace QingToolbox.Core.Localization;

public sealed class LanguageSettings
{
    public string Language { get; set; } = "system";
    public double? FloatingBadgeLeft { get; set; }
    public double? FloatingBadgeTop { get; set; }
    public bool HasFloatingBadgePosition { get; set; }
}
