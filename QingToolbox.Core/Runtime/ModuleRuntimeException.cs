namespace QingToolbox.Core.Runtime;

public sealed class ModuleRuntimeException : Exception
{
    public ModuleRuntimeException(string message)
        : base(message)
    {
    }

    public ModuleRuntimeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
