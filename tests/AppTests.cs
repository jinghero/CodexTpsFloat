using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CodexTpsFloat;
using CodexTpsFloat.App;
using AppWindow = CodexTpsFloat.App.MainWindow;
using Path = System.IO.Path;

if (args is [var childMode, var childHome, var childSettings]
    && childMode is "--framework-shutdown" or "--window-exit")
{
    int exitEvents = 0;
    bool shutdownAccepted = false;
    CodexTpsFloat.App.App.Options = new(null, childHome, null);
    AppBuilder.Configure<CodexTpsFloat.App.App>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont().StartWithClassicDesktopLifetime(Array.Empty<string>(), desktop =>
        {
            desktop.Exit += (_, _) => exitEvents++;
            desktop.Startup += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                var appSession = (DesktopSession)typeof(CodexTpsFloat.App.App)
                    .GetField("session", fields)!.GetValue(Application.Current)!;
                if (childMode == "--window-exit") appSession.ShowPanel();
                if (typeof(DesktopSession).GetField("window", fields)!.GetValue(appSession) is AppWindow panel)
                {
                    typeof(AppWindow).GetField("settingsStore", fields)!.SetValue(panel, new SettingsStore(childSettings));
                    if (childMode == "--window-exit")
                    {
                        panel.Close();
                        shutdownAccepted = true;
                    }
                }
                if (childMode == "--framework-shutdown") shutdownAccepted = desktop.TryShutdown();
            });
        });
    if (!shutdownAccepted || exitEvents != 1)
        throw new Exception($"Framework shutdown was reentered: accepted={shutdownAccepted}, exitEvents={exitEvents}");
    Console.WriteLine($"PASS the real desktop lifetime handles {childMode} exactly once");
    return;
}

AppBuilder.Configure<CodexTpsFloat.App.App>().UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
string root = Path.Combine(Path.GetTempPath(), "codex-export-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var scope = new Cleanup(root);
const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
var window = new AppWindow(capturePath: "test");
var generatedAt = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.FromHours(8));
double completedAt = generatedAt.AddSeconds(-1).ToUnixTimeMilliseconds() / 1000d;
var timedUsage = new TimedUsage(new UsageRecord("history-response", "history-thread", "history-turn",
    completedAt, 1000, 500, 120, 40), "history-model", completedAt - 4, 4);
typeof(AppWindow).GetField("snapshot", flags)!.SetValue(window,
    new UsageSnapshot(generatedAt, new[] { timedUsage }, new Dictionary<string, TurnData>(), 1, 0, 0, 0, 0));
