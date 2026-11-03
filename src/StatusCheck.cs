#:property TargetFramework=net10.0
#:package Microsoft.Data.Sqlite@9.0.10
#:package OpenAI@2.14.0
#:include Config.cs
#:include TimeWindow.cs
#:include Models.cs
#:include Normalize.cs
#:include Redaction.cs
#:include ByteLog.cs
#:include Store.cs
#:include CodexCollector.cs
#:include GitHubClient.cs
#:include OpenAiActivity.cs
#:include Ledger.cs
#:include Report.cs
#:include Pipeline.cs

Environment.ExitCode = await Cli.RunAsync(args);

/// <summary>
/// run / preview / apply / estimate の入口。APIキーは OPENAI_API_KEY_STATUS_CHECK だけを読む。
/// </summary>
internal static class Cli
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help")
            {
                Console.Error.WriteLine("Usage: dotnet run --file src/StatusCheck.cs -- run|preview|apply|estimate [options]");
                return args.Length == 0 ? 1 : 0;
            }

            var command = args[0];
            var options = ParseOptions(args[1..]);
            var config = AppConfig.LoadOrCreate(options.ConfigPath);
            if (!string.IsNullOrWhiteSpace(options.InputMode))
            {
                config = new AppConfig
                {
                    StateDirectory = config.StateDirectory,
                    CodexSessionsDir = config.CodexSessionsDir,
                    CodexArchivedSessionsDir = config.CodexArchivedSessionsDir,
                    LedgerRepository = config.LedgerRepository,
                    Model = config.Model,
                    ReasoningEffort = config.ReasoningEffort,
                    MaxParallel = config.MaxParallel,
                    MaxRetries = config.MaxRetries,
                    InputMode = InputModes.NormalizeMode(options.InputMode),
                };
            }

            if (command == "apply")
            {
                if (string.IsNullOrWhiteSpace(options.ProposalRunId))
                {
                    throw new StatusCheckException("apply requires --proposal <runId>.");
                }

                using var applyStore = new StateStore(config.DatabasePath);
                var applied = await StatusPipeline.ApplyAsync(config, applyStore, options.ProposalRunId, new GhPullRequestSource(), CancellationToken.None);
                Print(applied);
                return applied.ExitCode;
            }

            if (command is not ("run" or "preview" or "estimate"))
            {
                throw new StatusCheckException("Unknown command: " + command);
            }

            var estimate = command == "estimate";
            string? key = null;
            if (!estimate)
            {
                key = AppConfig.ReadApiKey();
                if (key is null)
                {
                    throw new StatusCheckException("OPENAI_API_KEY_STATUS_CHECK is not set.");
                }
            }

            var window = BuildWindow(options);
            var runId = Guid.NewGuid().ToString("D");
            using var store = new StateStore(config.DatabasePath);
            var result = await StatusPipeline.RunAsync(new RunRequest
            {
                Config = config,
                Window = window,
                ApplyLedger = command == "run",
                EstimateOnly = estimate,
                Model = estimate ? null : new OpenAiActivityModel(key!, config.Model, config.ReasoningEffort, config.MaxRetries, options.MaxUsd, store, runId),
                Pulls = estimate ? null : new GhPullRequestSource(),
                Store = store,
                Now = DateTimeOffset.UtcNow,
                RunId = runId,
            }, CancellationToken.None);
            Print(result);
            return result.ExitCode;
        }
        catch (StatusCheckException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void Print(RunResult result)
    {
        foreach (var skip in result.SkippedLogs)
        {
            Console.Error.WriteLine(skip.ToLog());
        }

        foreach (var skip in result.SkippedRecords)
        {
            Console.Error.WriteLine(skip.ToLog());
        }

        if (!string.IsNullOrWhiteSpace(result.ReportPath))
        {
            Console.WriteLine("report: " + result.ReportPath);
        }

        if (!string.IsNullOrWhiteSpace(result.TracePath))
        {
            Console.WriteLine("trace: " + result.TracePath);
        }

        if (result.Estimated)
        {
            Console.WriteLine("estimated input tokens: " + result.EstimatedInputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Console.WriteLine("planned calls: " + result.PlannedCalls.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Console.WriteLine("api calls: 0");
        }

        if (result.StopReason is not null)
        {
            Console.WriteLine("stopped: " + result.StopReason);
            return;
        }

        if (!result.Estimated)
        {
            Console.WriteLine(result.AppliedCards.Count == 0 ? "applied: none" : "applied: " + string.Join(", ", result.AppliedCards));
        }
    }

    private static TimeWindow BuildWindow(CliOptions options)
    {
        var zone = TimeZoneInfo.Local;
        if (options.From is not null || options.To is not null)
        {
            if (options.From is null || options.To is null)
            {
                throw new StatusCheckException("--from and --to must be used together.");
            }

            return TimeWindow.FromDates(options.From.Value, options.To.Value, zone);
        }

        if (options.LastDays is null)
        {
            throw new StatusCheckException("Specify --last-days N or --from yyyy-MM-dd --to yyyy-MM-dd.");
        }

        return TimeWindow.LastDays(options.LastDays.Value, zone, DateTimeOffset.UtcNow);
    }

    private static CliOptions ParseOptions(string[] args)
    {
        string? config = null;
        string? proposal = null;
        int? lastDays = null;
        DateOnly? from = null;
        DateOnly? to = null;
        decimal? maxUsd = null;
        string? mode = null;
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            string Value()
            {
                if (index + 1 >= args.Length)
                {
                    throw new StatusCheckException("Missing value for " + option);
                }

                index++;
                return args[index];
            }

            switch (option)
            {
                case "--config":
                    config = Value();
                    break;
                case "--proposal":
                    proposal = Value();
                    break;
                case "--last-days":
                    if (!int.TryParse(Value(), System.Globalization.CultureInfo.InvariantCulture, out var days))
                    {
                        throw new StatusCheckException("--last-days must be an integer.");
                    }

                    lastDays = days;
                    break;
                case "--from":
                    from = ParseDate(Value(), "--from");
                    break;
                case "--to":
                    to = ParseDate(Value(), "--to");
                    break;
                case "--max-usd":
                    if (!decimal.TryParse(Value(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var usd))
                    {
                        throw new StatusCheckException("--max-usd must be a number.");
                    }

                    maxUsd = usd;
                    break;
                case "--mode":
                    mode = Value();
                    break;
                default:
                    throw new StatusCheckException("Unknown option: " + option);
            }
        }

        return new CliOptions(config, proposal, lastDays, from, to, maxUsd, mode);
    }

    private static DateOnly ParseDate(string text, string option)
    {
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
        {
            throw new StatusCheckException(option + " must be yyyy-MM-dd.");
        }

        return date;
    }

    private sealed record CliOptions(string? ConfigPath, string? ProposalRunId, int? LastDays, DateOnly? From, DateOnly? To, decimal? MaxUsd, string? InputMode);
}
