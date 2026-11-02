using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// 導入済みの gh で PR の現在状態を取る。Open は未マージ、mergedAt があるものはマージ済み、
/// Closed かつ mergedAt が無いものは未マージの Close。
/// </summary>
internal sealed class GhPullRequestSource : IPullRequestSource
{
    public async Task<PullRequestSnapshot> GetAsync(string owner, string repository, int number, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "gh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        process.StartInfo.ArgumentList.Add("pr");
        process.StartInfo.ArgumentList.Add("view");
        process.StartInfo.ArgumentList.Add(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("--repo");
        process.StartInfo.ArgumentList.Add(owner + "/" + repository);
        process.StartInfo.ArgumentList.Add("--json");
        process.StartInfo.ArgumentList.Add("state,mergedAt,closedAt,title,url");
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new StatusCheckException($"gh pr view {owner}/{repository}#{number} failed: {stderr.Trim()}");
        }

        try
        {
            var obj = JsonNode.Parse(stdout) as JsonObject ?? throw new StatusCheckException("gh returned unexpected JSON for " + owner + "/" + repository + "#" + number);
            var mergedAt = ParseTime(obj["mergedAt"]);
            var closedAt = ParseTime(obj["closedAt"]);
            var stateText = obj["state"]?.GetValue<string>() ?? "";
            var state = mergedAt is not null || stateText.Equals("MERGED", StringComparison.OrdinalIgnoreCase)
                ? PullRequestState.Merged
                : stateText.Equals("OPEN", StringComparison.OrdinalIgnoreCase)
                    ? PullRequestState.Open
                    : PullRequestState.ClosedUnmerged;
            var url = obj["url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(url))
            {
                url = "https://github.com/" + owner + "/" + repository + "/pull/" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return new PullRequestSnapshot(url, owner, repository, number, state, mergedAt, closedAt, obj["title"]?.GetValue<string>() ?? "");
        }
        catch (StatusCheckException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new StatusCheckException("gh returned unexpected JSON for " + owner + "/" + repository + "#" + number, ex);
        }
    }

    private static DateTimeOffset? ParseTime(JsonNode? node)
    {
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
        {
            return null;
        }

        var text = node.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
    }
}
