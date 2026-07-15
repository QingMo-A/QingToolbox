namespace QingToolbox.Shell.Startup;

public sealed record ApplicationLaunchOptions(bool IsStartupLaunch)
{
    public static ApplicationLaunchOptions Parse(IEnumerable<string> args) =>
        new(args.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase)));
}
