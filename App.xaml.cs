using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Mio.Player;
using static Mio.Diagnostics.MioLog;

namespace Mio;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log("app starting");
        var mainWindow = new MainWindow();
        _window = mainWindow;
        _window.Activate();

        var launchSource = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(argument => MediaSource.IsRemote(argument) || File.Exists(argument));
        if (!string.IsNullOrWhiteSpace(launchSource))
        {
            mainWindow.OpenFileWhenReady(launchSource);
        }
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Message, e.Exception);

        // 有意不置 e.Handled：走到这里说明出现了未预期的异常，进程状态未必还能
        // 收拾干净，继续跑只会掩盖问题。留下日志再崩，比带病运行更容易定位。
    }

    private static void WriteCrashLog(string message, Exception? exception)
    {
        try
        {
            // 不能写 AppContext.BaseDirectory：装在 Program Files 下没有写权限，
            // 日志会被 catch 静默吞掉，等于没有。
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Mio");
            Directory.CreateDirectory(directory);

            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] unhandled exception: {message}{Environment.NewLine}{exception}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(directory, "crash.log"), line);
        }
        catch (Exception writeFailure)
        {
            // 崩溃处理里再抛异常只会盖掉原始错误，这里只能尽力而为。
            Log($"failed to write crash log: {writeFailure.Message}");
        }
    }
}