var datePicker = window.FindControl<CalendarDatePicker>("DateBox")!;
datePicker.SelectedDate = generatedAt.Date;
window.Show();
int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); passed++; }
var dragGuard = typeof(AppWindow).GetMethod("HasInteractiveAncestor", BindingFlags.Static | BindingFlags.NonPublic)!;
Check((bool)dragGuard.Invoke(null, new object[] { new RadioButton() })!, "duration radio buttons are excluded from panel dragging");
Check((bool)dragGuard.Invoke(null, new object[] { new CheckBox() })!, "settings checkboxes are excluded from panel dragging");
Check((bool)dragGuard.Invoke(null, new object[] { datePicker })!, "the calendar date selector is excluded from panel dragging");
window.FindControl<Button>("HistoryButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Dispatcher.UIThread.RunJobs();
window.UpdateLayout();
var chart = window.FindControl<HourlyChart>("HourChart")!;
var displayedHours = chart.Hours;
Check(displayedHours.Count == 13 && displayedHours[0].Hour == "00:00–01:00"
    && displayedHours[^1].Hour == "12:00–13:00" && displayedHours.Sum(h => h.Samples) == 1,
    "the chart hides future hours and orders elapsed hours chronologically");
Check(datePicker.Text == "2026-10-16", "the date selector displays a compact year-month-day value");
Check((bool)dragGuard.Invoke(null, new object[] { chart })!, "the chart is excluded from panel dragging");
Check(chart.Children.OfType<Ellipse>().Count() == 2 && !chart.Children.OfType<Polyline>().Any(),
    "a lone measured hour shows both series as points without bridging empty hours");
Check(chart.Children.OfType<Rectangle>().Any(r => ToolTip.GetTip(r) is string tip
    && tip.Contains("11:00–12:00（北京时间）") && tip.Contains("样本 1") && tip.Contains("加权 TPS：30.0")),
    "hourly tooltips include Beijing time, sample count and measured TPS");
chart.SetHours(new[]
{
    new HourStats("03:00–04:00", 1, 25, 30),
    new HourStats("02:00–03:00", 0, null, null),
    new HourStats("01:00–02:00", 1, 18, 20),
    new HourStats("00:00–01:00", 1, 8, 10)
});
var lines = chart.Children.OfType<Polyline>().ToArray();
Check(lines.Length == 2 && lines.All(l => l.Points.Count == 2
    && l.Points[0].X < l.Points[1].X && l.Points[0].Y > l.Points[1].Y)
    && lines.Count(l => l.StrokeDashArray?.Count > 0) == 1,
    "both TPS series connect adjacent hours in time order and stop at gaps");
Check(chart.Children.OfType<Ellipse>().Count() == 6,
    "an isolated measured hour after a gap remains visible in both series");
chart.SetHours(new[] { new HourStats("00:00–01:00", 0, null, null) });
Check(!chart.Children.OfType<Polyline>().Any() && !chart.Children.OfType<Ellipse>().Any()
    && chart.Children.OfType<TextBlock>().Any(t => t.Text == "暂无有效计时样本"),
    "an empty day shows an explicit empty state without zero-valued data points");
chart.SetHours(displayedHours);
typeof(AppWindow).GetField("initialized", flags)!.SetValue(window, true);
datePicker.SelectedDate = generatedAt.Date.AddDays(-1);
Dispatcher.UIThread.RunJobs();
Check(chart.Hours.Count == 24,
    "choosing a historical date updates the hourly chart");
datePicker.SelectedDate = generatedAt.Date;
Dispatcher.UIThread.RunJobs();
Button[] Buttons(Window dialog) => ((StackPanel)((StackPanel)dialog.Content!).Children[1]).Children.OfType<Button>().ToArray();
void Click(Window dialog, string label)
{
    Buttons(dialog).Single(b => Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();
}
void Export(string pickedPath, string pattern)
{
    var file = DispatchProxy.Create<IStorageFile, TestProxy>();
    ((TestProxy)(object)file).Handler = (m, a) => m.Name switch { "get_Path" => new Uri(pickedPath), "get_Name" => Path.GetFileName(pickedPath), _ => null };
    var provider = DispatchProxy.Create<IStorageProvider, TestProxy>();
    ((TestProxy)(object)provider).Handler = (m, a) =>
    {
        if (m.Name != "SaveFilePickerWithResultAsync") throw new Exception(m.Name);
        var options = (FilePickerSaveOptions)a![0]!;
        if (options.ShowOverwritePrompt != true) throw new Exception("Native overwrite prompt disabled");
        return Task.FromResult(new SaveFilePickerResult { File = file, SelectedFileType = options.FileTypeChoices!.Single(t => t.Patterns!.Contains(pattern)) });
    };
    typeof(TopLevel).GetField("_storageProvider", flags)!.SetValue(window, provider);
    typeof(AppWindow).GetMethod("ExportClick", flags)!.Invoke(window, new object?[] { null, new RoutedEventArgs() });
    Dispatcher.UIThread.RunJobs();
}
string picked = Path.Combine(root, "report.md"), target = Path.Combine(root, "report.json");
const string original = "existing data";
File.WriteAllText(target, original);
Export(picked, "*.json");
var dialog = window.OwnedWindows.Single();
Check(Buttons(dialog).Length == 2 && File.ReadAllText(target) == original, "extension change prompts before touching existing JSON");
Click(dialog, "取消");
Check(File.ReadAllText(target) == original && !window.OwnedWindows.Any(), "Cancel preserves existing JSON and ends export");
Export(picked, "*.json");
dialog = window.OwnedWindows.Single();
dialog.Close(); Dispatcher.UIThread.RunJobs();
Check(File.ReadAllText(target) == original, "closing confirmation preserves existing JSON");
Export(picked, "*.json");
Click(window.OwnedWindows.Single(), "覆盖");
Check(File.ReadAllText(target).TrimStart('\uFEFF').StartsWith("{") && !File.Exists(picked), "explicit approval writes JSON to normalized target");
using (var exported = System.Text.Json.JsonDocument.Parse(File.ReadAllText(target)))
{
    var exportedHours = exported.RootElement.GetProperty("hours");
    Check(exportedHours.GetArrayLength() == 13 && exportedHours[0].GetProperty("Hour").GetString() == "12:00–13:00"
        && exportedHours[12].GetProperty("Hour").GetString() == "00:00–01:00",
        "exported history retains elapsed hours in descending order");
}
Click(window.OwnedWindows.Single(), "确定");
File.Delete(target);
Export(picked, "*.json");
Check(File.Exists(target) && Buttons(window.OwnedWindows.Single()).Length == 1, "new normalized target needs no overwrite confirmation");
Click(window.OwnedWindows.Single(), "确定");
Export(target, "*.json");
Check(Buttons(window.OwnedWindows.Single()).Length == 1, "unchanged path relies on native overwrite confirmation");
Click(window.OwnedWindows.Single(), "确定");
File.WriteAllText(picked, original);
Export(target, "*.md");
Check(File.ReadAllText(picked) == original && Buttons(window.OwnedWindows.Single()).Length == 2, "JSON-to-Markdown normalization also confirms overwrite");
Click(window.OwnedWindows.Single(), "取消");
window.Close();

object? Field(AppWindow instance, string name) => typeof(AppWindow).GetField(name, flags)!.GetValue(instance);
Task Refresh(AppWindow instance) => (Task)typeof(AppWindow).GetMethod("RefreshAsync", flags)!.Invoke(instance, null)!;
void Complete(Task work)
{
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!work.IsCompleted && DateTime.UtcNow < deadline)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(1);
    }
    if (!work.IsCompleted) throw new Exception("Background work did not finish");
    work.GetAwaiter().GetResult();
    Dispatcher.UIThread.RunJobs();
}
string fixtureHome = Path.Combine(root, "lifecycle");
Directory.CreateDirectory(Path.Combine(fixtureHome, "sessions"));
string fixturePath = Path.Combine(fixtureHome, "sessions", "lifecycle.jsonl");
string fixtureLine = System.Text.Json.JsonSerializer.Serialize(new
{
    type = "token_usage_record", timestamp = DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O"),
    payload = new { turn_id = "lifecycle-turn", thread_id = "lifecycle-thread", response_id = "lifecycle-first",
        usage = new { output_tokens = 120 } }
});
File.WriteAllText(fixturePath, fixtureLine + "\n");
var lifecycle = new AppWindow(capturePath: "test", codexHome: fixtureHome);
Complete(lifecycle.StartAsync());
var lifecycleTimer = (DispatcherTimer)Field(lifecycle, "timer")!;
Check(!lifecycleTimer.IsEnabled && Field(lifecycle, "scanner") == null && Field(lifecycle, "requestClocks") == null
    && Field(lifecycle, "snapshot") == null, "hidden startup stays idle without reading logs or creating scan caches");
