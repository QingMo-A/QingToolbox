using System.Text.Json;
using QingToolbox.Abstractions;

namespace QingToolbox.ModuleLoader;

public sealed class ModuleManifestReader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public async Task<ModuleManifest?> ReadAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(manifestPath);
        return await JsonSerializer.DeserializeAsync<ModuleManifest>(stream, Options, cancellationToken);
    }
}
