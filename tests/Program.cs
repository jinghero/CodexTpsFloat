using System.Text;
using System.Text.Json;
using CodexTpsFloat;

if (args.Length == 6 && args[0] == "--single-instance-child")
{
    string instanceName = args[1], stateDirectory = args[2], readyPath = args[3];
    string wakePath = args[4], observedPath = args[5];
    using var instance = SingleInstance.Create(instanceName,
        () =>
        {
            Thread.Sleep(400); // Simulate UI startup/dispatch before acknowledging.
            File.WriteAllText(observedPath, "observed");
        }, stateDirectory);
    if (!instance.IsFirstInstance) return 3;
    File.WriteAllText(readyPath, "ready");
    for (int attempt = 0; attempt < 100; attempt++)
    {
        if (File.Exists(observedPath)) return 0;
        Thread.Sleep(50);
    }
    return 4;
}

static string Event(string type, object payload, double second) => JsonSerializer.Serialize(new
{
    type, timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)(second * 1000)).ToString("O"), payload
});
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Near(double? actual, double expected, string message) => Check(actual.HasValue && Math.Abs(actual.Value - expected) < .001, message + $"：{actual}");
const double origin = 1791907200; // 2026-10-14 00:00 UTC+8.
static void Seed(UsageScanner s, double t = origin)
{
    s.Ingest(Event("event_msg", new { type = "task_started", turn_id = "turn-a" }, t));
    s.Ingest(Event("turn_context", new { turn_id = "turn-a", model = "model-a" }, t));
    s.Ingest(Event("event_msg", new { type = "item_completed", turn_id = "turn-a", started_at_ms = (t + 2) * 1000,
        completed_at_ms = (t + 6) * 1000, item = new { type = "Reasoning", id = "item-a" } }, t + 6));
    s.Ingest(Event("token_usage_record", new { turn_id = "turn-a", thread_id = "thread-a", response_id = "response-a",
        usage = new { input_tokens = 1000, cached_input_tokens = 800, output_tokens = 100, reasoning_output_tokens = 60 } }, t + 7));
}
var scanner = new UsageScanner(".");
Seed(scanner); Seed(scanner);
var rows = scanner.TimedRecords();
Check(rows.Count == 1, "重复 response_id 去重"); Near(rows[0].Tps, 20, "输出包含推理但不重复加推理");
Check(rows[0].Usage.Output == 100 && rows[0].Usage.Input == 1000, "输入不参与生成 TPS");
var now = DateTimeOffset.FromUnixTimeSeconds((long)origin + 60).ToOffset(TimeSpan.FromHours(8));
var snap = new UsageSnapshot(now, rows, new Dictionary<string, TurnData>(), 1, 0, 0, 0, 0);
var stats = UsageScanner.Window(snap, 60);
Near(stats.InputRate, 1000d / 60, "窗口吞吐分母使用完整窗口"); Near(stats.OutputRate, 100d / 60, "输出吞吐和生成速度不同");
Near(stats.GenerationTps, 20, "生成速度按持续时间加权"); Near(stats.CacheRatio, .8, "缓存是输入子集");
Near(stats.RequestsPerMinute, 1, "完成请求计数"); Check(stats.ActiveSessions == 1, "活跃会话去重");
Check(UsageScanner.Window(snap, 5).GenerationTps == null, "空窗口不能显示 0 生成速度");
var hours = UsageScanner.Hours(snap, now.Date);
Check(hours.Count == 1 && hours[0].Samples == 1, "北京时间零点后仅显示已经开始的小时");
Near(hours[0].Median, 20, "小时中位数");
var midday = snap with { GeneratedAt = new DateTimeOffset(now.Date.AddHours(12.5), TimeSpan.FromHours(8)) };
var todayHours = UsageScanner.Hours(midday, now.Date);
Check(todayHours.Count == 13 && todayHours[0].Hour == "00:00–01:00" && todayHours[^1].Hour == "12:00–13:00",
    "今日分时隐藏未来小时并按时间正序排列");
