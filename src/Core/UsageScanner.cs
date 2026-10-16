using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace CodexTpsFloat;

public sealed record ModelItem(string Id, double Start, double End);
public sealed class TurnData
{
    public double? Start { get; set; }
    public double? FirstTokenMs { get; set; }
    public double? Completed { get; set; }
    public string Model { get; set; } = "未知模型";
    public Dictionary<string, ModelItem> Items { get; } = new();
}
public sealed record UsageRecord(string Id, string Thread, string Turn, double Completed,
    long Input, long Cached, long Output, long Reasoning);
public sealed record TimedUsage(UsageRecord Usage, string Model, double? StreamStart, double? StreamSeconds)
{
    public double? Tps => StreamSeconds is > 0 ? Usage.Output / StreamSeconds : null;
}
public sealed record WindowStats(int Seconds, double? GenerationTps, int GenerationSamples,
    double InputRate, double CachedRate, double OutputRate, double ReasoningRate,
    double RequestsPerMinute, int ActiveSessions, double CacheRatio, double? TurnFirstTokenMs);
public sealed record HourStats(string Hour, int Samples, double? Median, double? Weighted);
public sealed record UsageSnapshot(DateTimeOffset GeneratedAt, IReadOnlyList<TimedUsage> Records,
    IReadOnlyDictionary<string, TurnData> Turns, int Files, int InvalidLines, int UnsupportedFiles,
    int ReadFailures, double ScanMilliseconds);

public sealed class UsageScanner
{
    const int ReadBufferSize = 64 * 1024;
    // Retain one normal buffer; unusually long JSONL lines must not leave large
    // arrays cached in the shared pool after their file has been scanned.
    static readonly ArrayPool<byte> ReadBuffers = ArrayPool<byte>.Create(ReadBufferSize, 1);

    sealed class Cursor
    {
        public const int TailCapacity = 256;
        public long Offset;
        public long Creation;
        public long LastLength;
        public byte[]? Prefix;
        public int PrefixLength;
        public byte[]? Tail;
        public int TailLength;
        public string Thread = "";
        public string Turn = "";
        public bool HasUsage;
        public bool HasLegacyUsage;

