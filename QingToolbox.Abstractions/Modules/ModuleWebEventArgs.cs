using System.Text.Json;

namespace QingToolbox.Abstractions.Modules;

public sealed class ModuleWebEventArgs(string name, JsonElement? payload) : EventArgs
{
    public string Name { get; } = name;
    public JsonElement? Payload { get; } = payload;
}