Check(todayHours[0].Samples == 1 && todayHours[^1].Median == null && todayHours[^1].Samples == 0,
    "已发生但无样本的小时继续显示空值");
var yesterdayHours = UsageScanner.Hours(midday, now.Date.AddDays(-1));
Check(yesterdayHours.Count == 24 && yesterdayHours[0].Hour == "00:00–01:00" && yesterdayHours[^1].Hour == "23:00–24:00",
    "历史日期显示完整一天并按时间正序排列");
Check(UsageScanner.Hours(midday, now.Date.AddDays(1)).Count == 0, "未来日期不显示未发生的小时");
Check(UsageScanner.Hours(midday with { GeneratedAt = midday.GeneratedAt.ToUniversalTime() }, now.Date)
    .Select(h => h.Hour).SequenceEqual(todayHours.Select(h => h.Hour)), "未来小时判断保持北京时间口径");

var zero = new UsageScanner(".");
zero.Ingest(Event("event_msg", new { type = "task_started", turn_id = "z" }, origin));
zero.Ingest(Event("event_msg", new { type = "item_completed", turn_id = "z", started_at_ms = (origin + 1) * 1000,
    completed_at_ms = (origin + 1) * 1000, item = new { type = "Reasoning", id = "z" } }, origin + 1));
zero.Ingest(Event("token_usage_record", new { turn_id = "z", thread_id = "z", response_id = "z", usage = new { output_tokens = 100 } }, origin + 1.1));
Check(zero.TimedRecords()[0].Tps == null, "零持续时间不产生虚高 TPS");
var concurrent = new UsageScanner("."); Seed(concurrent);
concurrent.Ingest(Event("event_msg", new { type = "task_started", turn_id = "b" }, origin));
concurrent.Ingest(Event("turn_context", new { turn_id = "b", model = "model-b" }, origin));
concurrent.Ingest(Event("event_msg", new { type = "item_completed", turn_id = "b", started_at_ms = (origin + 2) * 1000,
    completed_at_ms = (origin + 6) * 1000, item = new { type = "AgentMessage", id = "b" } }, origin + 6));
concurrent.Ingest(Event("token_usage_record", new { turn_id = "b", thread_id = "thread-b", response_id = "b", usage = new { output_tokens = 200 } }, origin + 7));
var both = new UsageSnapshot(now, concurrent.TimedRecords(), new Dictionary<string, TurnData>(), 1, 0, 0, 0, 0);
Near(UsageScanner.Window(both, 60).GenerationTps, 30, "并发耗时分别相加");
Near(UsageScanner.Window(both, 60, "model-b").GenerationTps, 40, "模型筛选");
var retry = new UsageScanner("."); Seed(retry);
retry.RequestStarts = new Dictionary<string, double[]> { ["thread-a/turn-a"] = new[] { origin + 3 } };
Check(retry.TimedRecords()[0].Tps == null, "不把失败尝试的输出项计入后续请求的计时");

