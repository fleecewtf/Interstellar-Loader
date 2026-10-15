using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Interstellar.Loader;

public partial class MainWindow
{
    private LoaderPreferences? lifetimePreferences;
    private Forms.NotifyIcon? trayIcon;
    private Forms.ContextMenuStrip? trayMenu;
    private Drawing.Icon? trayDrawingIcon;

    private bool trayHiding;
    private bool hiddenToTray;
    private bool fullExitRequested;
    private bool exitWaitingForOperation;

    internal void InitializeLifetime(LoaderPreferences savedPreferences)
    {
        lifetimePreferences = savedPreferences ?? throw new ArgumentNullException(nameof(savedPreferences));
    }

    internal bool HandleCloseToTray(CancelEventArgs e)
    {
        if (e.Cancel || fullExitRequested || closeCommitted || windowClosed ||
            lifetimePreferences?.CloseToTray != true) return false;
        if (!EnsureTrayIcon()) return false;

        e.Cancel = true;
        if (trayHiding || hiddenToTray) return true;
        trayHiding = true;
        closingRequested = true;
        AppFrame.IsHitTestVisible = false;
        SetTrayVisible(true);
        if (!MotionEnabled || WindowState == WindowState.Minimized)
        {
            CompleteTrayHide();
            return true;
        }

        var opacity = AppFrame.Opacity;
        var transform = AppFrame.RenderTransform as TransformGroup;
        var scale = transform?.Children.OfType<ScaleTransform>().FirstOrDefault()?.ScaleX ?? 1;
        var offset = transform?.Children.OfType<TranslateTransform>().FirstOrDefault()?.Y ?? 0;
        Transition(AppFrame, opacity, 0, offset, 4, 180, scale, 0.98,
            completed: CompleteTrayHide, easingMode: EasingMode.EaseIn);
        return true;
    }

    private void CompleteTrayHide()
    {
        if (!trayHiding || (fullExitRequested && !exitWaitingForOperation) || windowClosed) return;
        Hide();
        hiddenToTray = true;
        trayHiding = false;
        closingRequested = false;
        AppFrame.IsHitTestVisible = true;
        PrepareWindowEntrance();
    }

    internal void RestoreFromTray()
    {
        if (windowClosed || closeCommitted || (fullExitRequested && !exitWaitingForOperation) || (closingRequested && !trayHiding)) return;
        var reverseHide = trayHiding;
        var opacity = AppFrame.Opacity;
        var transform = AppFrame.RenderTransform as TransformGroup;
        var scale = transform?.Children.OfType<ScaleTransform>().FirstOrDefault()?.ScaleX ?? 1;
        var offset = transform?.Children.OfType<TranslateTransform>().FirstOrDefault()?.Y ?? 0;
        trayHiding = false;
        closingRequested = false;
        AppFrame.IsHitTestVisible = true;
        SetTrayVisible(false);
        var restoreHidden = hiddenToTray || !IsVisible;
        hiddenToTray = false;
        if (restoreHidden) PrepareWindowEntrance();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible) Show();
        if (restoreHidden) BeginWindowEntrance();
        else if (reverseHide) Transition(AppFrame, opacity, 1, offset, 0, 180, scale, 1);
        if (ShowActivated) Activate();
    }

    internal void RequestFullExit()
    {
        if (windowClosed || closeCommitted) return;
        fullExitRequested = true;
        if (WaitForOperationBeforeExit()) return;
        FinishFullExit();
    }

    private bool DeferCloseUntilOperationSettles(CancelEventArgs e)
    {
        if (closeCommitted || windowClosed || activeOperationCompletion is null || activeOperationCompletion.IsCompleted) return false;
        e.Cancel = true;
        fullExitRequested = true;
        WaitForOperationBeforeExit();
        return true;
    }

    private bool WaitForOperationBeforeExit()
    {
        if (exitWaitingForOperation) return true;
        var completion = activeOperationCompletion;
        if (completion is null || completion.IsCompleted) return false;
        exitWaitingForOperation = true;
        BodyContent.IsEnabled = false;
        ActivityText.Text = "Finishing the current operation before closing…";
        ManageFeedback.Text = ActivityText.Text;
        operationsLifetime.Cancel();
        _ = FinishAfterOperationAsync(completion);
        return true;
    }

    private async Task FinishAfterOperationAsync(Task completion)
    {
        await completion;
        exitWaitingForOperation = false;
        if (!windowClosed && !closeCommitted) FinishFullExit();
    }

    private void FinishFullExit()
    {
        trayHiding = false;
        closingRequested = false;
        SetTrayVisible(false);
        if (!IsVisible || WindowState == WindowState.Minimized) closeCommitted = true;
        Close();
    }

    internal void PrepareForSystemShutdown()
    {
        fullExitRequested = true;
        exitWaitingForOperation = false;
        trayHiding = false;
        closeCommitted = true;
        operationsLifetime.Cancel();
        DisposeLifetime();
    }

    internal void DisposeLifetime()
    {
        SetTrayVisible(false);
        if (trayIcon is not null)
        {
            trayIcon.Dispose();
            trayIcon = null;
        }
        trayMenu?.Dispose();
        trayMenu = null;
        trayDrawingIcon?.Dispose();
        trayDrawingIcon = null;
    }

    private bool EnsureTrayIcon()
    {
        if (trayIcon is not null) return true;
        try
        {
            var resource = Application.GetResourceStream(new Uri("Assets/app-icon.ico", UriKind.Relative));
            if (resource is null) throw new InvalidOperationException("The loader tray icon is unavailable.");
            using (resource.Stream)
            using (var icon = new Drawing.Icon(resource.Stream)) trayDrawingIcon = (Drawing.Icon)icon.Clone();
            trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Items.Add("Open Interstellar Loader", null, (_, _) => DispatchTrayAction(RestoreFromTray));
            trayMenu.Items.Add(new Forms.ToolStripSeparator());
            trayMenu.Items.Add("Exit", null, (_, _) => DispatchTrayAction(RequestFullExit));
            trayIcon = new Forms.NotifyIcon
            {
                Text = "Interstellar Loader",
                Icon = trayDrawingIcon,
                ContextMenuStrip = trayMenu,
                Visible = false
            };
            trayIcon.DoubleClick += (_, _) => DispatchTrayAction(RestoreFromTray);

            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or Win32Exception or System.Runtime.InteropServices.ExternalException)
        {
            DisposeLifetime();

            return false;
        }
    }

    private void SetTrayVisible(bool visible)
    {
        if (trayIcon is not null) trayIcon.Visible = visible;
    }

    private void DispatchTrayAction(Action action)
    {
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            Dispatcher.BeginInvoke(action);
    }
}
