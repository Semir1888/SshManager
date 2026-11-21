using System.IO;
using Avalonia;
using Avalonia.Threading;
using SshManager.App.Services;

namespace SshManager.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogUnhandled(e.ExceptionObject as Exception ?? new Exception("unknown"));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogUnhandled(e.Exception);
            e.SetObserved();
        };
        DragOutStaging.CleanupOld(TimeSpan.FromDays(1));
        try
        {
            BuildAvaloniaApp()
                // 注意：必须等平台初始化完成后再访问 Dispatcher.UIThread。
                // 在 StartWithClassicDesktopLifetime 之前访问会让 Dispatcher 以
                // NullDispatcherImpl 永久缓存，导致 MainLoop 抛 PlatformNotSupportedException。
                .AfterSetup(_ => RegisterUiThreadExceptionHandler())
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // 启动阶段（平台 / App 初始化）的异常走不到 UI 通道，这里兜底记录
            LogUnhandled(ex);
            throw;
        }
    }

    // UI 线程未处理异常：记录到 error.log 并阻止闪退（配合各 ViewModel 的
    // try/catch 兜底，任何漏网异常都不再让进程直接崩溃）
    private static void RegisterUiThreadExceptionHandler()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            LogUnhandled(e.Exception);
            e.Handled = true;
        };
    }

    // Avalonia 设计器需要此方法，请勿删除/重命名
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void LogUnhandled(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SshManager");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch { }
    }
}
