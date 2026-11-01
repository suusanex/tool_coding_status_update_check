using System.Text.RegularExpressions;

/// <summary>
/// 保存とAPI送信の前に、秘密情報らしい文字列を伏せる。
/// </summary>
internal static partial class Redaction
{
    internal static string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var redacted = text;
        redacted = SecretPattern().Replace(redacted, "[REDACTED]");
        redacted = BearerPattern().Replace(redacted, "Bearer [REDACTED]");
        redacted = AssignmentPattern().Replace(redacted, "$1[REDACTED]");
        return redacted;
    }

    internal static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        return text[..maxChars] + "\n[truncated]";
    }

    [GeneratedRegex(@"\b(?:sk-[A-Za-z0-9_\-]{10,}|ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xai-[A-Za-z0-9]{10,})", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?i)((?:api[_-]?key|token|secret|password)\s*[:=]\s*[""']?)[A-Za-z0-9_\-]{8,}")]
    private static partial Regex AssignmentPattern();
}
