using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace CodexTpsFloat;

// Optional local diagnostic clocks prevent failed-attempt items from timing a later response.
// SQLite is opened in read-only mode; no prompt or response body is retained.
public sealed class RequestClockReader
{
    readonly string home;
    readonly Dictionary<string, List<double>> clocks = new();
    long lastId;
    string? lastDatabase;
    static readonly Regex TurnId = new("turn\\.id=\"?([\\w-]+)", RegexOptions.Compiled);
    public string? Warning { get; private set; }
    public RequestClockReader(string home) => this.home = home;
    public IReadOnlyDictionary<string, double[]> Refresh(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Warning = null;
        try
        {
            string? database = Directory.Exists(home) ? Directory.EnumerateFiles(home, "logs_*.sqlite")
                .OrderByDescending(p => int.TryParse(Path.GetFileNameWithoutExtension(p)[5..], out int n) ? n : 0).FirstOrDefault() : null;
            if (database == null) { Warning = "未找到请求诊断库，仅使用输出项计时"; return Freeze(); }
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
            connection.Open();
            cancellationToken.ThrowIfCancellationRequested();
            // SqliteCommand.Cancel is a no-op. Check cancellation inside SQLite's
            // VM too, including queries which have not produced their first row.
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1000,
                static state => ((CancellationToken)state).IsCancellationRequested ? 1 : 0, cancellationToken);
            using (var maximum = connection.CreateCommand())
            {
                maximum.CommandText = "SELECT COALESCE(MAX(id),0) FROM logs";
                long id = Convert.ToInt64(maximum.ExecuteScalar());
                if (lastDatabase != database || id < lastId) { clocks.Clear(); lastId = 0; }
                lastDatabase = database;
            }
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,ts,ts_nanos,thread_id,feedback_log_body FROM logs WHERE id>$last AND target='feedback_tags' AND feedback_log_body LIKE '%endpoint=\"/responses\"%' ORDER BY id";
            command.Parameters.AddWithValue("$last", lastId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastId = Math.Max(lastId, reader.GetInt64(0));
                if (reader.IsDBNull(3) || reader.IsDBNull(4)) continue;
                string body = reader.GetString(4);
                if (!body.Contains("run_sampling_request{", StringComparison.Ordinal)
                    || body.Contains("websocket.warmup=true", StringComparison.Ordinal)) continue;
                var match = TurnId.Match(body);
                if (!match.Success) continue;
                string key = reader.GetString(3) + "/" + match.Groups[1].Value;
                if (!clocks.TryGetValue(key, out var times)) clocks[key] = times = new();
                times.Add(reader.GetInt64(1) + reader.GetInt64(2) / 1e9);
            }
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        { Warning = "请求诊断库不可用，仅使用输出项计时"; }
        cancellationToken.ThrowIfCancellationRequested();
        return Freeze();
    }
    IReadOnlyDictionary<string, double[]> Freeze() => clocks.ToDictionary(p => p.Key, p => p.Value.Order().ToArray());
}
