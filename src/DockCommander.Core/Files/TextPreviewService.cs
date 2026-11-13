using System.Text;

namespace DockCommander.Core.Files;

public sealed record TextPreview(string Text, string EncodingName, bool IsTruncated);

public sealed class TextPreviewService
{
    public const int MaxBytes = 256 * 1024;

    public async Task<TextPreview> ReadAsync(IFileSystemProvider provider, string path, CancellationToken cancellationToken = default)
    {
        await using var stream = await provider.OpenReadAsync(path, cancellationToken);
        // Read one extra byte to distinguish an exact-limit file from a truncated preview.
        var buffer = new byte[MaxBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken);
            if (read == 0) break;
            count += read;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => Decode(buffer, count), cancellationToken);
    }

    private static TextPreview Decode(byte[] buffer, int count)
    {
        var truncated = count > MaxBytes;
        var bytes = buffer.AsSpan(0, Math.Min(count, MaxBytes));
        Encoding encoding = new UTF8Encoding(false, true);
        var name = "UTF-8";
        var offset = 0;
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }))
        { encoding = new UTF32Encoding(false, false, true); name = "UTF-32 LE"; offset = 4; }
        else if (bytes.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }))
        { encoding = new UTF32Encoding(true, false, true); name = "UTF-32 BE"; offset = 4; }
        else if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) offset = 3;
        else if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }))
        { encoding = new UnicodeEncoding(false, false, true); name = "UTF-16 LE"; offset = 2; }
        else if (bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
        { encoding = new UnicodeEncoding(true, false, true); name = "UTF-16 BE"; offset = 2; }

        try
        {
            var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
            // With a partial preview, hold an incomplete trailing Unicode character instead of corrupting it.
            encoding.GetDecoder().Convert(bytes[offset..], chars, !truncated, out _, out var length, out _);
            var text = new string(chars, 0, length);
            if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n' or '\f')))
                throw new InvalidDataException("This file appears to contain binary data and cannot be shown as text.");
            return new(text, name, truncated);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Unsupported text encoding or binary file. The viewer supports UTF-8 and BOM-marked UTF-16/UTF-32.", ex);
        }
    }
}
