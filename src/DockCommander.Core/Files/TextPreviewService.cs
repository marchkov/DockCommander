namespace DockCommander.Core.Files;

public sealed record TextPreview(string Text, string EncodingName, bool IsTruncated);

public sealed class TextPreviewService
{
    public const int MaxBytes = TextDocuments.MaxViewBytes;

    public async Task<TextPreview> ReadAsync(IFileSystemProvider provider, string path, CancellationToken cancellationToken = default)
    {
        var document = await TextDocuments.ReadAsync(provider, path, MaxBytes, allowTruncate: true, cancellationToken);
        return new(document.Text, document.EncodingName, document.IsTruncated);
    }
}
