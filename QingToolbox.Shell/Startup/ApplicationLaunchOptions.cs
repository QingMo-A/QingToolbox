namespace QingToolbox.Shell.Startup;

public sealed record ApplicationLaunchOptions(
    bool IsStartupLaunch,
    ApplicationExecutionEnvironment Environment,
    bool EnvironmentWasExplicit)
{
    public ApplicationLaunchOptions(bool isStartupLaunch) : this(
        isStartupLaunch, ApplicationExecutionEnvironment.Production(), false) { }

    public static ApplicationLaunchOptions Parse(IEnumerable<string> arguments, bool requireExplicitEnvironment = false)
    {
        var args = arguments.ToArray();
        bool startup = false, environmentSeen = false, profileSeen = false, rootSeen = false;
        string? profile = null, dataRoot = null;
        var kind = ApplicationEnvironmentKind.Production;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.Equals("--startup", StringComparison.OrdinalIgnoreCase))
            {
                if (startup) throw new ArgumentException("Duplicate argument: --startup");
                startup = true;
                continue;
            }
            string ReadValue()
            {
                if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Missing value for {argument}.");
                return args[index];
            }
            if (argument.Equals("--environment", StringComparison.OrdinalIgnoreCase))
            {
                if (environmentSeen) throw new ArgumentException("Duplicate argument: --environment");
                environmentSeen = true;
                if (!Enum.TryParse(ReadValue(), true, out kind))
                    throw new ArgumentException("Environment must be Production, Development or ModuleTest.");
            }
            else if (argument.Equals("--profile", StringComparison.OrdinalIgnoreCase))
            {
                if (profileSeen) throw new ArgumentException("Duplicate argument: --profile");
                profileSeen = true; profile = ReadValue();
            }
            else if (argument.Equals("--data-root", StringComparison.OrdinalIgnoreCase))
            {
                if (rootSeen) throw new ArgumentException("Duplicate argument: --data-root");
                rootSeen = true; dataRoot = ReadValue();
            }
            else throw new ArgumentException($"Unknown argument: {argument}");
        }

        if (requireExplicitEnvironment && !environmentSeen)
            throw new ArgumentException("Debug builds require --environment. Use scripts/start-dev-host.ps1.");
        if (kind == ApplicationEnvironmentKind.Production)
        {
            if (rootSeen) throw new ArgumentException("Production does not accept --data-root.");
            if (profileSeen && !string.Equals(profile, "Default", StringComparison.Ordinal))
                throw new ArgumentException("Production only supports profile Default.");
            return new(startup, ApplicationExecutionEnvironment.Production(), environmentSeen);
        }
        if (startup) throw new ArgumentException("--startup cannot be combined with a non-production environment.");
        if (!profileSeen) throw new ArgumentException("Development and ModuleTest require --profile.");
        if (!rootSeen) throw new ArgumentException("Development and ModuleTest require --data-root.");
        return new(false, ApplicationExecutionEnvironment.Sandbox(kind, profile!, dataRoot!), environmentSeen);
    }
}