var directory = Path.Combine(Path.GetTempPath(), "codex-tps-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(directory, "sessions"));
Directory.CreateDirectory(Path.Combine(directory, "archived_sessions"));
try
{
    string file = Path.Combine(directory, "sessions", "test.jsonl");
    string row = Event("token_usage_record", new { turn_id = "partial", response_id = "partial", thread_id = "p", usage = new { output_tokens = 10 } }, origin + 4);
    File.WriteAllText(file, row[..(row.Length / 2)], new UTF8Encoding(true));
    var inc = new UsageScanner(directory);
    Check(inc.Refresh(now).Records.Count == 0, "未完成尾行不计数");
    File.AppendAllText(file, row[(row.Length / 2)..] + "\n", new UTF8Encoding(false));
    var initial = inc.Refresh(now);
    Check(initial.Records.Count == 1 && initial.InvalidLines == 0, "UTF8 BOM 与尾行补全");
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        bool scanCanceled = false, clocksCanceled = false;
        try { inc.Refresh(now, canceled.Token); }
        catch (OperationCanceledException) { scanCanceled = true; }
        try { new RequestClockReader(directory).Refresh(canceled.Token); }
        catch (OperationCanceledException) { clocksCanceled = true; }
        Check(scanCanceled && clocksCanceled, "扫描器和诊断库读取响应取消请求");
    }
    Check(inc.Refresh(now).Records.Count == 1, "重复刷新不重复计数");
    File.Move(file, Path.Combine(directory, "archived_sessions", "test.jsonl"));
    Check(inc.Refresh(now).Records.Count == 1, "归档移动不重复计数");
    File.WriteAllText(Path.Combine(directory, "archived_sessions", "test.jsonl"), "{}\n", Encoding.UTF8);
    Check(inc.Refresh(now).Records.Count == 0, "截断文件清除陈旧统计");
    File.WriteAllText(file, "bad-json\n" + row + "\n", Encoding.UTF8);
    var partial = inc.Refresh(now);
    Check(partial.InvalidLines == 1 && partial.Records.Count == 1, "坏记录不阻断有效记录");

    // A rewrite that keeps the length identical must still be detected; on Unix systems
    // birth time is unavailable, so identity has to come from the consumed bytes.
    string replacedRoot = Path.Combine(directory, "replaced");
    Directory.CreateDirectory(Path.Combine(replacedRoot, "sessions"));
    string replacedFile = Path.Combine(replacedRoot, "sessions", "same-length.jsonl");
    File.WriteAllText(replacedFile, row + "\n", new UTF8Encoding(false));
    var replaced = new UsageScanner(replacedRoot);
    Check(replaced.Refresh(now).Records.Count == 1, "重写前计入一条");
    string other = row.Replace("\"response_id\":\"partial\"", "\"response_id\":\"rewrite\"");
    Check(other.Length == row.Length, "重写样本保持等长");
    File.WriteAllText(replacedFile, other + "\n", new UTF8Encoding(false));
    var afterReplace = replaced.Refresh(now);
    Check(afterReplace.Records.Count == 1 && afterReplace.Records[0].Usage.Id == "rewrite",
        "等长重写后重建游标并采用新内容");

    // Replacing a log can preserve its last 256 bytes while changing earlier usage.
    string sameSuffix = Event("event_msg", new { type = "noop", text = new string('x', 600) }, 107) + "\n";
    File.WriteAllText(replacedFile, row + "\n" + sameSuffix, new UTF8Encoding(false));
    replaced = new UsageScanner(replacedRoot);
    Check(replaced.Refresh(now).Records.Single().Usage.Id == "partial", "相同尾部替换前读取原始用量");
    string replacementFile = replacedFile + ".replacement";
    File.WriteAllText(replacementFile, other + "\n" + sameSuffix, new UTF8Encoding(false));
    if (OperatingSystem.IsWindows())
        File.SetCreationTimeUtc(replacementFile, File.GetCreationTimeUtc(replacedFile).AddMinutes(1));
    File.Move(replacementFile, replacedFile, true);
    Check(replaced.Refresh(now).Records.Single().Usage.Id == "rewrite",
        "等长替换保留相同尾部时仍重建旧用量");

    // Coarse timestamps can collide on a quick replacement; verify the content fallback.
    File.WriteAllText(replacedFile, row + "\n" + sameSuffix, new UTF8Encoding(false));
    replaced = new UsageScanner(replacedRoot);
    Check(replaced.Refresh(now).Records.Single().Usage.Id == "partial", "时间戳碰撞前读取原始用量");
    File.WriteAllText(replacementFile, other + "\n" + sameSuffix, new UTF8Encoding(false));
    File.SetLastWriteTimeUtc(replacementFile, File.GetLastWriteTimeUtc(replacedFile));
    if (OperatingSystem.IsWindows())
        File.SetCreationTimeUtc(replacementFile, File.GetCreationTimeUtc(replacedFile));
    File.Move(replacementFile, replacedFile, true);
    Check(replaced.Refresh(now).Records.Single().Usage.Id == "rewrite",
        "时间戳相同且尾部相同时仍检查文件头");

    string streamRoot = Path.Combine(directory, "streaming");
    Directory.CreateDirectory(Path.Combine(streamRoot, "sessions"));
    string streamFile = Path.Combine(streamRoot, "sessions", "unicode.jsonl");
    string longLine = "{\"type\":\"response_item\",\"payload\":{\"text\":\"" + new string('中', 1500000) + "\"}}\r\n";
    string unicodeRow = row.Replace("\"thread_id\":\"p\"", "\"thread_id\":\"中文线程\"");
    byte[] unicodeBytes = Encoding.UTF8.GetBytes(unicodeRow + "\r\n");
    int middleOfCharacter = Encoding.UTF8.GetByteCount(unicodeRow[..unicodeRow.IndexOf("中文线程", StringComparison.Ordinal)]) + 1;
    File.WriteAllText(streamFile, longLine + " \t \r\n", new UTF8Encoding(true));
    using (var output = new FileStream(streamFile, FileMode.Append)) output.Write(unicodeBytes, 0, middleOfCharacter);
    var streamed = new UsageScanner(streamRoot);
    var unfinished = streamed.Refresh(now);
    Check(unfinished.Records.Count == 0 && unfinished.InvalidLines == 0, "跨缓冲区长行、空白 CRLF 和未完成 UTF8 尾行");
    using (var output = new FileStream(streamFile, FileMode.Append))
        output.Write(unicodeBytes, middleOfCharacter, unicodeBytes.Length - middleOfCharacter);
    var completed = streamed.Refresh(now);
    Check(completed.Records.Count == 1 && completed.InvalidLines == 0
        && completed.Records[0].Usage.Thread == "中文线程", "从中文字符中间补全尾行且按字节推进");
    string secondRow = unicodeRow.Replace("\"response_id\":\"partial\"", "\"response_id\":\"second\"");
    File.AppendAllText(streamFile, secondRow + "\n", new UTF8Encoding(false));
    Check(streamed.Refresh(now).Records.Count == 2 && streamed.Refresh(now).InvalidLines == 0,
        "长行后继续增量读取，CRLF 和 LF 不重复或遗漏记录");
    File.AppendAllText(streamFile, longLine.TrimEnd('\r', '\n') + "junk\n", new UTF8Encoding(false));
    Check(streamed.Refresh(now).InvalidLines == 1, "跳过大型非统计日志时仍校验完整 JSON，保留异常计数");
    string fullLargeObject = longLine.TrimEnd('\r', '\n');
    File.AppendAllText(streamFile, fullLargeObject[..^1] + "\n" + fullLargeObject[..^4] + "\n", new UTF8Encoding(false));
    Check(streamed.Refresh(now).InvalidLines == 3,
        "大型无关记录缺少根结束符或截断字符串仍计入坏记录");

    string cancelHome = Path.Combine(directory, "cancel-scan");
    Directory.CreateDirectory(Path.Combine(cancelHome, "sessions"));
    using (var writer = new StreamWriter(Path.Combine(cancelHome, "sessions", "slow.jsonl")))
    {
        writer.WriteLine("bad-json"); // A visible progress marker before the expensive input.
        for (int i = 0; i < 16; i++) writer.Write(longLine);
        writer.WriteLine(row);
    }
    var midScan = new UsageScanner(cancelHome);
    var invalidField = typeof(UsageScanner).GetField("invalidLines", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    using (var cancellation = new CancellationTokenSource())
    {
        var scan = Task.Run(() => midScan.Refresh(now, cancellation.Token));
        try
        {
            Check(SpinWait.SpinUntil(() => (int)invalidField.GetValue(midScan)! > 0 || scan.IsCompleted, 5000)
                && !scan.IsCompleted, "日志扫描实际处理了首行后才发出取消请求");
            cancellation.Cancel();
            bool canceled = false;
            try { scan.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && midScan.TimedRecords().Count == 0, "中途取消在处理文件尾部用量前停止扫描");
        }
        finally
        {
            cancellation.Cancel();
            try { scan.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
    }

    string clockHome = Path.Combine(directory, "cancel-clocks");
    Directory.CreateDirectory(clockHome);
    using (var database = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = Path.Combine(clockHome, "logs_1.sqlite"), Pooling = false }.ToString()))
    {
        database.Open();
        using var seed = database.CreateCommand();
        seed.CommandText = """
            CREATE TABLE seed (id INTEGER PRIMARY KEY);
            WITH RECURSIVE n(id) AS (VALUES(1) UNION ALL SELECT id+1 FROM n WHERE id<5000)
                INSERT INTO seed SELECT id FROM n;
            CREATE VIEW logs AS SELECT a.id*5000+b.id AS id, 100 AS ts, 0 AS ts_nanos,
                'thread' AS thread_id, 'other' AS target, '' AS feedback_log_body FROM seed a CROSS JOIN seed b;
            """;
        seed.ExecuteNonQuery();
        SQLitePCL.raw.sqlite3_busy_timeout(database.Handle, 0);
        using var cancellation = new CancellationTokenSource();
        var clocks = Task.Run(() => new RequestClockReader(clockHome).Refresh(cancellation.Token));
        try
        {
            bool reading = false;
            Check(SpinWait.SpinUntil(() =>
            {
                int result = SQLitePCL.raw.sqlite3_exec(database.Handle, "BEGIN EXCLUSIVE");
                if (result == SQLitePCL.raw.SQLITE_BUSY) reading = true;
                else
                {
                    Check(result == SQLitePCL.raw.SQLITE_OK, "检查诊断读取的共享锁");
                    SQLitePCL.raw.sqlite3_exec(database.Handle, "ROLLBACK");
                }
                return reading || clocks.IsCompleted;
            }, 5000) && reading && !clocks.IsCompleted, "诊断查询持有读取锁后才发出取消请求");
            cancellation.Cancel();
            bool canceled = false;
            try { clocks.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "没有返回首行的诊断查询可中断，取消不会被降级告警吞掉");
        }
        finally
        {
            cancellation.Cancel();
            try { clocks.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
        seed.CommandText = """
            DROP VIEW logs;
            CREATE TABLE logs (id INTEGER PRIMARY KEY, ts INTEGER, ts_nanos INTEGER,
                thread_id TEXT, target TEXT, feedback_log_body TEXT);
            INSERT INTO logs VALUES (1,100,250000000,'clock-thread','feedback_tags',
                'run_sampling_request{turn.id="clock-turn"} endpoint="/responses"');
            """;
        seed.ExecuteNonQuery();
        var normalClocks = new RequestClockReader(clockHome);
        Check(normalClocks.Refresh()["clock-thread/clock-turn"].Single() == 100.25 && normalClocks.Warning == null
            && normalClocks.Refresh()["clock-thread/clock-turn"].Length == 1,
            "正常诊断查询保持时钟精度、增量去重和无降级状态");
    }

    string lateType = row.Replace("\"type\":\"token_usage_record\",", "")[..^1]
        + ",\"padding\":\"" + new string('x', 70000) + "\",\"type\":\"token_usage_record\"}";
    var lateTypeScanner = new UsageScanner(directory);
    lateTypeScanner.Ingest(lateType);
    Check(lateTypeScanner.TimedRecords().Single().Usage.Id == "partial", "长记录的 type 在 payload 后面时仍计量");
    lateTypeScanner.Ingest(lateType.Replace("\"type\":\"token_usage_record\"",
        "\"type\":\"response_item\",\"type\":\"token_usage_record\""));
    Check(lateTypeScanner.TimedRecords().Count == 1, "长记录按最后一个同名 type 属性计量且保持去重");
    var ignoredTypeScanner = new UsageScanner(directory);
    ignoredTypeScanner.Ingest(lateType.Replace("\"type\":\"token_usage_record\"",
        "\"type\":\"token_usage_record\",\"type\":\"response_item\""));
    Check(ignoredTypeScanner.TimedRecords().Count == 0, "长记录最后一个 type 不参与统计时忽略用量字段");

    string legacyRoot = Path.Combine(directory, "legacy");
    Directory.CreateDirectory(Path.Combine(legacyRoot, "sessions"));
    string legacyFile = Path.Combine(legacyRoot, "sessions", "aborted.jsonl");
    File.WriteAllText(legacyFile, string.Join("\n",
        Event("event_msg", new { type = "task_started", turn_id = "aborted" }, origin),
        Event("event_msg", new { type = "item_completed", turn_id = "aborted",
            item = new { type = "UserMessage", id = "user-item" } }, origin + 1),
        Event("event_msg", new { type = "token_count", info = (object?)null }, origin + 2),
        Event("event_msg", new { type = "turn_aborted", turn_id = "aborted" }, origin + 3)) + "\n", new UTF8Encoding(false));
    var legacy = new UsageScanner(legacyRoot);
    var aborted = legacy.Refresh(now);
    Check(aborted.UnsupportedFiles == 0 && aborted.Records.Count == 0 && aborted.InvalidLines == 0,
        "仅有空 token_count 的中断会话不触发旧格式告警");
    File.AppendAllText(legacyFile,
        Event("event_msg", new { type = "token_count" }, origin + 4) + "\n" +
        Event("event_msg", new { type = "token_count", info = new { } }, origin + 5) + "\n" +
        Event("event_msg", new { type = "token_count", info = new {
            total_token_usage = new { input_tokens = 100, output_tokens = 0 },
            last_token_usage = new { input_tokens = 100, output_tokens = 0 } } }, origin + 6) + "\n", new UTF8Encoding(false));
    Check(legacy.Refresh(now).UnsupportedFiles == 0, "缺失或零输出用量不触发旧格式告警");
    File.AppendAllText(legacyFile, Event("event_msg", new { type = "token_count",
        info = new { total_token_usage = new { output_tokens = 10 } } }, origin + 7) + "\n", new UTF8Encoding(false));
    var actualLegacy = legacy.Refresh(now);
    Check(actualLegacy.UnsupportedFiles == 1 && actualLegacy.Records.Count == 0,
        "增量读到真实旧格式输出用量后仍告警，不虚构响应样本");
    File.AppendAllText(legacyFile, Event("event_msg", new { type = "token_count", info = (object?)null }, origin + 8) + "\n",
        new UTF8Encoding(false));
    Check(legacy.Refresh(now).UnsupportedFiles == 1, "后续空用量事件不清除已有旧格式告警");
    File.WriteAllText(Path.Combine(legacyRoot, "sessions", "last-usage.jsonl"), Event("event_msg",
        new { type = "token_count", info = new { last_token_usage = new { output_tokens = 5 } } }, origin + 9) + "\n",
        new UTF8Encoding(false));
    Check(legacy.Refresh(now).UnsupportedFiles == 2, "逐次旧格式输出用量也触发告警");
    File.AppendAllText(legacyFile, row + "\n", new UTF8Encoding(false));
    var modern = legacy.Refresh(now);
    Check(modern.UnsupportedFiles == 1 && modern.Records.Count == 1 && modern.InvalidLines == 0,
        "同文件出现逐次 token_usage_record 后恢复可计量状态");

    string settingsDirectory = Path.Combine(directory, "settings");
    string expectedConfigRoot = OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support")
        : OperatingSystem.IsLinux()
            ? Path.IsPathRooted(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"))
                ? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")!
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    Check(PlatformPaths.UserConfigDirectory() == Path.Combine(expectedConfigRoot, "CodexTpsFloat"),
        "用户配置目录遵循各平台约定");
    Check(PlatformPaths.UserAutostartDirectory() == Path.Combine(expectedConfigRoot, "autostart"),
        "自动启动目录使用平台配置根目录");

    var store = new SettingsStore(Path.Combine(settingsDirectory, "settings.json"));
    Check(store.Load().WindowSeconds == 300, "缺失设置文件时回退到默认值");
    var custom = new PanelSettings { CodexHome = directory, WindowSeconds = 999, RefreshSeconds = 42,
        Pinned = false, X = 12.5, Y = 34.5 };
    store.Save(custom);
    var loaded = store.Load();
    Check(loaded.WindowSeconds == 300 && loaded.RefreshSeconds == 15, "越界选项在读取时归一化");
    Check(loaded.Pinned == false && loaded.X == 12.5 && loaded.Y == 34.5 && loaded.CodexHome == directory,
        "有效设置项完整往返");
    Check(!File.Exists(store.Path + ".tmp"), "原子保存不残留临时文件");

    var exportHours = new[] { new HourStats("09:00–10:00", 2, 21.5, 22.25), new HourStats("10:00–11:00", 0, null, null) };
    string markdownPath = Path.Combine(directory, "report.md");
    ReportExporter.Write(markdownPath, snap, now.Date, "model-a", exportHours);
    string markdown = File.ReadAllText(markdownPath);
    Check(markdown.Contains("| 09:00–10:00 | 2 | 21.50 | 22.25 |") && markdown.Contains("| 10:00–11:00 | 0 | — | — |"),
        "Markdown 报告包含有效与空样本行");
    string jsonPath = Path.Combine(directory, "report.json");
    ReportExporter.Write(jsonPath, snap, now.Date, null, exportHours);
    using var reportJson = JsonDocument.Parse(File.ReadAllText(jsonPath));
    Check(reportJson.RootElement.GetProperty("timezone").GetString() == "UTC+8"
        && reportJson.RootElement.GetProperty("model").GetString() == "全部模型"
        && reportJson.RootElement.GetProperty("hours").GetArrayLength() == 2, "JSON 报告字段与导出参数一致");
    string unsafeModel = "![remote](https://example.com/pixel.png)\n<link>[injected]";
    ReportExporter.Write(markdownPath, snap, now.Date, unsafeModel, exportHours);
    string escapedMarkdown = File.ReadAllText(markdownPath);
    Check(escapedMarkdown.Contains("模型：`![remote](https://example.com/pixel.png) <link>[injected]`。")
        && !escapedMarkdown.Contains("\n<link>"),
        "Markdown 报告转义来自日志的模型名");

    string entry = OperatingSystem.IsMacOS()
        ? Path.Combine(directory, "LaunchAgents", "com.codextpsfloat.plist")
        : Path.Combine(directory, "autostart", "CodexTpsFloat.desktop");
    IAutostartService login = OperatingSystem.IsMacOS()
        ? new MacAutostart("/Applications/Codex TPS.app", Path.GetDirectoryName(entry))
        : new LinuxAutostart("/opt/codex tps/CodexTpsFloat", Path.GetDirectoryName(entry)!);
    Check(!login.IsEnabled(), "未启用时登录启动为关闭状态");
    Check(login.SetEnabled(true) == null && login.IsEnabled(), "启用登录启动会创建条目");
    string entryText = File.ReadAllText(entry);
    Check(OperatingSystem.IsMacOS()
        ? entryText.Contains("<string>/Applications/Codex TPS.app</string>") && entryText.Contains("<key>RunAtLoad</key>")
        : entryText.Contains("Exec=\"/opt/codex tps/CodexTpsFloat\"") && entryText.StartsWith("[Desktop Entry]"),
        "登录启动条目包含可执行路径与自启标记");
    Check(login.SetEnabled(false) == null && !login.IsEnabled() && !File.Exists(entry), "停用登录启动会移除条目");

    string linuxEntryDirectory = Path.Combine(directory, "linux-escaping");
    var escapedLogin = new LinuxAutostart("/opt/a\"b\\c%d$e`f\napp", linuxEntryDirectory);
    Check(escapedLogin.SetEnabled(true) == null, "Linux 特殊字符路径可写入自启动条目");
    string execLine = File.ReadAllLines(Path.Combine(linuxEntryDirectory, "CodexTpsFloat.desktop"))
        .Single(line => line.StartsWith("Exec="));
    // Decode desktop-entry escapes, then Exec quoting and literal percent codes.
    string decodedExec = execLine[6..^1].Replace("\\n", "\n").Replace("\\\\", "\\");
    var decodedPath = new StringBuilder();
    for (int i = 0; i < decodedExec.Length; i++)
    {
        if (decodedExec[i] == '\\') i++;
        else if (decodedExec[i] == '%' && decodedExec[i + 1] == '%') i++;
        decodedPath.Append(decodedExec[i]);
    }
    Check(decodedPath.ToString() == "/opt/a\"b\\c%d$e`f\napp", "Exec 两层转义后保留原始路径且不插入新条目");
    Check(new LinuxAutostart("/opt/a=b/app", linuxEntryDirectory).SetEnabled(true) == null,
        "允许 Linux 可执行路径包含等号");
    Check(File.ReadAllLines(Path.Combine(linuxEntryDirectory, "CodexTpsFloat.desktop"))
        .Contains("Exec=\"/opt/a=b/app\""), "Desktop Entry 保留可执行路径中的等号");

    string instanceName = "codex-tps-test-" + Guid.NewGuid().ToString("N") + new string('x', 80);
    string instanceDirectory = Path.Combine(directory, "instance");
    string readyPath = Path.Combine(directory, "instance-ready");
    string wakePath = Path.Combine(directory, "instance-wake");
    string observedPath = Path.Combine(directory, "instance-observed");
    using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false,
        ArgumentList =
        {
            "--single-instance-child", instanceName, instanceDirectory,
            readyPath, wakePath, observedPath
        }
    }) ?? throw new Exception("无法启动单实例测试子进程");
    try
    {
        for (int attempt = 0; attempt < 100 && !File.Exists(readyPath); attempt++)
            Thread.Sleep(50);
        Check(File.Exists(readyPath) && !child.HasExited, "子进程获得首个单实例所有权");
        using var secondInstance = SingleInstance.Create(instanceName, stateDirectory: instanceDirectory);
        Check(!secondInstance.IsFirstInstance, "第二个进程不会重复启动界面");
        var handoff = System.Diagnostics.Stopwatch.StartNew();
        secondInstance.SignalExistingInstance();
        Check(handoff.ElapsedMilliseconds >= 350, "唤醒确认等待界面回调完成");
        for (int attempt = 0; attempt < 100 && !File.Exists(observedPath); attempt++)
            Thread.Sleep(50);
        Check(File.Exists(observedPath), "第二个进程会唤醒已有实例");
        Check(child.WaitForExit(5000), "已有实例收到唤醒后正常退出");
        Check(child.ExitCode == 0, "已有实例退出码正确");
        bool timedOut = false;
        try { secondInstance.SignalExistingInstance(TimeSpan.FromMilliseconds(300)); }
        catch (TimeoutException) { timedOut = true; }
        Check(timedOut, "实例退出后不再错误报告唤醒成功");
        Check(!Directory.Exists(instanceDirectory) || !Directory.EnumerateFiles(instanceDirectory, "*.request*").Any(),
            "唤醒超时清理请求与确认文件");
        if (!OperatingSystem.IsWindows())
        {
            string movedDirectory = instanceDirectory + "-moved";
            Directory.Move(instanceDirectory, movedDirectory);
            try
            {
                bool writeFailed = false;
                try { secondInstance.SignalExistingInstance(); }
                catch (IOException) { writeFailed = true; }
                Check(writeFailed, "Unix 唤醒写入失败会报告错误");
            }
            finally { Directory.Move(movedDirectory, instanceDirectory); }

            string lockPath = Directory.GetFiles(instanceDirectory, "*.lock").Single();
            File.Delete(lockPath);
            File.CreateSymbolicLink(lockPath, Path.Combine(instanceDirectory, "missing-parent", "lock"));
            bool lockFailed = false;
            try { using var broken = SingleInstance.Create(instanceName, stateDirectory: instanceDirectory); }
            catch (IOException) { lockFailed = true; }
            Check(lockFailed, "Unix 非竞争锁错误不会伪装成已有实例");
            File.Delete(lockPath);
        }
    }
    finally
    {
        if (!child.HasExited)
        {
            child.Kill(true);
            child.WaitForExit();
        }
    }
}
finally { Directory.Delete(directory, true); }
Console.WriteLine("通过：统计口径、去重、重试区间、时区、增量刷新、归档、截断、等长重写、坏记录、BOM、跨缓冲区长行、CRLF、中文字符中间补全尾行、旧格式告警、设置往返、报告导出、登录启动与单实例唤醒。");
return 0;
