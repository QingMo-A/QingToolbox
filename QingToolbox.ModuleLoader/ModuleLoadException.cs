namespace QingToolbox.ModuleLoader;

public sealed class ModuleLoadException : Exception
{
    public ModuleLoadException(string message)
        : base(message)
    {
    }

    public ModuleLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