Complete(Refresh(lifecycle));
Check(Field(lifecycle, "scanner") == null && Field(lifecycle, "snapshot") == null,
    "a refresh request while hidden does not restart monitoring");
lifecycle.ShowPanel();
Complete((Task)Field(lifecycle, "refreshTask")!);
Check(lifecycleTimer.IsEnabled && ((UsageSnapshot)Field(lifecycle, "snapshot")!).Records.Count == 1,
    "opening the panel scans current logs and resumes the refresh timer");
lifecycle.FindControl<Button>("HistoryButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
var lifecycleChart = lifecycle.FindControl<HourlyChart>("HourChart")!;
var lifecycleDate = lifecycle.FindControl<CalendarDatePicker>("DateBox")!;
var historicalDate = lifecycleDate.SelectedDate!.Value.AddDays(-1);
lifecycleDate.SelectedDate = historicalDate;
lifecycleTimer.Interval = TimeSpan.FromMilliseconds(10);
lifecycle.Hide();
Complete((Task)Field(lifecycle, "refreshTask")!);
Check(!lifecycleTimer.IsEnabled && Field(lifecycle, "scanner") == null && Field(lifecycle, "requestClocks") == null
    && Field(lifecycle, "snapshot") == null && lifecycleChart.Hours.Count == 0 && lifecycleChart.Children.Count == 0,
    "hiding releases the snapshot, scan caches and chart visuals and stops the timer");
File.AppendAllText(fixturePath, fixtureLine.Replace("lifecycle-first", "lifecycle-second") + "\n");
for (int i = 0; i < 30; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
Complete(Refresh(lifecycle));
Check(Field(lifecycle, "snapshot") == null && Field(lifecycle, "scanner") == null,
    "hidden monitoring remains suspended after timer intervals and new log data");
var hiddenMessage = (Task<bool>)typeof(AppWindow).GetMethod("ShowMessage", flags)!
    .Invoke(lifecycle, new object[] { "test", true })!;
Complete(hiddenMessage);
Check(!hiddenMessage.Result && !lifecycle.OwnedWindows.Any(),
    "hidden operations do not create a dialog or approve an overwrite");
lifecycleTimer.Interval = TimeSpan.FromSeconds(30);
lifecycle.ShowPanel();
Complete((Task)Field(lifecycle, "refreshTask")!);
Check(((UsageSnapshot)Field(lifecycle, "snapshot")!).Records.Count == 2 && lifecycleDate.SelectedDate == historicalDate,
    "reopening rebuilds statistics including hidden-period records and preserves the selected date");
var interruptedRefresh = Refresh(lifecycle);
lifecycle.Hide();
Complete(interruptedRefresh);
Complete((Task)Field(lifecycle, "refreshTask")!);
Check(Field(lifecycle, "snapshot") == null && Field(lifecycle, "scanner") == null,
    "a refresh finishing after hide cannot repopulate the released state");
lifecycle.ShowPanel();
Complete((Task)Field(lifecycle, "refreshTask")!);
interruptedRefresh = Refresh(lifecycle);
lifecycle.Hide();
lifecycle.ShowPanel();
Complete((Task)Field(lifecycle, "refreshTask")!);
Check(lifecycleTimer.IsEnabled && ((UsageSnapshot)Field(lifecycle, "snapshot")!).Records.Count == 2,
    "quick hide and reopen resumes a fresh scan after the canceled refresh");
lifecycle.Close();
var neverOpened = new AppWindow(capturePath: "test", codexHome: fixtureHome);
Complete(neverOpened.StartAsync());
neverOpened.Close();
Check((bool)Field(neverOpened, "closing")! && !((DispatcherTimer)Field(neverOpened, "timer")!).IsEnabled,
    "the application can exit before the panel has ever been opened");

object? SessionField(DesktopSession instance, string name) => typeof(DesktopSession).GetField(name, flags)!.GetValue(instance);
AppWindow? Panel(DesktopSession instance) => (AppWindow?)SessionField(instance, "window");
int shutdowns = 0;
var session = new DesktopSession(() => shutdowns++, capturePath: "test", codexHome: fixtureHome);
Check(Panel(session) == null,
    "the tray starts without allocating a statistics window or renderer");
Check(session.HasTray == !OperatingSystem.IsLinux(),
    "the platform tray policy allows Windows and macOS hiding and keeps Linux accessible");
session.ShowPanel();
var firstPanel = Panel(session)!;
Complete((Task)Field(firstPanel, "refreshTask")!);
Check(firstPanel.FindControl<Button>("HideButton")!.IsVisible == session.HasTray,
    "the hide control follows the platform tray policy");
if (!session.HasTray)
{
    firstPanel.FindControl<Button>("HideButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Check(Panel(session) == firstPanel && firstPanel.IsVisible,
        "the Linux fallback cannot be hidden through the unavailable tray control");
}
firstPanel.FindControl<Button>("HistoryButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
firstPanel.FindControl<StackPanel>("SettingsPanel")!.IsVisible = true;
firstPanel.FindControl<CalendarDatePicker>("DateBox")!.SelectedDate = historicalDate;
firstPanel.FindControl<ComboBox>("ModelBox")!.SelectedItem = "未知模型";
session.HidePanel();
Complete((Task)SessionField(session, "reclamationTask")!);
Check(Panel(session) == null && firstPanel.PlatformImpl == null
    && !((DispatcherTimer)Field(firstPanel, "timer")!).IsEnabled
    && Field(firstPanel, "snapshot") == null && shutdowns == 0,
    "returning to the tray destroys the native window and cancels monitoring without exiting");
session.ShowPanel();
var rebuiltPanel = Panel(session)!;
Check(!ReferenceEquals(firstPanel, rebuiltPanel) && rebuiltPanel.IsVisible
    && rebuiltPanel.FindControl<CalendarDatePicker>("DateBox")!.SelectedDate == historicalDate
    && (string?)rebuiltPanel.FindControl<ComboBox>("ModelBox")!.SelectedItem == "未知模型"
    && rebuiltPanel.FindControl<StackPanel>("HistoryPanel")!.IsVisible
    && rebuiltPanel.FindControl<StackPanel>("SettingsPanel")!.IsVisible,
    "opening creates a fresh window and restores the selected date, model and expanded sections");
Complete((Task)Field(rebuiltPanel, "refreshTask")!);
Check(((UsageSnapshot)Field(rebuiltPanel, "snapshot")!).Records.Count == 2,
    "the rebuilt window reads statistics from the existing logs");
var closingScan = Refresh(rebuiltPanel);
session.HidePanel();
session.ShowPanel();
var quickPanel = Panel(session)!;
Complete(closingScan);
Complete((Task)Field(quickPanel, "refreshTask")!);
Complete((Task)SessionField(session, "reclamationTask")!);
Check(rebuiltPanel.PlatformImpl == null && Panel(session) == quickPanel
    && ((UsageSnapshot)Field(quickPanel, "snapshot")!).Records.Count == 2 && shutdowns == 0,
    "a quick reopen keeps the new window active when an old scan is canceled");
session.Exit();
session.Exit();
Check(shutdowns == 1 && Panel(session) == null && quickPanel.PlatformImpl == null,
    "exit disposes the active window and tray exactly once");
var idleSession = new DesktopSession(() => shutdowns++, capturePath: "test", codexHome: fixtureHome);
idleSession.Exit();
Check(shutdowns == 2 && Panel(idleSession) == null,
    "the tray can exit without ever creating a statistics window");

// Run outside this method's stack to verify no application-level callback keeps
// closed controls alive through the tray, completed tasks or IPC opener.
[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
WeakReference CloseDisposablePanel(DesktopSession host)
{
    host.ShowPanel();
    var panel = Panel(host)!;
    Complete((Task)Field(panel, "refreshTask")!);
    var weak = new WeakReference(panel);
    host.HidePanel();
    return weak;
}
var collectibleSession = new DesktopSession(() => shutdowns++, capturePath: "test", codexHome: fixtureHome);
var weakPanel = CloseDisposablePanel(collectibleSession);
Complete((Task)SessionField(collectibleSession, "reclamationTask")!);
Check(!weakPanel.IsAlive && Panel(collectibleSession) == null,
    "the closed statistics window and its controls are collectible while the tray stays alive");
collectibleSession.Exit();
var closeSession = new DesktopSession(() => shutdowns++, capturePath: "test", codexHome: fixtureHome);
closeSession.ShowPanel();
var closingPanel = Panel(closeSession)!;
Complete((Task)Field(closingPanel, "refreshTask")!);
int beforeClose = shutdowns;
closingPanel.Close();
closeSession.ShowPanel();
Check(shutdowns == beforeClose + 1 && Panel(closeSession) == null && closingPanel.PlatformImpl == null,
    "closing the statistics window exits once and cannot reopen a terminated session");
var disposingSession = new DesktopSession(() => shutdowns++, capturePath: "test", codexHome: fixtureHome);
disposingSession.ShowPanel();
var disposingPanel = Panel(disposingSession)!;
Complete((Task)Field(disposingPanel, "refreshTask")!);
int beforeDispose = shutdowns;
disposingSession.Dispose();
disposingSession.Exit();
Check(shutdowns == beforeDispose && Panel(disposingSession) == null && disposingPanel.PlatformImpl == null,
    "framework cleanup disposes the window and tray without requesting another shutdown");
int platformShutdowns = 0;
var platformSession = new DesktopSession(() => platformShutdowns++, codexHome: fixtureHome);
Complete(platformSession.StartAsync());
Check((Panel(platformSession)?.IsVisible == true) == (OperatingSystem.IsMacOS() || !platformSession.HasTray),
    "normal startup stays in the Windows tray and shows the macOS or Linux fallback panel");
if (Panel(platformSession) is { } platformPanel)
{
    Complete((Task)Field(platformPanel, "refreshTask")!);
    // Normal startup exercises the non-capture path; keep its settings write in
    // the fixture instead of touching a developer's per-user macOS/Linux config.
    typeof(AppWindow).GetField("settingsStore", flags)!.SetValue(platformPanel,
        new SettingsStore(Path.Combine(root, "platform-settings.json")));
}
platformSession.Exit();
Check(platformShutdowns == 1, "normal platform startup can exit without leaving a window running");
foreach (string mode in new[] { "--framework-shutdown", "--window-exit" })
{
    using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false,
        ArgumentList = { mode, fixtureHome, Path.Combine(root, "framework-settings.json") }
    }) ?? throw new Exception("Could not start the framework lifecycle test");
    if (!child.WaitForExit(10000))
    {
        child.Kill(true);
        child.WaitForExit();
        throw new Exception("Framework shutdown timed out");
    }
    Check(child.ExitCode == 0, $"{mode} completes without reentering the application exit event");
}
Console.WriteLine($"All {passed} Avalonia regression checks passed without native UI.");

public class TestProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? m, object?[]? a) => Handler(m!, a);
}
public sealed record Cleanup(string Root) : IDisposable { public void Dispose() => Directory.Delete(Root, true); }
