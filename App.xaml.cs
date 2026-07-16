using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;

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
        Debug.WriteLine("[Mio.WinUI] app starting");
        var mainWindow = new MainWindow();
        _window = mainWindow;
        _window.Activate();

        var launchFile = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(launchFile))
        {
            mainWindow.OpenFileWhenReady(launchFile);
        }
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "mio-startup-error.log");
            File.AppendAllText(logPath, $"[Mio.WinUI] unhandled exception: {e.Message}{Environment.NewLine}{e.Exception}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
