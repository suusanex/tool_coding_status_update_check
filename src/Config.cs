using System.Text.Json.Nodes;

/// <summary>
/// ユーザー領域の設定。APIキーは OPENAI_API_KEY_STATUS_CHECK だけを読む。
/// </summary>
internal sealed class AppConfig
{
    public const string ApiKeyVariable = "OPENAI_API_KEY_STATUS_CHECK";

    public required string StateDirectory { get; init; }
    public required string CodexSessionsDir { get; init; }
    public string? CodexArchivedSessionsDir { get; init; }
    public required string FacadeRunsDir { get; init; }
    public required string LedgerRepository { get; init; }
    public required string Model { get; init; }
    public required string ReasoningEffort { get; init; }
    public required int MaxParallel { get; init; }
    public required int MaxRetries { get; init; }

    public string DatabasePath => Path.Combine(StateDirectory, "state.sqlite");

    public string ReportsDirectory => Path.Combine(StateDirectory, "reports");

    public static string DefaultConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".coding-status-check", "config.json");

    public static string? ReadApiKey()
    {
        var value = Environment.GetEnvironmentVariable(ApiKeyVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    public static AppConfig LoadOrCreate(string? configPath = null)
    {
        var path = string.IsNullOrWhiteSpace(configPath) ? DefaultConfigPath : configPath;
        if (!File.Exists(path))
        {
            var created = CreateDefault();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = new JsonObject
            {
                ["stateDirectory"] = created.StateDirectory,
                ["codexSessionsDir"] = created.CodexSessionsDir,
                ["codexArchivedSessionsDir"] = created.CodexArchivedSessionsDir,
                ["facadeRunsDir"] = created.FacadeRunsDir,
                ["ledgerRepository"] = created.LedgerRepository,
                ["model"] = created.Model,
                ["reasoningEffort"] = created.ReasoningEffort,
                ["maxParallel"] = created.MaxParallel,
                ["maxRetries"] = created.MaxRetries,
            };
            File.WriteAllText(path, json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return created;
        }

        return Parse(File.ReadAllText(path));
    }

    public static AppConfig CreateDefault()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var archived = Path.Combine(profile, ".codex", "archived_sessions");
        return new AppConfig
        {
            StateDirectory = Path.Combine(profile, ".coding-status-check"),
            CodexSessionsDir = Path.Combine(profile, ".codex", "sessions"),
            CodexArchivedSessionsDir = Directory.Exists(archived) ? archived : null,
            FacadeRunsDir = ResolveFacadeDirectory(Environment.GetEnvironmentVariable, profile),
            LedgerRepository = @"D:\Data\git\personal-project-driver",
            Model = "gpt-6.1-terra",
            ReasoningEffort = "medium",
            MaxParallel = 2,
            MaxRetries = 3,
        };
    }

    public static string ResolveFacadeDirectory(Func<string, string?> environment, string userProfile)
    {
        var configured = environment("CODEX_AGENT_FACADE_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Environment.ExpandEnvironmentVariables(configured.Trim());
        }

        return Path.Combine(userProfile, ".codex-agent-facade", "runs");
    }

    public static AppConfig Parse(string json)
    {
        var obj = JsonNode.Parse(json) as JsonObject ?? throw new StatusCheckException("Config JSON is not an object.");
        var state = Required(obj, "stateDirectory");
        var codex = Required(obj, "codexSessionsDir");
        var facade = Required(obj, "facadeRunsDir");
        var ledger = Required(obj, "ledgerRepository");
        var model = obj["model"]?.GetValue<string>();
        var effort = obj["reasoningEffort"]?.GetValue<string>();
        var parallel = obj["maxParallel"]?.GetValue<int>() ?? 2;
        var retries = obj["maxRetries"]?.GetValue<int>() ?? 3;
        if (parallel < 1)
        {
            throw new StatusCheckException("maxParallel must be at least 1.");
        }

        if (retries < 1)
        {
            throw new StatusCheckException("maxRetries must be at least 1.");
        }

        var archived = obj["codexArchivedSessionsDir"]?.GetValue<string>();
        return new AppConfig
        {
            StateDirectory = state,
            CodexSessionsDir = codex,
            CodexArchivedSessionsDir = string.IsNullOrWhiteSpace(archived) ? null : archived,
            FacadeRunsDir = facade,
            LedgerRepository = ledger,
            Model = string.IsNullOrWhiteSpace(model) ? "gpt-6.1-terra" : model,
            ReasoningEffort = string.IsNullOrWhiteSpace(effort) ? "medium" : effort,
            MaxParallel = parallel,
            MaxRetries = retries,
        };
    }

    private static string Required(JsonObject obj, string name)
    {
        var value = obj[name]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new StatusCheckException($"Config is missing {name}.");
        }

        return value;
    }
}
