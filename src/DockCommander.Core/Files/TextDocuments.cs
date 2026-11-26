using System.Text;

namespace DockCommander.Core.Files;

public sealed record TextEncodingChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record TextDocument(
    string Text,
    string EncodingId,
    string EncodingName,
    string NewLine,
    bool LowConfidence,
    bool IsTruncated,
    byte[] Bytes);

public static class TextDocuments
{
    public const int MaxViewBytes = 256 * 1024;
    public const int MaxEditBytes = 1024 * 1024;

    public static readonly IReadOnlyList<TextEncodingChoice> Encodings =
    [
        new("utf-8", "UTF-8"),
        new("utf-8-bom", "UTF-8 BOM"),
        new("utf-16-le", "UTF-16 LE"),
        new("utf-16-be", "UTF-16 BE"),
        new("utf-32-le", "UTF-32 LE"),
        new("utf-32-be", "UTF-32 BE"),
        new("windows-1250", "Windows-1250"),
        new("windows-1251", "Windows-1251"),
        new("iso-8859-1", "ISO-8859-1")
    ];

    static TextDocuments() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static async Task<TextDocument> ReadAsync(IFileSystemProvider provider, string path, int maxBytes, bool allowTruncate,
        CancellationToken cancellationToken = default)
    {
        await using var stream = await provider.OpenReadAsync(path, cancellationToken);
        var buffer = new byte[maxBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken);
            if (read == 0) break;
            count += read;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (count > maxBytes && !allowTruncate)
            throw new InvalidDataException($"This file is too large to edit. The limit is {maxBytes / 1024} KiB, and nothing was changed.");
        var truncated = count > maxBytes;
        return Decode(buffer.AsSpan(0, Math.Min(count, maxBytes)), truncated, encodingId: null);
    }

    public static TextDocument Decode(ReadOnlySpan<byte> bytes, bool truncated = false, string? encodingId = null)
    {
        var copy = bytes.ToArray();
        if (encodingId is not null)
        {
            var forced = Choice(encodingId);
            var text = DecodeWith(forced, copy, truncated, out _);
            RejectBinary(text);
            return new(text, forced.Id, forced.Name, DetectNewLine(text), false, truncated, copy);
        }
        if (TryBom(copy, out var bom, out var offset))
        {
            var text = DecodeWith(bom, copy, truncated, out _, offset);
            RejectBinary(text);
            return new(text, bom.Id, bom.Name, DetectNewLine(text), false, truncated, copy);
        }
        if (TryUtf8(copy, truncated, out var utf8))
        {
            RejectBinary(utf8);
            return new(utf8, "utf-8", "UTF-8", DetectNewLine(utf8), false, truncated, copy);
        }
        if (copy.Contains((byte)0) || EndsWithIncompleteUtf8(copy))
            throw new InvalidDataException(copy.Contains((byte)0)
                ? "This file appears to contain binary data and cannot be shown as text."
                : "Unsupported text encoding or binary file. The viewer supports UTF-8 and BOM-marked UTF-16/UTF-32.");
        var fallback = Choice("windows-1251");
        var guessed = DecodeWith(fallback, copy, truncated, out _);
        RejectBinary(guessed);
        return new(guessed, fallback.Id, fallback.Name, DetectNewLine(guessed), true, truncated, copy);
    }

    public static byte[] Encode(string text, string encodingId, string newLine)
    {
        var choice = Choice(encodingId);
        var encoding = Create(choice.Id);
        var normalized = ApplyNewLine(text, newLine);
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(normalized);
        if (preamble.Length == 0) return body;
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    public static string NewLineLabel(string newLine) => newLine == "\r\n" ? "CRLF" : "LF";

    public static string ApplyNewLine(string text, string newLine)
    {
        var unix = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return newLine == "\r\n" ? unix.Replace("\n", "\r\n", StringComparison.Ordinal) : unix;
    }

    public static TextEncodingChoice Choice(string encodingId) =>
        Encodings.FirstOrDefault(choice => choice.Id == encodingId)
        ?? throw new InvalidDataException("Choose a supported text encoding.");

    private static bool TryBom(byte[] bytes, out TextEncodingChoice choice, out int offset)
    {
        choice = Choice("utf-8");
        offset = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) { choice = Choice("utf-32-le"); offset = 4; return true; }
        if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) { choice = Choice("utf-32-be"); offset = 4; return true; }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { choice = Choice("utf-8-bom"); offset = 3; return true; }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { choice = Choice("utf-16-le"); offset = 2; return true; }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { choice = Choice("utf-16-be"); offset = 2; return true; }
        return false;
    }

    private static bool TryUtf8(byte[] bytes, bool truncated, out string text)
    {
        text = "";
        try
        {
            text = DecodeWith(Choice("utf-8"), bytes, truncated, out _);
            return true;
        }
        catch (DecoderFallbackException) { return false; }
        catch (InvalidDataException) { return false; }
    }

    private static string DecodeWith(TextEncodingChoice choice, byte[] bytes, bool truncated, out int consumed, int offset = 0)
    {
        var encoding = Create(choice.Id);
        var preamble = encoding.GetPreamble();
        if (offset == 0 && preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble)) offset = preamble.Length;
        var chars = new char[encoding.GetMaxCharCount(Math.Max(0, bytes.Length - offset))];
        encoding.GetDecoder().Convert(bytes.AsSpan(offset), chars, !truncated, out consumed, out var length, out _);
        return new string(chars, 0, length);
    }

    private static bool EndsWithIncompleteUtf8(byte[] bytes)
    {
        var index = 0;
        while (index < bytes.Length)
        {
            var lead = bytes[index];
            var need = lead < 0x80 ? 1
                : lead is >= 0xC2 and <= 0xDF ? 2
                : lead is >= 0xE0 and <= 0xEF ? 3
                : lead is >= 0xF0 and <= 0xF4 ? 4 : 0;
            if (need == 0) return false;
            var available = bytes.Length - index;
            var present = Math.Min(need, available);
            for (var extra = 1; extra < present; extra++)
                if ((bytes[index + extra] & 0xC0) != 0x80) return false;
            if (available < need) return true;
            index += need;
        }
        return false;
    }

    private static Encoding Create(string encodingId) => encodingId switch
    {
        "utf-8" => new UTF8Encoding(false, true),
        "utf-8-bom" => new UTF8Encoding(true, true),
        "utf-16-le" => new UnicodeEncoding(false, true, true),
        "utf-16-be" => new UnicodeEncoding(true, true, true),
        "utf-32-le" => new UTF32Encoding(false, true, true),
        "utf-32-be" => new UTF32Encoding(true, true, true),
        "windows-1250" => Encoding.GetEncoding(1250, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        "windows-1251" => Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        "iso-8859-1" => Encoding.GetEncoding("iso-8859-1", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        _ => throw new InvalidDataException("Choose a supported text encoding.")
    };

    private static void RejectBinary(string text)
    {
        if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n' or '\f')))
            throw new InvalidDataException("This file appears to contain binary data and cannot be shown as text.");
    }

    public static string DetectNewLine(string text)
    {
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf++;
            else lf++;
        }
        if (crlf == 0 && lf == 0) return "\n";
        return crlf >= lf ? "\r\n" : "\n";
    }
}
