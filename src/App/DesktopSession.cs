using System;
using System.Runtime;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;

namespace CodexTpsFloat.App;

// The tray outlives each statistics window. Keeping a hidden Window would also
// retain its native handle, composition target and control tree.
public sealed class DesktopSession
{
    readonly Action shutdown;
    readonly string? capturePath, codexHome;
    readonly TrayIcon? tray;
    MainWindow? window;
    PanelViewState? viewState;
    Task reclamationTask = Task.CompletedTask;
    int viewVersion;
    bool exiting;

    public DesktopSession(Action shutdown, string? capturePath = null, string? codexHome = null)
    {
        this.shutdown = shutdown;
        this.capturePath = capturePath;
        this.codexHome = codexHome;
        tray = OperatingSystem.IsBrowser() ? null : CreateTrayIcon();
        if (tray != null && Application.Current != null)
            TrayIcon.SetIcons(Application.Current, new TrayIcons { tray });
    }

    // Linux may create a tray object without a status-notifier host. macOS also
    // opens the panel at startup because its menu bar item can be suppressed.
    public bool HasTray => tray != null && !OperatingSystem.IsLinux();

    public async Task StartAsync()
    {
        SetToolTip("Codex TPS · 已暂停");
        if (capturePath != null)
        {
            ShowPanel();
            await window!.CaptureAndCloseAsync(capturePath);
        }
        else if (OperatingSystem.IsMacOS() || !HasTray)
            ShowPanel();
    }

    public void ShowPanel()
    {
        if (exiting) return;
        if (window == null)
        {
            viewVersion++;
            window = new MainWindow(capturePath, codexHome, this, viewState);
            window.Closed += PanelClosed;
            // Start while hidden: initialization is synchronous and performs no scan.
            _ = window.StartAsync();
        }
        window.ShowPanel();
    }

    public void HidePanel()
    {
        if (window == null) return;
        var panel = window;
        var compositor = ElementComposition.GetElementVisual(panel)?.Compositor;
        viewState = panel.RememberView();
        window = null;
        panel.Close(); // Dispose the native window and renderer, not just visibility.
        SetToolTip("Codex TPS · 已暂停");
        reclamationTask = ReclaimAsync(panel.PendingRefresh, compositor, ++viewVersion);
    }

    async Task ReclaimAsync(Task pendingRefresh, Compositor? compositor, int version)
    {
        await pendingRefresh;
        pendingRefresh = Task.CompletedTask; // Do not root the closed window through its scan task.
        // Detaching the control tree queues composition changes after the native
        // target is disposed. Drain those references before collecting the window.
        if (compositor != null) await compositor.RequestCommitAsync();
        await Task.Yield(); // Let Close and the initiating click handler unwind.
        if (window != null || exiting || version != viewVersion) return;
        await Task.Run(static () =>
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            // Finalizable rendering resources can keep the last control tree alive
            // through the first collection. Finish them before compacting the heap.
            GC.WaitForPendingFinalizers();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            // Aggressive also decommits unused heap pages; Forced may retain them
            // for a future scan even after the objects themselves were collected.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        });
    }

    void PanelClosed(object? sender, EventArgs e)
    {
        if (sender is not MainWindow panel) return;
        panel.Closed -= PanelClosed;
        // HidePanel detaches it first; a normal close still exits the application.
        if (ReferenceEquals(window, panel)) Exit();
    }

    public void Exit()
    {
        if (exiting) return;
        exiting = true;
        var panel = window;
        window = null;
        panel?.Close();
        tray?.Dispose();
        if (Application.Current != null)
            TrayIcon.SetIcons(Application.Current, new TrayIcons());
        shutdown();
    }

    internal void SetToolTip(string text)
    {
        if (tray != null) tray.ToolTipText = text;
    }

    TrayIcon? CreateTrayIcon()
    {
        try
        {
            var menu = new NativeMenu();
            var open = new NativeMenuItem("打开统计面板");
            open.Click += (_, _) => ShowPanel();
            var refresh = new NativeMenuItem("立即刷新");
            refresh.Click += async (_, _) =>
            {
                if (window == null) ShowPanel();
                else await window.RefreshNowAsync();
            };
            var exit = new NativeMenuItem("退出");
            exit.Click += (_, _) => Exit();
            menu.Items.Add(open);
            menu.Items.Add(refresh);
            menu.Items.Add(exit);
            var icon = new TrayIcon
            {
                Icon = TrayIconFactory.Create(),
                ToolTipText = "Codex TPS · 已暂停",
                Menu = menu,
                IsVisible = true
            };
            MacOSProperties.SetIsTemplateIcon(icon, OperatingSystem.IsMacOS());
            icon.Clicked += (_, _) =>
            {
                if (window != null && HasTray) HidePanel();
                else ShowPanel();
            };
            return icon;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