        // Keep a sliding window of the most recently consumed bytes so a replaced or
        // rewritten file can be detected without relying on birth time, which Unix
        // filesystems often do not expose (and ctime changes on every append there).
        public void RememberBytes(ReadOnlySpan<byte> data)
        {
            if (PrefixLength < TailCapacity)
            {
                Prefix ??= new byte[TailCapacity];
                int take = Math.Min(data.Length, TailCapacity - PrefixLength);
                data[..take].CopyTo(Prefix.AsSpan(PrefixLength));
                PrefixLength += take;
            }
            Tail ??= new byte[TailCapacity];
            if (data.Length >= TailCapacity)
            {
                data[^TailCapacity..].CopyTo(Tail);
                TailLength = TailCapacity;
                return;
            }
            int keep = Math.Min(TailLength, TailCapacity - data.Length);
            int drop = TailLength - keep;
            if (drop > 0) Tail.AsSpan(drop, keep).CopyTo(Tail);
            data.CopyTo(Tail.AsSpan(keep));
            TailLength = keep + data.Length;
        }
    }
    // Windows paths are case-insensitive; Unix paths are not, so two logs that differ only
    // by case must keep separate cursors there.
    static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    readonly Dictionary<string, Cursor> cursors = new(PathComparer);
    readonly Dictionary<string, UsageRecord> usage = new();
    readonly Dictionary<string, TurnData> turns = new();
    readonly string home;
    int invalidLines;
    public IReadOnlyDictionary<string, double[]> RequestStarts { get; set; } = new Dictionary<string, double[]>();
    public UsageScanner(string codexHome) => home = Path.GetFullPath(codexHome);
    static string Str(JsonElement e, string k, string fallback = "") =>
        e.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? fallback : fallback;
    static double? Num(JsonElement e, string k) =>
        e.TryGetProperty(k, out var p) && p.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    static long Count(JsonElement e, string k) =>
        e.TryGetProperty(k, out var p) && p.TryGetInt64(out var n) && n >= 0 ? n : 0;
    static bool HasOutputUsage(JsonElement info, string key) =>
        info.TryGetProperty(key, out var u) && u.ValueKind == JsonValueKind.Object && Count(u, "output_tokens") > 0;
    TurnData Turn(string id)
    {
        if (!turns.TryGetValue(id, out var turn)) turns[id] = turn = new();
        return turn;
    }
    public void Ingest(string line, string sourceThread = "")
    {
        var cursor = new Cursor { Thread = sourceThread };
        Parse(Encoding.UTF8.GetBytes(line), cursor);
    }
    static bool IsTelemetryLine(ReadOnlySpan<byte> line)
    {
        var reader = new Utf8JsonReader(line);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return true;
        bool relevant = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                reader.Read(); // Validate that no trailing JSON or garbage follows the object.
                return relevant;
            }
            bool isType = reader.ValueTextEquals("type"u8);
            reader.Read();
            if (isType)
                relevant = reader.TokenType == JsonTokenType.String
                    && (reader.ValueTextEquals("session_meta"u8) || reader.ValueTextEquals("turn_context"u8)
                        || reader.ValueTextEquals("event_msg"u8) || reader.ValueTextEquals("token_usage_record"u8));
            reader.Skip();
        }
        return relevant;
    }
    void Parse(ReadOnlyMemory<byte> line, Cursor cursor)
    {
        // JsonDocument rents metadata buffers based on input size, even for ignored
        // tool output. Validate large irrelevant lines without building a document.
        if (line.Length >= ReadBufferSize && !IsTelemetryLine(line.Span)) return;
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) return;
        string type = Str(root, "type");
        if (type == "session_meta") { cursor.Thread = Str(p, "id", Str(p, "session_id")); return; }
        if (!DateTimeOffset.TryParse(Str(root, "timestamp"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date)) return;
        double timestamp = date.ToUnixTimeMilliseconds() / 1000d;
        string turnId = Str(p, "turn_id", cursor.Turn);
        if (type == "turn_context")
        {
            cursor.Turn = turnId;
            if (turnId.Length > 0) Turn(turnId).Model = Str(p, "model", "未知模型");
            return;
        }
        if (type == "event_msg")
        {
            string eventType = Str(p, "type");
            if (eventType == "token_count")
            {
                // Rate-limit-only updates do not imply a generated response.
                if (p.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                    && (HasOutputUsage(info, "total_token_usage") || HasOutputUsage(info, "last_token_usage")))
                    cursor.HasLegacyUsage = true;
                return;
            }
            if (turnId.Length == 0) return;
            var turn = Turn(turnId);
            if (eventType == "task_started")
            {
                cursor.Turn = turnId;
                turn.Start = timestamp;
            }
            else if (eventType == "task_complete")
            {
                turn.FirstTokenMs = Num(p, "time_to_first_token_ms");
                turn.Completed = timestamp;
            }
            else if (eventType == "item_completed" && p.TryGetProperty("item", out var item))
            {
                string itemType = Str(item, "type");
                if (itemType is not ("AgentMessage" or "Reasoning")) return;
                double? start = Num(p, "started_at_ms");
                double? end = Num(p, "completed_at_ms");
                string id = Str(item, "id");
                if (start.HasValue && end.HasValue && end >= start && id.Length > 0)
                    turn.Items[id] = new(id, start.Value / 1000, end.Value / 1000);
            }
            return;
        }
        if (type != "token_usage_record") return;
        cursor.HasUsage = true;
        if (!p.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;
        string responseId = Str(p, "response_id");
        if (responseId.Length == 0 || turnId.Length == 0 || !u.TryGetProperty("output_tokens", out var output)
            || !output.TryGetInt64(out var outputCount) || outputCount < 0) { invalidLines++; return; }
        long input = Count(u, "input_tokens");
        long cached = Count(u, "cached_input_tokens");
        long reasoning = Count(u, "reasoning_output_tokens");
        if (cached > input || reasoning > outputCount) { invalidLines++; return; }
        usage[responseId] = new(responseId, Str(p, "thread_id", cursor.Thread), turnId, timestamp,
            input, cached, outputCount, reasoning);
    }
    long ReadCompleteLines(FileStream stream, Cursor cursor, CancellationToken cancellationToken)
    {
        byte[] buffer = ReadBuffers.Rent(ReadBufferSize);
        int buffered = 0;
        long discardedBytes = 0;
        try
        {
            int read;
            while ((read = stream.Read(buffer, buffered, buffer.Length - buffered)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                buffered += read;
                int consumed = 0;
                while (consumed < buffered)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int newline = buffer.AsSpan(consumed, buffered - consumed).IndexOf((byte)'\n');
                    if (newline < 0) break;
                    var line = buffer.AsMemory(consumed, newline);
                    if (!line.Span.Trim(" \t\r\v\f"u8).IsEmpty)
                    {
                        try { Parse(line, cursor); }
                        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
                        { invalidLines++; }
                    }
                    cursor.RememberBytes(buffer.AsSpan(consumed, newline + 1));
                    consumed += newline + 1;
                    cursor.Offset += newline + 1; // Byte offsets also cover CRLF and multibyte UTF-8.
                }
                buffered -= consumed;
                if (buffered > 0 && consumed > 0) Buffer.BlockCopy(buffer, consumed, buffer, 0, buffered);
                if (buffered == buffer.Length)
                {
                    byte[] larger = ReadBuffers.Rent(checked(buffer.Length * 2));
                    Buffer.BlockCopy(buffer, 0, larger, 0, buffered);
                    ReturnBuffer(buffer);
                    buffer = larger;
                }
            }
            // Leave an unfinished tail at its original offset for the next refresh.
        }
        finally { ReturnBuffer(buffer); }
        return discardedBytes;

        void ReturnBuffer(byte[] returned)
        {
            if (returned.Length > ReadBufferSize) discardedBytes += returned.Length;
            ReadBuffers.Return(returned, clearArray: true);
        }
    }
    static bool BytesMatch(FileStream stream, long offset, ReadOnlySpan<byte> expected)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        Span<byte> probe = stackalloc byte[Cursor.TailCapacity];
        int read = 0;
        while (read < expected.Length)
        {
            int count = stream.Read(probe[read..expected.Length]);
            if (count <= 0) return false;
            read += count;
        }
        return probe[..expected.Length].SequenceEqual(expected);
    }
    // A replacement can retain its suffix and filesystem timestamp. Check the first
    // consumed bytes too, without rereading the entire log on every refresh.
    static bool ContentMatches(FileStream stream, Cursor cursor) =>
        cursor.Prefix != null && cursor.Tail != null && stream.Length >= cursor.Offset
        && BytesMatch(stream, 0, cursor.Prefix.AsSpan(0, cursor.PrefixLength))
        && BytesMatch(stream, cursor.Offset - cursor.TailLength, cursor.Tail.AsSpan(0, cursor.TailLength));
    public UsageSnapshot Refresh(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var files = new List<string>();
        int failures = 0;
        long discardedBufferBytes = 0;
        foreach (string directory in new[] { Path.Combine(home, "sessions"), Path.Combine(home, "archived_sessions") })
        {
            if (!Directory.Exists(directory)) continue;
            try
            {
                foreach (string path in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    files.Add(path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failures++; }
        }
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!cursors.TryGetValue(path, out var cursor)) cursors[path] = cursor = new();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                long length = stream.Length;
                long creation = File.GetCreationTimeUtc(stream.SafeFileHandle).Ticks;
                if (cursor.Offset == 0 && stream.Length >= 3)
                {
                    if (stream.ReadByte() == 0xEF && stream.ReadByte() == 0xBB && stream.ReadByte() == 0xBF)
                    {
                        cursor.Offset = 3;
                        ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
                        cursor.RememberBytes(bom);
                    }
                }
                // Only a cursor that already consumed bytes can be invalidated; an empty
                // cursor must always fall through to reading so a lone partial line works.
                // Unix can fall back to ctime for creation time, so a changed timestamp
                // there only invalidates a same-size file, not an ordinary append.
                bool replaced = cursor.Creation != 0 && cursor.Creation != creation
                    && (OperatingSystem.IsWindows() || length == cursor.LastLength);
                if (cursor.Offset > 0 && (length < cursor.Offset || replaced || !ContentMatches(stream, cursor)))
                {
                    // Rebuild after replacement/truncation; stale usage must not survive a reset.
                    cursors.Clear(); usage.Clear(); turns.Clear(); invalidLines = 0;
                    return Refresh(now, cancellationToken);
                }
                cursor.Creation = creation;
                cursor.LastLength = length;
                if (stream.Length == cursor.Offset) continue;
                stream.Seek(cursor.Offset, SeekOrigin.Begin);
                discardedBufferBytes += ReadCompleteLines(stream, cursor, cancellationToken);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failures++; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var records = TimedRecords(now.ToUnixTimeMilliseconds() / 1000d, cancellationToken);
        watch.Stop();
        var frozenTurns = turns.ToDictionary(pair => pair.Key, pair => new TurnData
        {
            Start = pair.Value.Start, Model = pair.Value.Model,
            FirstTokenMs = pair.Value.FirstTokenMs, Completed = pair.Value.Completed
        });
        // A bulk scan can discard many large arrays without further LOH allocations
        // to trigger their collection. Reclaim them in the background after that scan.
        if (discardedBufferBytes >= 8 * 1024 * 1024)
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
        return new(now, records, frozenTurns, files.Count, invalidLines,
            cursors.Values.Count(c => c.HasLegacyUsage && !c.HasUsage), failures, watch.Elapsed.TotalMilliseconds);
    }
    public IReadOnlyList<TimedUsage> TimedRecords(double cutoff = double.PositiveInfinity, CancellationToken cancellationToken = default)
    {
        var result = new List<TimedUsage>();
        foreach (var group in usage.Values.Where(r => r.Completed <= cutoff).GroupBy(r => r.Turn))
        {
            cancellationToken.ThrowIfCancellationRequested();
            turns.TryGetValue(group.Key, out var turn);
            double previous = turn?.Start ?? double.NegativeInfinity;
            foreach (var r in group.OrderBy(r => r.Completed))
            {
                double itemCutoff = previous;
                if (RequestStarts.TryGetValue(r.Thread + "/" + r.Turn, out var boundaries))
                {
                    foreach (double boundary in boundaries)
                        if (boundary >= previous - .25 && boundary < r.Completed)
                            itemCutoff = Math.Max(previous, boundary - .05);
                }
                double? start = null, seconds = null;
                double first = double.PositiveInfinity;
                bool hasDuration = false;
                if (turn != null && r.Output > 0)
                {
                    foreach (var item in turn.Items.Values)
                    {
                        if (item.Start < itemCutoff || item.End > r.Completed + .001) continue;
                        first = Math.Min(first, item.Start);
                        hasDuration |= item.End > item.Start;
                    }
                }
                double elapsed = r.Completed - first;
                if (hasDuration && elapsed > 0) { start = first; seconds = elapsed; }
                result.Add(new(r, turn?.Model ?? "未知模型", start, seconds));
                previous = r.Completed;
            }
        }
        return result;
    }
    public static WindowStats Window(UsageSnapshot snapshot, int seconds, string? model = null)
    {
        if (seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        double now = snapshot.GeneratedAt.ToUnixTimeMilliseconds() / 1000d;
        var rows = snapshot.Records.Where(r => r.Usage.Completed > now - seconds && r.Usage.Completed <= now
            && (model == null || r.Model == model)).ToArray();
        var valid = rows.Where(r => r.StreamSeconds is > 0).ToArray();
        long input = rows.Sum(r => r.Usage.Input), cached = rows.Sum(r => r.Usage.Cached);
        var ttft = rows.Select(r => r.Usage.Turn).Distinct().Select(id => snapshot.Turns.GetValueOrDefault(id))
            .Where(t => t?.Completed > now - seconds && t?.Completed <= now && t.FirstTokenMs is > 0)
            .Select(t => t!.FirstTokenMs!.Value).ToArray();
        return new(seconds, valid.Length > 0 ? valid.Sum(r => (double)r.Usage.Output) / valid.Sum(r => r.StreamSeconds!.Value) : null,
            valid.Length, input / (double)seconds, cached / (double)seconds,
            rows.Sum(r => (double)r.Usage.Output) / seconds, rows.Sum(r => (double)r.Usage.Reasoning) / seconds,
            rows.Length * 60d / seconds, rows.Select(r => r.Usage.Thread).Distinct().Count(),
            input > 0 ? cached / (double)input : 0, ttft.Length > 0 ? Median(ttft) : null);
    }
    public static double Median(IEnumerable<double> source)
    {
        var a = source.Order().ToArray();
        if (a.Length == 0) throw new ArgumentException("没有样本");
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }
    public static IReadOnlyList<HourStats> Hours(UsageSnapshot snapshot, DateTime date, string? model = null)
    {
        var tz = TimeSpan.FromHours(8);
        var start = new DateTimeOffset(date.Date, tz).ToUnixTimeSeconds();
        var rows = snapshot.Records.Where(r => r.StreamStart >= start && r.StreamStart < start + 86400
            && r.StreamSeconds is > 0 && (model == null || r.Model == model)).ToArray();
        long asOf = snapshot.GeneratedAt.ToUnixTimeSeconds();
        return Enumerable.Range(0, 24).Where(hour => start + hour * 3600 <= asOf).Select(hour =>
        {
            var a = rows.Where(r => DateTimeOffset.FromUnixTimeMilliseconds((long)(r.StreamStart!.Value * 1000))
                .ToOffset(tz).Hour == hour).ToArray();
            return new HourStats($"{hour:00}:00–{hour + 1:00}:00", a.Length,
                a.Length > 0 ? Median(a.Select(r => r.Tps!.Value)) : null,
                a.Length > 0 ? a.Sum(r => (double)r.Usage.Output) / a.Sum(r => r.StreamSeconds!.Value) : null);
        }).ToArray();
    }
}
