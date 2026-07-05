using System.Text.Json;
using System.Text.Json.Serialization;
using QingToolbox.Abstractions.Modules;

namespace QingToolbox.ModuleLoader;

public sealed class ModuleManifestReader
{
    private static readonly JsonSerializerOptions Options = CreateSerializerOptions();

    public async Task<ModuleManifest?> ReadAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(manifestPath);
        return await JsonSerializer.DeserializeAsync<ModuleManifest>(stream, Options, cancellationToken);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
