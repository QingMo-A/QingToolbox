using System.Runtime.CompilerServices;

namespace QingToolbox.DevTools.StagingPayloadProbe;

public static class Probe
{
    [ModuleInitializer]
    public static void Initialize()
    {
        var sentinel = Environment.GetEnvironmentVariable("QINGTOOLBOX_STAGING_PROBE_SENTINEL");
        if (!string.IsNullOrWhiteSpace(sentinel)) File.WriteAllText(sentinel, "payload assembly was loaded");
    }
}
