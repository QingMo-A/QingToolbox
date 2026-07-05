using QingToolbox.Abstractions;

namespace QingToolbox.Modules.Hello;

public sealed class HelloModule : IModule
{
    public string Id => "qingtoolbox.modules.hello";
    public string DisplayName => "Hello";
}
