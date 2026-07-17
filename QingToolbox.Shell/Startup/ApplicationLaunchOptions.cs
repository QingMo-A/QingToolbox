namespace QingToolbox.Shell.Startup;

public sealed record ApplicationLaunchOptions(
    bool IsStartupLaunch,
    ApplicationExecutionEnvironment Environment,
    bool EnvironmentWasExplicit,
    StartupLaunchSource StartupSource = StartupLaunchSource.Manual)
{
    public ApplicationLaunchOptions(bool isStartupLaunch) : this(
        isStartupLaunch, ApplicationExecutionEnvironment.Production(), false) { }

    public static ApplicationLaunchOptions Parse(IEnumerable<string> arguments, bool requireExplicitEnvironment = false)
    {
        var args = arguments.ToArray();
        bool startup = false, environmentSeen = false, profileSeen = false, repositoryRootSeen = false, sourceSeen = false;
        var startupSource = StartupLaunchSource.Manual;
        string? profile = null, repositoryRoot = null;
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
            if (argument.Equals("--startup-source", StringComparison.OrdinalIgnoreCase))
            {
                if (sourceSeen) throw new ArgumentException("Duplicate argument: --startup-source");
                sourceSeen = true;
                if (!Enum.TryParse<StartupLaunchSource>(ReadValue(), true, out startupSource) || startupSource == StartupLaunchSource.Manual)
                    throw new ArgumentException("Startup source must be TaskScheduler, RegistryRun or StartupTest.");
            }
            else if (argument.Equals("--environment", StringComparison.OrdinalIgnoreCase))
            {
                if (environmentSeen) throw new ArgumentException("Duplicate argument: --environment");
                environmentSeen = true;
                var environmentName = ReadValue();
                if (environmentName.Equals("Production", StringComparison.OrdinalIgnoreCase))
                    kind = ApplicationEnvironmentKind.Production;
                else if (environmentName.Equals("Development", StringComparison.OrdinalIgnoreCase))
                    kind = ApplicationEnvironmentKind.Development;
                else if (environmentName.Equals("ModuleTest", StringComparison.OrdinalIgnoreCase))
                    kind = ApplicationEnvironmentKind.ModuleTest;
                else
                    throw new ArgumentException("Environment must be Production, Development or ModuleTest.");
            }
            else if (argument.Equals("--profile", StringComparison.OrdinalIgnoreCase))
            {
                if (profileSeen) throw new ArgumentException("Duplicate argument: --profile");
                profileSeen = true; profile = ReadValue();
            }
            else if (argument.Equals("--repo-root", StringComparison.OrdinalIgnoreCase))
            {
                if (repositoryRootSeen) throw new ArgumentException("Duplicate argument: --repo-root");
                repositoryRootSeen = true; repositoryRoot = ReadValue();
            }
            else throw new ArgumentException($"Unknown argument: {argument}");
        }

        if (requireExplicitEnvironment && !environmentSeen)
            throw new ArgumentException("Debug builds require --environment. Use scripts/start-dev-host.ps1.");
        if (sourceSeen && !startup) throw new ArgumentException("--startup-source requires --startup.");
        if (startup && !sourceSeen) startupSource = StartupLaunchSource.RegistryRun;
        if (kind == ApplicationEnvironmentKind.Production)
        {
            if (repositoryRootSeen) throw new ArgumentException("Production does not accept --repo-root.");
            if (profileSeen && !string.Equals(profile, "Default", StringComparison.Ordinal))
                throw new ArgumentException("Production only supports profile Default.");
            return new(startup, ApplicationExecutionEnvironment.Production(), environmentSeen, startup ? startupSource : StartupLaunchSource.Manual);
        }
        if (startup) throw new ArgumentException("--startup cannot be combined with a non-production environment.");
        if (!profileSeen) throw new ArgumentException("Development and ModuleTest require --profile.");
        if (!repositoryRootSeen) throw new ArgumentException("Development and ModuleTest require --repo-root.");
        return new(false, ApplicationExecutionEnvironment.Sandbox(kind, profile!, repositoryRoot!), environmentSeen);
    }
}

public enum StartupLaunchSource { Manual, TaskScheduler, RegistryRun, StartupTest }
