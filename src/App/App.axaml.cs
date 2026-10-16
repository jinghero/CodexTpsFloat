using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace CodexTpsFloat.App;

public partial class App : Application
{
    public static CommandLineOptions Options { get; set; } = new(null, null, null);
    public static SingleInstance? SingleInstance { get; set; }
    static readonly TaskCompletionSource<Action> panelReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The IPC worker must not consume a request until startup has finished.
    public static void ShowExistingPanel()
    {
        if (!panelReady.Task.Wait(TimeSpan.FromSeconds(8)))
            throw new TimeoutException("统计面板尚未完成启动，请稍后重试。");
        var show = Dispatcher.UIThread.InvokeAsync(panelReady.Task.Result).GetTask();
        if (!show.Wait(TimeSpan.FromSeconds(2)))
            throw new TimeoutException("统计面板未响应显示请求，请稍后重试。");
    }
    DesktopSession? session;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        session = new DesktopSession(() => desktop.Shutdown(), Options.CapturePath, Options.CodexHome);

        desktop.Startup += async (_, _) =>
        {
            await session.StartAsync();
            if (Options.CapturePath == null)
                panelReady.TrySetResult(session.ShowPanel);
        };
        desktop.Exit += (_, _) => session.Exit();

        base.OnFrameworkInitializationCompleted();
    }
}
