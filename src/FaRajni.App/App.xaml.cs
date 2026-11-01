using System.Windows;
using System.Windows.Threading;
using FaRajni.App.Services;

namespace FaRajni.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => AppPaths.LogError("AppDomain", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { AppPaths.LogError("Task", a.Exception); a.SetObserved(); };

        if (e.Args.Contains("--build-vlc-cache")) { VlcEngine.BuildPluginCache(); Shutdown(); return; }

        var launchFile = e.Args.FirstOrDefault(File.Exists);
        if (!SingleInstance.Claim() && SingleInstance.Forward(launchFile)) { Shutdown(); return; }
        if (launchFile is not null) VlcEngine.Start();   // opening straight into a video: don't wait for the window

        var window = new MainWindow(launchFile);
        MainWindow = window;
        window.Show();
        SingleInstance.Listen(file => window.Dispatcher.BeginInvoke(() => window.OpenForwarded(file)));

        // Warm the video engine only after the first frame is on screen, so it never delays launch.
        window.ContentRendered += (_, _) => VlcEngine.Start();
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppPaths.LogError("Dispatcher", e.Exception);
        e.Handled = true;
        (MainWindow as MainWindow)?.ShowToast("Something went wrong: " + e.Exception.Message, "\uE7BA");
    }
}
