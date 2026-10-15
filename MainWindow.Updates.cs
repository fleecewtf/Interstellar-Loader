using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Interstellar.Loader;

public partial class MainWindow
{
    private DispatcherTimer? updateTimer;
    private bool updateChecking;
    private bool updateProgressVisible;

    private void InitializeReleaseLists()
    {
        MenuReleasesTab.IsChecked = true;
        ReleaseList.ItemsSource = ReleaseCatalog.GetEntries(ReleaseProduct.Menu);
    }

    private void ReleaseProduct_Checked(object sender, RoutedEventArgs e)
    {
        if (!initialized || ReleaseList == null || sender is not RadioButton button) return;
        var product = button.Tag as string == "Loader" ? ReleaseProduct.Loader : ReleaseProduct.Menu;
        ReleaseList.ItemsSource = ReleaseCatalog.GetEntries(product);
        ReleaseScroll.ScrollToTop();
        if (ReleasesPage.Visibility == Visibility.Visible)
            Transition(ReleaseList, 0, 1, 6, 0, 160);
    }

    private void StartUpdatePolling()
    {
        updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(UpdateService.PollIntervalSeconds) };
        updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync();
        updateTimer.Start();
    }

    private async Task CheckForUpdatesAsync(bool manual = false, bool opening = false)
    {
        if ((!productionMode && !manual) || updateChecking || operationRunning || fullExitRequested || windowClosed || closingRequested) return;
        if (ModalOverlay.Visibility == Visibility.Visible) return;
        updateChecking = true;
        CheckUpdatesButton.IsEnabled = false;
        bool showProgress = UpdateService.IsSourceConfigured && (manual || opening && preferences.AutoUpdateLoader);
        if (showProgress) ShowUpdateProgress("Checking for updates", "Looking for new Interstellar releases.", "Checking releases");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operationsLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var result = await UpdateService.CheckAsync(timeout.Token);
            if (windowClosed || fullExitRequested) return;
            ReleaseUpdateStatus.Text = result.StatusText;
            SettingsUpdateStatus.Text = result.StatusText;
            if (result.SourceConfigured && result.LoaderUpdateAvailable && !preferences.AutoUpdateLoader)
                ReleaseUpdateStatus.Text = "A loader update is available. Auto-update is off.";
            else if (result.SourceConfigured && result.MenuUpdateAvailable && !preferences.AutoUpdateMenu)
                ReleaseUpdateStatus.Text = "A menu update is available. Auto-update is off.";
        }
        catch (OperationCanceledException)
        {
            if (!windowClosed && !fullExitRequested && !operationsLifetime.IsCancellationRequested)
                ReleaseUpdateStatus.Text = SettingsUpdateStatus.Text = "Update check timed out. The next check will retry.";
        }
        catch (Exception error)
        {
            if (!windowClosed && !fullExitRequested)
            {
                ReleaseUpdateStatus.Text = SettingsUpdateStatus.Text = "Could not check for updates. The next check will retry.";
                GameService.LogError("Check for updates", error);
            }
        }
        finally
        {
            updateChecking = false;
            if (!windowClosed)
            {
                CheckUpdatesButton.IsEnabled = true;
                if (showProgress) HideUpdateProgress();
            }
        }
    }

    private void ShowUpdateProgress(string title, string description, string phase, double? percent = null)
    {
        updateProgressVisible = true;
        UpdateTitle.Text = title;
        UpdateDescription.Text = description;
        UpdateProgressLabel.Text = phase;
        UpdateProgress.IsIndeterminate = !percent.HasValue;
        if (percent.HasValue) UpdateProgress.Value = Math.Max(0, Math.Min(100, percent.Value));
        BodyContent.IsEnabled = false;
        TitleBar.IsEnabled = false;
        UpdateOverlay.Visibility = Visibility.Visible;
        Transition(UpdateOverlay, 0, 1, 0, 0, 160);
        Transition(UpdateCard, 0, 1, 8, 0, 200, 0.98, 1);
    }

    private void HideUpdateProgress()
    {
        updateProgressVisible = false;
        Transition(UpdateOverlay, UpdateOverlay.Opacity, 0, 0, 0, 130, completed: () =>
        {
            if (updateProgressVisible || windowClosed) return;
            StopMotion(UpdateOverlay);
            StopMotion(UpdateCard);
            UpdateOverlay.Visibility = Visibility.Collapsed;
            bool enabled = ModalOverlay.Visibility != Visibility.Visible && !fullExitRequested;
            BodyContent.IsEnabled = enabled;
            TitleBar.IsEnabled = enabled;
        });
    }
}
