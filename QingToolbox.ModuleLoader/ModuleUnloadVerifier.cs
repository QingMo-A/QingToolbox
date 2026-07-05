namespace QingToolbox.ModuleLoader;

public static class ModuleUnloadVerifier
{
    public static bool WaitForUnload(
        WeakReference weakReference,
        int maxCollectCount = 10)
    {
        ArgumentNullException.ThrowIfNull(weakReference);
        ArgumentOutOfRangeException.ThrowIfNegative(maxCollectCount);

        for (var i = 0; weakReference.IsAlive && i < maxCollectCount; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        return !weakReference.IsAlive;
    }
}
