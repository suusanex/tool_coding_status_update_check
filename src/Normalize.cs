using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// URL、runId、repository、本文の切り詰めを機械的に扱う。
/// </summary>
internal static partial class Normalize
{
    internal const int ExcerptLimit = 4000;
    internal const int StoredBodyLimit = 200_000;
    internal const int InputModelVersion = 2;

    internal static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static string ShortHash(string text) => Sha256Hex(text)[..20];

    internal static string ThreadKey(string source, string nativeId) => source + "\n" + nativeId;

    internal static string ObservationId(string path, long ordinal, string kind) =>
        "obs_" + ShortHash(path + "\n" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + kind);

    internal static string SelectionFingerprint(SelectedExcerpt? request, SelectedExcerpt? response)
    {
        var joined = InputModelVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\n" + (request?.Text ?? "")
            + "\n" + (response?.Text ?? "");
        return Sha256Hex(joined);
    }

    /// <summary>
    /// 表示用の4000文字とは別に、redact済み本文をローカル状態へ残す。上限を超えた分は落とさず、省略フラグで完了削除を止める。
    /// </summary>
    internal static string StoreBody(string text, out bool omittedTail)
    {
        var redacted = Redaction.Apply(text).Trim();
        if (redacted.Length <= StoredBodyLimit)
        {
            omittedTail = false;
            return redacted;
        }

        omittedTail = true;
        return redacted[..StoredBodyLimit];
    }

    internal static IReadOnlyList<GitHubRef> GitHubRefs(string text)
    {
        var found = new List<GitHubRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in GitHubUrlPattern().Matches(text))
        {
            var kind = match.Groups["kind"].Value.Equals("pull", StringComparison.OrdinalIgnoreCase) ? "pull" : "issue";
            var url = "https://github.com/" + match.Groups["owner"].Value + "/" + match.Groups["repo"].Value + "/" + (kind == "pull" ? "pull" : "issues") + "/" + match.Groups["num"].Value;
            if (!seen.Add(url))
            {
                continue;
            }

            found.Add(new GitHubRef(url, match.Groups["owner"].Value, match.Groups["repo"].Value, int.Parse(match.Groups["num"].Value, System.Globalization.CultureInfo.InvariantCulture), kind));
        }

        return found;
    }

    /// <summary>
    /// 選択済み本文の PR URL と、repository が分かっているときの番号表記を候補にする。
    /// </summary>
    internal static IReadOnlyList<GitHubRef> PullCandidates(string text, string? repository)
    {
        var found = GitHubRefs(text).Where(item => item.Kind == "pull").ToList();
        var seen = found.Select(item => item.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in RepoPullPattern().Matches(text))
        {
            AddPull(found, seen, match.Groups["owner"].Value, match.Groups["repo"].Value, match.Groups["num"].Value);
        }

        if (!string.IsNullOrWhiteSpace(repository))
        {
            var parts = repository.Split('/', 2);
            if (parts.Length == 2)
            {
                foreach (Match match in HashPullPattern().Matches(text))
                {
                    AddPull(found, seen, parts[0], parts[1], match.Groups["num"].Value);
                }
            }
        }

        return found;
    }

    private static void AddPull(List<GitHubRef> found, HashSet<string> seen, string owner, string repo, string numberText)
    {
        if (!int.TryParse(numberText, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return;
        }

        var url = "https://github.com/" + owner + "/" + repo + "/pull/" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!seen.Add(url))
        {
            return;
        }

        found.Add(new GitHubRef(url, owner, repo, number, "pull"));
    }

    internal static IReadOnlyList<string> RunIds(string text)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in RunIdPattern().Matches(text))
        {
            var id = match.Groups["id"].Value;
            if (seen.Add(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    internal static string? RepositoryFromGitUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var match = GitRemotePattern().Match(url.Trim());
        if (!match.Success)
        {
            return null;
        }

        var repo = match.Groups["repo"].Value;
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repo = repo[..^4];
        }

        return match.Groups["owner"].Value + "/" + repo;
    }

    internal static string Excerpt(string text)
    {
        var redacted = Redaction.Apply(text);
        return Redaction.Truncate(redacted.Trim(), ExcerptLimit);
    }

    internal static IReadOnlyList<string> ResidualHints(string text)
    {
        var hints = new List<string>();
        foreach (var sentence in SplitSentences(text))
        {
            if (sentence.Contains("残作業", StringComparison.Ordinal) ||
                sentence.Contains("残件", StringComparison.Ordinal) ||
                sentence.Contains("マージ後", StringComparison.Ordinal) ||
                sentence.Contains("未対応", StringComparison.Ordinal) ||
                sentence.Contains("remaining work", StringComparison.OrdinalIgnoreCase) ||
                sentence.Contains("follow-up", StringComparison.OrdinalIgnoreCase))
            {
                hints.Add(Redaction.Truncate(Redaction.Apply(sentence), 500));
            }

            if (hints.Count == 10)
            {
                break;
            }
        }

        return hints;
    }

    internal static List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            current.Append(ch);
            if (ch is '。' or '\n' or '.' )
            {
                var sentence = current.ToString().Trim();
                current.Clear();
                if (sentence.Length > 0)
                {
                    sentences.Add(sentence);
                }
            }
        }

        var tail = current.ToString().Trim();
        if (tail.Length > 0)
        {
            sentences.Add(tail);
        }

        return sentences;
    }

    internal static string? ConfirmQuote(string? quote, IEnumerable<string> sources)
    {
        if (string.IsNullOrWhiteSpace(quote))
        {
            return null;
        }

        var trimmed = quote.Trim();
        foreach (var source in sources)
        {
            if (source.Contains(trimmed, StringComparison.Ordinal))
            {
                return trimmed;
            }
        }

        return null;
    }

    /// <summary>
    /// このツールの分析実行だけを自己実行とする。作業ディレクトリだけでは判定しない。
    /// </summary>
    internal static bool IsSelfInvocation(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        return body.Contains("StatusCheck.cs", StringComparison.Ordinal) &&
            (body.Contains("dotnet run", StringComparison.OrdinalIgnoreCase) || body.Contains("--file", StringComparison.Ordinal));
    }

    internal static bool IsSubstantiveKind(string kind) =>
        kind is not ("session_meta" or "task_started" or "session_bound");

    internal static string ReadText(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Array => ReadArrayText(element),
            _ => "",
        };
    }

    private static string ReadArrayText(JsonElement array)
    {
        var builder = new StringBuilder();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                builder.AppendLine(item.GetString());
                continue;
            }

            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                builder.AppendLine(text.GetString());
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"https://github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)/(?<kind>pull|issues)/(?<num>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitHubUrlPattern();

    [GeneratedRegex(@"(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)#(?<num>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex RepoPullPattern();

    [GeneratedRegex(@"(?:^|\s)#(?<num>\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex HashPullPattern();

    [GeneratedRegex(@"\b(?<id>\d{8}T\d{6}Z-[0-9a-fA-F]{8})\b", RegexOptions.CultureInvariant)]
    private static partial Regex RunIdPattern();

    [GeneratedRegex(@"(?:https://github\.com/|git@github\.com:|ssh://git@github\.com/)(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitRemotePattern();
}
