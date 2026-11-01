using System.Security.Cryptography;
using System.Text;

/// <summary>
/// JSONLの追記だけを読む。末尾の改行が無い行は消費しない。
/// 先頭4096バイトのハッシュが変わった、または短くなったファイルは先頭から読み直す。
/// </summary>
internal static class ByteLog
{
    internal sealed record LogLine(long Number, string Text);

    internal sealed record ReadResult(bool Reset, long NewOffset, long Size, string PrefixHash, long NextLine, IReadOnlyList<LogLine> Lines);

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static ReadResult Read(string path, long previousOffset, long previousSize, string? previousPrefixHash, long nextLine)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var size = stream.Length;
        var prefixLength = (int)Math.Min(4096, size);
        var prefix = new byte[prefixLength];
        if (prefixLength > 0)
        {
            stream.ReadExactly(prefix);
        }

        var prefixHash = Convert.ToHexString(SHA256.HashData(prefix)).ToLowerInvariant();
        var reset = previousPrefixHash is not null && (size < previousSize || !string.Equals(prefixHash, previousPrefixHash, StringComparison.Ordinal) || previousOffset > size);
        var start = reset || previousPrefixHash is null ? 0 : previousOffset;
        var lineNumber = reset || previousPrefixHash is null ? 1 : nextLine;
        if (start > size)
        {
            start = 0;
            lineNumber = 1;
            reset = true;
        }

        stream.Position = start;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var data = buffer.ToArray();
        var lastNewline = Array.LastIndexOf(data, (byte)'\n');
        if (lastNewline < 0)
        {
            return new ReadResult(reset, start, size, prefixHash, lineNumber, []);
        }

        string text;
        try
        {
            text = Utf8.GetString(data, 0, lastNewline + 1);
        }
        catch (DecoderFallbackException ex)
        {
            throw new StatusCheckException($"The log is not valid UTF-8: {path}", ex);
        }

        var lines = new List<LogLine>();
        var current = new StringBuilder();
        var number = lineNumber;
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                var value = current.ToString().TrimEnd('\r');
                current.Clear();
                if (value.Length > 0)
                {
                    lines.Add(new LogLine(number, value));
                }

                number++;
                continue;
            }

            current.Append(ch);
        }

        return new ReadResult(reset, start + lastNewline + 1, size, prefixHash, number, lines);
    }
}
