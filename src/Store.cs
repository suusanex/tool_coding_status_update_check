using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

/// <summary>
/// ローカル状態。raw logそのものは保存せず、正規化済みのobservationと活動だけを置く。
/// </summary>
internal sealed class StateStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public StateStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection("Data Source=" + databasePath);
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
        Execute("""
            CREATE TABLE IF NOT EXISTS files (
              path TEXT PRIMARY KEY,
              source TEXT NOT NULL,
              byte_offset INTEGER NOT NULL,
              size INTEGER NOT NULL,
              prefix_hash TEXT NOT NULL,
              next_line INTEGER NOT NULL,
              native_id TEXT,
              repository TEXT,
              cwd TEXT,
              last_event_utc TEXT,
              terminal INTEGER NOT NULL,
              scan_version INTEGER NOT NULL DEFAULT 0,
              updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS observations (
              id TEXT PRIMARY KEY,
              source TEXT NOT NULL,
              native_id TEXT NOT NULL,
              file_path TEXT NOT NULL,
              ordinal INTEGER NOT NULL,
              ts_utc TEXT,
              kind TEXT NOT NULL,
              body TEXT NOT NULL,
              detail_json TEXT NOT NULL,
              urls_json TEXT NOT NULL,
              run_ids_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_obs_native ON observations(source, native_id);
            CREATE INDEX IF NOT EXISTS ix_obs_ts ON observations(ts_utc);
            CREATE TABLE IF NOT EXISTS activities (
              id TEXT PRIMARY KEY,
              native_id TEXT,
              evidence_key TEXT NOT NULL,
              json TEXT NOT NULL,
              replaced_by TEXT,
              updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS activity_evidence (
              activity_id TEXT NOT NULL,
              observation_id TEXT NOT NULL,
              PRIMARY KEY (activity_id, observation_id)
            );
            CREATE TABLE IF NOT EXISTS tracked_prs (
              url TEXT PRIMARY KEY,
              owner TEXT NOT NULL,
              repo TEXT NOT NULL,
              number INTEGER NOT NULL,
              state TEXT NOT NULL,
              merged_at TEXT,
              closed_at TEXT,
              title TEXT,
              checked_at TEXT NOT NULL,
              activity_id TEXT
            );
            CREATE TABLE IF NOT EXISTS ledger_decisions (
              decision_key TEXT PRIMARY KEY,
              run_id TEXT NOT NULL,
              file_path TEXT NOT NULL,
              workstream TEXT NOT NULL,
              action TEXT NOT NULL,
              activity_id TEXT NOT NULL,
              applied_utc TEXT NOT NULL,
              commit_sha TEXT
            );
            CREATE TABLE IF NOT EXISTS llm_usage (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              run_id TEXT NOT NULL,
              stage TEXT NOT NULL,
              model TEXT NOT NULL,
              input_tokens INTEGER NOT NULL,
              output_tokens INTEGER NOT NULL,
              estimated_usd REAL NOT NULL,
              created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS runs (
              id TEXT PRIMARY KEY,
              window_from TEXT NOT NULL,
              window_to TEXT NOT NULL,
              timezone TEXT NOT NULL,
              started_utc TEXT NOT NULL,
              finished_utc TEXT,
              status TEXT NOT NULL,
              report_path TEXT,
              proposal_path TEXT
            );
            CREATE TABLE IF NOT EXISTS meta (
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS threads (
              source TEXT NOT NULL,
              native_id TEXT NOT NULL,
              file_path TEXT NOT NULL,
              repository TEXT,
              cwd TEXT,
              request_text TEXT,
              request_at TEXT,
              request_ordinal INTEGER,
              request_basis TEXT,
              response_text TEXT,
              response_at TEXT,
              response_ordinal INTEGER,
              response_basis TEXT,
              first_event_utc TEXT,
              last_event_utc TEXT,
              fingerprint TEXT NOT NULL,
              assessment_json TEXT,
              matched_path TEXT,
              matched_workstream TEXT,
              reconcile_fingerprint TEXT,
              input_version INTEGER NOT NULL,
              PRIMARY KEY (source, native_id)
            );
            """);
        EnsureColumn("files", "scan_version", "INTEGER NOT NULL DEFAULT 0");
        SetMeta("input_model_version", Normalize.InputModelVersion.ToString(CultureInfo.InvariantCulture));
    }

    public FileCursor? GetCursor(string path)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT byte_offset, size, prefix_hash, next_line, native_id, repository, cwd, last_event_utc, terminal, scan_version FROM files WHERE path = $path";
            command.Parameters.AddWithValue("$path", path);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new FileCursor
            {
                ByteOffset = reader.GetInt64(0),
                Size = reader.GetInt64(1),
                PrefixHash = reader.GetString(2),
                NextLine = reader.GetInt64(3),
                NativeId = reader.IsDBNull(4) ? null : reader.GetString(4),
                Repository = reader.IsDBNull(5) ? null : reader.GetString(5),
                Cwd = reader.IsDBNull(6) ? null : reader.GetString(6),
                LastEventUtc = reader.IsDBNull(7) ? null : ParseTime(reader.GetString(7)),
                Terminal = reader.GetInt64(8) != 0,
                ScanVersion = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
            };
        }
    }

    public void SaveCursor(string path, string source, FileCursor cursor)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO files(path, source, byte_offset, size, prefix_hash, next_line, native_id, repository, cwd, last_event_utc, terminal, scan_version, updated_utc)
                VALUES ($path, $source, $offset, $size, $hash, $line, $native, $repo, $cwd, $last, $terminal, $version, $updated)
                ON CONFLICT(path) DO UPDATE SET
                  source = excluded.source,
                  byte_offset = excluded.byte_offset,
                  size = excluded.size,
                  prefix_hash = excluded.prefix_hash,
                  next_line = excluded.next_line,
                  native_id = excluded.native_id,
                  repository = excluded.repository,
                  cwd = excluded.cwd,
                  last_event_utc = excluded.last_event_utc,
                  terminal = excluded.terminal,
                  scan_version = excluded.scan_version,
                  updated_utc = excluded.updated_utc
                """;
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$offset", cursor.ByteOffset);
            command.Parameters.AddWithValue("$size", cursor.Size);
            command.Parameters.AddWithValue("$hash", cursor.PrefixHash);
            command.Parameters.AddWithValue("$line", cursor.NextLine);
            command.Parameters.AddWithValue("$native", (object?)cursor.NativeId ?? DBNull.Value);
            command.Parameters.AddWithValue("$repo", (object?)cursor.Repository ?? DBNull.Value);
            command.Parameters.AddWithValue("$cwd", (object?)cursor.Cwd ?? DBNull.Value);
            command.Parameters.AddWithValue("$last", cursor.LastEventUtc is null ? DBNull.Value : FormatTime(cursor.LastEventUtc.Value));
            command.Parameters.AddWithValue("$terminal", cursor.Terminal ? 1 : 0);
            command.Parameters.AddWithValue("$version", cursor.ScanVersion);
            command.Parameters.AddWithValue("$updated", FormatTime(DateTimeOffset.UtcNow));
            command.ExecuteNonQuery();
        }
    }

    public void DeleteObservationsForFile(string path)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM observations WHERE file_path = $path";
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
        }
    }

    public void UpsertObservation(ObservationDraft draft)
    {
        var id = Normalize.ObservationId(draft.FilePath, draft.Ordinal, draft.Kind);
        var urls = new JsonArray();
        foreach (var url in draft.Urls)
        {
            JsonLists.AddObject(urls, new JsonObject
            {
                ["url"] = url.Url,
                ["owner"] = url.Owner,
                ["repository"] = url.Repository,
                ["number"] = url.Number,
                ["kind"] = url.Kind,
            });
        }

        var runIds = new JsonArray();
        foreach (var runId in draft.RunIds)
        {
            JsonLists.AddText(runIds, runId);
        }

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO observations(id, source, native_id, file_path, ordinal, ts_utc, kind, body, detail_json, urls_json, run_ids_json)
                VALUES ($id, $source, $native, $path, $ordinal, $ts, $kind, $body, $detail, $urls, $runs)
                ON CONFLICT(id) DO UPDATE SET
                  source = excluded.source,
                  native_id = excluded.native_id,
                  ts_utc = excluded.ts_utc,
                  body = excluded.body,
                  detail_json = excluded.detail_json,
                  urls_json = excluded.urls_json,
                  run_ids_json = excluded.run_ids_json
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$source", draft.Source);
            command.Parameters.AddWithValue("$native", draft.NativeId);
            command.Parameters.AddWithValue("$path", draft.FilePath);
            command.Parameters.AddWithValue("$ordinal", draft.Ordinal);
            command.Parameters.AddWithValue("$ts", draft.Timestamp is null ? DBNull.Value : FormatTime(draft.Timestamp.Value));
            command.Parameters.AddWithValue("$kind", draft.Kind);
            command.Parameters.AddWithValue("$body", draft.Body);
            command.Parameters.AddWithValue("$detail", draft.DetailJson);
            command.Parameters.AddWithValue("$urls", urls.ToJsonString());
            command.Parameters.AddWithValue("$runs", runIds.ToJsonString());
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<StoredObservation> ListByNative(string source, string nativeId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, source, native_id, file_path, ordinal, ts_utc, kind, body, detail_json, urls_json, run_ids_json FROM observations WHERE source = $source AND native_id = $native ORDER BY ordinal";
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$native", nativeId);
            return ReadObservations(command);
        }
    }

    public IReadOnlyList<(string Source, string NativeId)> ListNativeIdsInWindow(DateTimeOffset fromUtc, DateTimeOffset toUtcExclusive)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT source, native_id FROM observations WHERE ts_utc >= $from AND ts_utc < $to";
            command.Parameters.AddWithValue("$from", FormatTime(fromUtc));
            command.Parameters.AddWithValue("$to", FormatTime(toUtcExclusive));
            var found = new List<(string, string)>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                found.Add((reader.GetString(0), reader.GetString(1)));
            }

            return found;
        }
    }

    public void UpsertPull(PullRequestSnapshot snapshot, string? activityId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO tracked_prs(url, owner, repo, number, state, merged_at, closed_at, title, checked_at, activity_id)
                VALUES ($url, $owner, $repo, $number, $state, $merged, $closed, $title, $checked, $activity)
                ON CONFLICT(url) DO UPDATE SET
                  state = excluded.state,
                  merged_at = excluded.merged_at,
                  closed_at = excluded.closed_at,
                  title = excluded.title,
                  checked_at = excluded.checked_at,
                  activity_id = COALESCE(excluded.activity_id, tracked_prs.activity_id)
                """;
            command.Parameters.AddWithValue("$url", snapshot.Url);
            command.Parameters.AddWithValue("$owner", snapshot.Owner);
            command.Parameters.AddWithValue("$repo", snapshot.Repository);
            command.Parameters.AddWithValue("$number", snapshot.Number);
            command.Parameters.AddWithValue("$state", snapshot.State.ToString());
            command.Parameters.AddWithValue("$merged", snapshot.MergedAt is null ? DBNull.Value : FormatTime(snapshot.MergedAt.Value));
            command.Parameters.AddWithValue("$closed", snapshot.ClosedAt is null ? DBNull.Value : FormatTime(snapshot.ClosedAt.Value));
            command.Parameters.AddWithValue("$title", snapshot.Title);
            command.Parameters.AddWithValue("$checked", FormatTime(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$activity", (object?)activityId ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<(PullRequestSnapshot Snapshot, string? ActivityId)> ListUnmergedTracked()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT url, owner, repo, number, state, merged_at, closed_at, title, activity_id FROM tracked_prs WHERE state != 'Merged'";
            var found = new List<(PullRequestSnapshot, string?)>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var activityId = reader.IsDBNull(8) ? null : reader.GetString(8);
                found.Add((ReadPull(reader), activityId));
            }

            return found;
        }
    }

    public IReadOnlyList<PullRequestSnapshot> ListUnmergedPulls()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT url, owner, repo, number, state, merged_at, closed_at, title, activity_id FROM tracked_prs WHERE state != 'Merged'";
            var found = new List<PullRequestSnapshot>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                found.Add(ReadPull(reader));
            }

            return found;
        }
    }

    public PullRequestSnapshot? GetPull(string url)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT url, owner, repo, number, state, merged_at, closed_at, title, activity_id FROM tracked_prs WHERE url = $url";
            command.Parameters.AddWithValue("$url", url);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadPull(reader) : null;
        }
    }

    public void RecordUsage(string runId, string stage, string model, int inputTokens, int outputTokens, decimal estimatedUsd)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO llm_usage(run_id, stage, model, input_tokens, output_tokens, estimated_usd, created_utc) VALUES ($run, $stage, $model, $input, $output, $usd, $created)";
            command.Parameters.AddWithValue("$run", runId);
            command.Parameters.AddWithValue("$stage", stage);
            command.Parameters.AddWithValue("$model", model);
            command.Parameters.AddWithValue("$input", inputTokens);
            command.Parameters.AddWithValue("$output", outputTokens);
            command.Parameters.AddWithValue("$usd", (double)estimatedUsd);
            command.Parameters.AddWithValue("$created", FormatTime(DateTimeOffset.UtcNow));
            command.ExecuteNonQuery();
        }
    }

    public decimal SumUsage(string runId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(SUM(estimated_usd), 0) FROM llm_usage WHERE run_id = $run";
            command.Parameters.AddWithValue("$run", runId);
            var value = command.ExecuteScalar();
            return value is double number ? (decimal)number : 0m;
        }
    }

    public (int InputTokens, int OutputTokens, decimal Usd) UsageTotals(string runId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(SUM(input_tokens), 0), COALESCE(SUM(output_tokens), 0), COALESCE(SUM(estimated_usd), 0) FROM llm_usage WHERE run_id = $run";
            command.Parameters.AddWithValue("$run", runId);
            using var reader = command.ExecuteReader();
            reader.Read();
            return (Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture), Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture), Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture));
        }
    }

    public void InsertRun(string runId, TimeWindow window)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO runs(id, window_from, window_to, timezone, started_utc, status) VALUES ($id, $from, $to, $zone, $started, 'running')";
            command.Parameters.AddWithValue("$id", runId);
            command.Parameters.AddWithValue("$from", FormatTime(window.FromUtc));
            command.Parameters.AddWithValue("$to", FormatTime(window.ToUtcExclusive));
            command.Parameters.AddWithValue("$zone", window.TimeZoneId);
            command.Parameters.AddWithValue("$started", FormatTime(DateTimeOffset.UtcNow));
            command.ExecuteNonQuery();
        }
    }

    public void FinishRun(string runId, string status, string? reportPath, string? proposalPath)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE runs SET finished_utc = $finished, status = $status, report_path = $report, proposal_path = $proposal WHERE id = $id";
            command.Parameters.AddWithValue("$finished", FormatTime(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$report", (object?)reportPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$proposal", (object?)proposalPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", runId);
            command.ExecuteNonQuery();
        }
    }

    public void SaveDecision(string decisionKey, string runId, string filePath, string workstream, string action, string activityId, string? commitSha)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ledger_decisions(decision_key, run_id, file_path, workstream, action, activity_id, applied_utc, commit_sha)
                VALUES ($key, $run, $path, $workstream, $action, $activity, $applied, $sha)
                ON CONFLICT(decision_key) DO NOTHING
                """;
            command.Parameters.AddWithValue("$key", decisionKey);
            command.Parameters.AddWithValue("$run", runId);
            command.Parameters.AddWithValue("$path", filePath);
            command.Parameters.AddWithValue("$workstream", workstream);
            command.Parameters.AddWithValue("$action", action);
            command.Parameters.AddWithValue("$activity", activityId);
            command.Parameters.AddWithValue("$applied", FormatTime(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$sha", (object?)commitSha ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public bool HasDeleteDecision(string activityId, string filePath)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM ledger_decisions WHERE activity_id = $activity AND file_path = $path AND action = 'delete' LIMIT 1";
            command.Parameters.AddWithValue("$activity", activityId);
            command.Parameters.AddWithValue("$path", filePath);
            return command.ExecuteScalar() is not null;
        }
    }

    public void DeleteOtherThreads(string source, string path, string? keepNativeId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = keepNativeId is null
                ? "DELETE FROM threads WHERE source = $source AND file_path = $path"
                : "DELETE FROM threads WHERE source = $source AND file_path = $path AND native_id <> $keep";
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$path", path);
            if (keepNativeId is not null)
            {
                command.Parameters.AddWithValue("$keep", keepNativeId);
            }

            command.ExecuteNonQuery();
        }
    }

    public void UpsertThread(ThreadRecord thread)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO threads(
                  source, native_id, file_path, repository, cwd,
                  request_text, request_at, request_ordinal, request_basis,
                  response_text, response_at, response_ordinal, response_basis,
                  first_event_utc, last_event_utc, fingerprint, assessment_json,
                  matched_path, matched_workstream, reconcile_fingerprint, input_version)
                VALUES (
                  $source, $native, $path, $repo, $cwd,
                  $request, $requestAt, $requestOrdinal, $requestBasis,
                  $response, $responseAt, $responseOrdinal, $responseBasis,
                  $first, $last, $fingerprint, $assessment,
                  $matchedPath, $matchedWorkstream, $reconcile, $version)
                ON CONFLICT(source, native_id) DO UPDATE SET
                  file_path = excluded.file_path,
                  repository = excluded.repository,
                  cwd = excluded.cwd,
                  request_text = excluded.request_text,
                  request_at = excluded.request_at,
                  request_ordinal = excluded.request_ordinal,
                  request_basis = excluded.request_basis,
                  response_text = excluded.response_text,
                  response_at = excluded.response_at,
                  response_ordinal = excluded.response_ordinal,
                  response_basis = excluded.response_basis,
                  first_event_utc = excluded.first_event_utc,
                  last_event_utc = excluded.last_event_utc,
                  fingerprint = excluded.fingerprint,
                  assessment_json = excluded.assessment_json,
                  matched_path = excluded.matched_path,
                  matched_workstream = excluded.matched_workstream,
                  reconcile_fingerprint = excluded.reconcile_fingerprint,
                  input_version = excluded.input_version
                """;
            BindThread(command, thread);
            command.ExecuteNonQuery();
        }
    }

    public ThreadRecord? GetThread(string source, string nativeId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = ThreadSelect + " WHERE source = $source AND native_id = $native";
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$native", nativeId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadThread(reader) : null;
        }
    }

    public IReadOnlyList<ThreadRecord> ListThreads()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = ThreadSelect + " ORDER BY source, native_id";
            var found = new List<ThreadRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                found.Add(ReadThread(reader));
            }

            return found;
        }
    }

    public void SaveAssessment(string source, string nativeId, ThreadAssessment assessment, string? matchedPath, string? matchedWorkstream, string? reconcileFingerprint)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE threads
                SET assessment_json = $assessment,
                    matched_path = $path,
                    matched_workstream = $workstream,
                    reconcile_fingerprint = $reconcile
                WHERE source = $source AND native_id = $native
                """;
            command.Parameters.AddWithValue("$assessment", assessment.ToJson());
            command.Parameters.AddWithValue("$path", (object?)matchedPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$workstream", (object?)matchedWorkstream ?? DBNull.Value);
            command.Parameters.AddWithValue("$reconcile", (object?)reconcileFingerprint ?? DBNull.Value);
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$native", nativeId);
            command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private void SetMeta(string key, string value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO meta(key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private void EnsureColumn(string table, string column, string definition)
    {
        var exists = false;
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(" + table + ")";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            Execute("ALTER TABLE " + table + " ADD COLUMN " + column + " " + definition);
        }
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<StoredObservation> ReadObservations(SqliteCommand command)
    {
        var found = new List<StoredObservation>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var urls = ReadRefs(reader.GetString(9));
            var runIds = ReadStringArray(reader.GetString(10));
            var draft = new ObservationDraft
            {
                Source = reader.GetString(1),
                NativeId = reader.GetString(2),
                FilePath = reader.GetString(3),
                Ordinal = reader.GetInt64(4),
                Timestamp = reader.IsDBNull(5) ? null : ParseTime(reader.GetString(5)),
                Kind = reader.GetString(6),
                Body = reader.GetString(7),
                DetailJson = reader.GetString(8),
                Urls = urls,
                RunIds = runIds,
            };
            found.Add(new StoredObservation { Id = reader.GetString(0), Draft = draft });
        }

        return found;
    }

    private static PullRequestSnapshot ReadPull(SqliteDataReader reader)
    {
        return new PullRequestSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            Enum.Parse<PullRequestState>(reader.GetString(4)),
            reader.IsDBNull(5) ? null : ParseTime(reader.GetString(5)),
            reader.IsDBNull(6) ? null : ParseTime(reader.GetString(6)),
            reader.IsDBNull(7) ? "" : reader.GetString(7));
    }

    private static IReadOnlyList<GitHubRef> ReadRefs(string json)
    {
        var array = JsonNode.Parse(json) as JsonArray;
        var refs = new List<GitHubRef>();
        if (array is null)
        {
            return refs;
        }

        foreach (var node in array)
        {
            if (node is not JsonObject obj)
            {
                continue;
            }

            refs.Add(new GitHubRef(
                obj["url"]?.GetValue<string>() ?? "",
                obj["owner"]?.GetValue<string>() ?? "",
                obj["repository"]?.GetValue<string>() ?? "",
                obj["number"]?.GetValue<int>() ?? 0,
                obj["kind"]?.GetValue<string>() ?? "pull"));
        }

        return refs;
    }

    private static IReadOnlyList<string> ReadStringArray(string json)
    {
        var array = JsonNode.Parse(json) as JsonArray;
        var values = new List<string>();
        if (array is null)
        {
            return values;
        }

        foreach (var node in array)
        {
            var text = node?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(text))
            {
                values.Add(text);
            }
        }

        return values;
    }

    private const string ThreadSelect = """
        SELECT source, native_id, file_path, repository, cwd,
               request_text, request_at, request_ordinal, request_basis,
               response_text, response_at, response_ordinal, response_basis,
               first_event_utc, last_event_utc, fingerprint, assessment_json,
               matched_path, matched_workstream, reconcile_fingerprint, input_version
        FROM threads
        """;

    private static void BindThread(SqliteCommand command, ThreadRecord thread)
    {
        command.Parameters.AddWithValue("$source", thread.Source);
        command.Parameters.AddWithValue("$native", thread.NativeId);
        command.Parameters.AddWithValue("$path", thread.FilePath);
        command.Parameters.AddWithValue("$repo", (object?)thread.Repository ?? DBNull.Value);
        command.Parameters.AddWithValue("$cwd", (object?)thread.Cwd ?? DBNull.Value);
        command.Parameters.AddWithValue("$request", (object?)thread.Request?.Text ?? DBNull.Value);
        command.Parameters.AddWithValue("$requestAt", thread.Request?.Timestamp is null ? DBNull.Value : FormatTime(thread.Request.Timestamp.Value));
        command.Parameters.AddWithValue("$requestOrdinal", thread.Request is null ? DBNull.Value : thread.Request.Ordinal);
        command.Parameters.AddWithValue("$requestBasis", (object?)thread.Request?.Basis ?? DBNull.Value);
        command.Parameters.AddWithValue("$response", (object?)thread.Response?.Text ?? DBNull.Value);
        command.Parameters.AddWithValue("$responseAt", thread.Response?.Timestamp is null ? DBNull.Value : FormatTime(thread.Response.Timestamp.Value));
        command.Parameters.AddWithValue("$responseOrdinal", thread.Response is null ? DBNull.Value : thread.Response.Ordinal);
        command.Parameters.AddWithValue("$responseBasis", (object?)thread.Response?.Basis ?? DBNull.Value);
        command.Parameters.AddWithValue("$first", thread.FirstEventUtc is null ? DBNull.Value : FormatTime(thread.FirstEventUtc.Value));
        command.Parameters.AddWithValue("$last", thread.LastEventUtc is null ? DBNull.Value : FormatTime(thread.LastEventUtc.Value));
        command.Parameters.AddWithValue("$fingerprint", thread.Fingerprint);
        command.Parameters.AddWithValue("$assessment", thread.Assessment is null ? DBNull.Value : thread.Assessment.ToJson());
        command.Parameters.AddWithValue("$matchedPath", (object?)thread.MatchedPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$matchedWorkstream", (object?)thread.MatchedWorkstream ?? DBNull.Value);
        command.Parameters.AddWithValue("$reconcile", (object?)thread.ReconcileFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", Normalize.InputModelVersion);
    }

    private static ThreadRecord ReadThread(SqliteDataReader reader)
    {
        return new ThreadRecord
        {
            Source = reader.GetString(0),
            NativeId = reader.GetString(1),
            FilePath = reader.GetString(2),
            Repository = reader.IsDBNull(3) ? null : reader.GetString(3),
            Cwd = reader.IsDBNull(4) ? null : reader.GetString(4),
            Request = ReadExcerpt(reader, 5),
            Response = ReadExcerpt(reader, 9),
            FirstEventUtc = reader.IsDBNull(13) ? null : ParseTime(reader.GetString(13)),
            LastEventUtc = reader.IsDBNull(14) ? null : ParseTime(reader.GetString(14)),
            Fingerprint = reader.GetString(15),
            Assessment = reader.IsDBNull(16) ? null : ReadAssessment(reader.GetString(16)),
            MatchedPath = reader.IsDBNull(17) ? null : reader.GetString(17),
            MatchedWorkstream = reader.IsDBNull(18) ? null : reader.GetString(18),
            ReconcileFingerprint = reader.IsDBNull(19) ? null : reader.GetString(19),
        };
    }

    /// <summary>
    /// 保存済み評価が壊れていても、そのスレッドだけ未評価に戻して他の行は読む。
    /// </summary>
    private static ThreadAssessment? ReadAssessment(string json)
    {
        try
        {
            return ThreadAssessment.FromJson(json);
        }
        catch (Exception ex) when (ex is StatusCheckException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static SelectedExcerpt? ReadExcerpt(SqliteDataReader reader, int textOrdinal)
    {
        if (reader.IsDBNull(textOrdinal))
        {
            return null;
        }

        var text = reader.GetString(textOrdinal);
        var at = reader.IsDBNull(textOrdinal + 1) ? (DateTimeOffset?)null : ParseTime(reader.GetString(textOrdinal + 1));
        var ordinal = reader.IsDBNull(textOrdinal + 2) ? 0 : reader.GetInt64(textOrdinal + 2);
        var basis = reader.IsDBNull(textOrdinal + 3) ? "" : reader.GetString(textOrdinal + 3);
        return new SelectedExcerpt(text, at, ordinal, basis);
    }

    internal static string FormatTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

internal sealed class FileCursor
{
    public long ByteOffset { get; set; }
    public long Size { get; set; }
    public string PrefixHash { get; set; } = "";
    public long NextLine { get; set; } = 1;
    public string? NativeId { get; set; }
    public string? Repository { get; set; }
    public string? Cwd { get; set; }
    public DateTimeOffset? LastEventUtc { get; set; }
    public bool Terminal { get; set; }
    public int ScanVersion { get; set; }
}
