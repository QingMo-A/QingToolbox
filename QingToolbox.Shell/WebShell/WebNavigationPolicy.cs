namespace QingToolbox.Shell.WebShell;

public sealed class WebNavigationPolicy
{
    public const string VirtualHost = "app.qingtoolbox.local";
    public static readonly Uri StartUri = new($"https://{VirtualHost}/index.html");
    public bool IsAllowed(Uri? uri) => uri is { Scheme: "https" } &&
        string.Equals(uri.Host, VirtualHost, StringComparison.OrdinalIgnoreCase) && uri.Port == 443;
    public bool AllowNewWindow(Uri? _) => false;
    public bool AllowDownload(Uri? _) => false;
    public bool AllowPermission(string? _) => false;
}
