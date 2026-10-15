using System.Windows;

namespace Interstellar.Loader;

public partial class App : Application
{
    private SingleInstanceCoordinator? singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        singleInstance = SingleInstanceCoordinator.AcquireOrActivate(() =>
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(ActivateExistingWindow));
        });
        if (singleInstance is null)
        {
            Shutdown(0);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.InitializeProduction();
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow is Interstellar.Loader.MainWindow loader) loader.DisposeLifetime();
        singleInstance?.Dispose();
        singleInstance = null;
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (!e.Cancel && MainWindow is Interstellar.Loader.MainWindow loader) loader.PrepareForSystemShutdown();
    }

    private void ActivateExistingWindow()
    {
        if (Dispatcher.HasShutdownStarted || MainWindow is not { } window) return;
        if (window is Interstellar.Loader.MainWindow loader)
        {
            loader.RestoreFromTray();
            return;
        }
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        if (!window.IsVisible) window.Show();
        if (window.ShowActivated) window.Activate();
    }
}
