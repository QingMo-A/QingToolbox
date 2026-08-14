namespace QingToolbox.Abstractions.Modules;

/// <summary>Optional backend sink for files explicitly dropped onto a Web module window.</summary>
public interface IWebExternalFileDropSink
{
    Task HandleExternalFilesDroppedAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default);
}
