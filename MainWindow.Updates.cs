using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Interstellar.Loader;

public partial class MainWindow
{
    private DispatcherTimer? updateTimer;
    private bool updateChecking;
    private bool updateProgressVisible;
    private UpdateCheckResult? latestReleaseCheck;
    private string releaseListIdentity = "";
    private static readonly Lazy<string> CurrentLoaderHash = new(() =>
    {
        using var input = File.OpenRead(Assembly.GetExecutingAssembly().Location);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
    });

    private void InitializeReleaseLists()
    {
        MenuReleasesTab.IsChecked = true;
        RefreshReleaseEntries();
    }

    private void ReleaseProduct_Checked(object sender, RoutedEventArgs e)
    {
        if (!initialized || ReleaseList == null || sender is not RadioButton) return;
        RefreshReleaseEntries();
        ReleaseScroll.ScrollToTop();
        if (ReleasesPage.Visibility == Visibility.Visible)
            Transition(ReleaseList, 0, 1, 6, 0, 160);
    }

    private void RefreshReleaseEntries()
    {
        if (ReleaseList == null) return;
        var product = LoaderReleasesTab?.IsChecked == true ? ReleaseProduct.Loader : ReleaseProduct.Menu;
        var release = product == ReleaseProduct.Loader ? latestReleaseCheck?.LatestLoaderRelease : latestReleaseCheck?.LatestMenuRelease;
        string identity = product + ":" + release?.Version + ":" + release?.Sha256;
        if (identity == releaseListIdentity) return;
        releaseListIdentity = identity;
        ReleaseList.ItemsSource = ReleaseCatalog.GetEntries(product, release);
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
        bool showChecking = manual || opening && preferences.AutoUpdateLoader;
        if (showChecking) ShowUpdateProgress("Checking for updates", "Looking for new Interstellar releases on GitHub.", "Checking releases");
        bool restart = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operationsLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            string? installedVersion = string.IsNullOrEmpty(gameStatus?.InstalledFileVersion) ? null : gameStatus!.InstalledFileVersion;
            var result = await UpdateService.CheckAsync(timeout.Token, installedVersion);
            if (windowClosed || fullExitRequested || closingRequested) return;
            latestReleaseCheck = result;
            RefreshReleaseEntries();
            SetUpdateStatus(result.StatusText);
            if (operationRunning || ModalOverlay.Visibility == Visibility.Visible) return;
            string requestedDirectory = preferences.GameDirectory;
            var status = await GameService.ReadStatusAsync(requestedDirectory, operationsLifetime.Token);
            string loaderHash = await Task.Run(() => CurrentLoaderHash.Value, operationsLifetime.Token);
            if (windowClosed || fullExitRequested || closingRequested || operationRunning ||
                requestedDirectory != preferences.GameDirectory || ModalOverlay.Visibility == Visibility.Visible) return;

            bool menuNeeded = status.MenuInstalled && UpdatePolicy.NeedsUpdate(result.LatestMenuRelease,
                status.InstalledFileVersion, status.InstalledSha256);
            bool loaderNeeded = UpdatePolicy.NeedsUpdate(result.LatestLoaderRelease, UpdateService.CurrentLoaderVersion, loaderHash);
            if (manual && loaderNeeded && preferences.AutoUpdateLoader)
                LoaderSelfUpdate.ClearFailure(result.LatestLoaderRelease!.Sha256);
            bool loaderBlocked = loaderNeeded && LoaderSelfUpdate.IsBlocked(result.LatestLoaderRelease!.Sha256);
            var messages = new List<string> { result.StatusText };
            if (menuNeeded)
            {
                if (!preferences.AutoUpdateMenu) messages.Add("A menu update is available. Auto-update menu is off.");
                else if (!status.MenuManaged) messages.Add("Install Interstellar through this loader to enable automatic menu updates.");
                else if (!status.BepInExInstalled) messages.Add("Install or repair BepInEx before updating the menu.");
                else if (status.GameRunning) messages.Add("Menu v" + result.LatestMenuRelease!.Version + " will install after Gorilla Tag closes.");
            }
            else if (!status.MenuInstalled && result.LatestMenuRelease != null)
                messages.Add("Menu v" + result.LatestMenuRelease.Version + " is available to install from the launcher.");
            if (loaderNeeded && !preferences.AutoUpdateLoader) messages.Add("A loader update is available. Auto-update loader is off.");
            else if (loaderBlocked) messages.Add("The last loader update could not start. Select Check for updates to retry, or wait for a corrected release.");
            SetUpdateStatus(string.Join(" ", messages));

            bool applyMenu = menuNeeded && preferences.AutoUpdateMenu && status.MenuManaged && status.BepInExInstalled && !status.GameRunning;
            bool applyLoader = loaderNeeded && preferences.AutoUpdateLoader && !loaderBlocked;
            if (!applyMenu && !applyLoader) return;
            restart = await ApplyUpdatesAsync(requestedDirectory, result, applyMenu, applyLoader, messages);
        }
        catch (OperationCanceledException)
        {
            if (!windowClosed && !fullExitRequested && !operationsLifetime.IsCancellationRequested)
                SetUpdateStatus("Update check timed out. The next check will retry.");
        }
        catch (Exception error)
        {
            if (!windowClosed && !fullExitRequested)
            {
                SetUpdateStatus("Could not finish the update check. The next check will retry.");
                GameService.LogError("Check for updates", error);
            }
        }
        finally
        {
            updateChecking = false;
            if (!windowClosed)
            {
                CheckUpdatesButton.IsEnabled = !fullExitRequested && !operationRunning;
                if (!restart && updateProgressVisible) HideUpdateProgress();
            }
        }
        if (restart && !windowClosed) RequestFullExit();
    }

    private async Task<bool> ApplyUpdatesAsync(string directory, UpdateCheckResult result, bool applyMenu,
        bool applyLoader, List<string> messages)
    {
        var settlement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        activeOperationCompletion = settlement.Task;
        operationRunning = true;
        bool restart = false;
        try
        {
            if (applyMenu && preferences.AutoUpdateMenu)
            {
                ShowUpdateProgress("Updating Interstellar Menu", "The menu will be ready the next time you launch Gorilla Tag.", "Downloading menu", 0);
                try
                {
                    await ReleaseService.InstallReleaseAsync(directory, result.LatestMenuRelease!,
                        CreateUpdateProgress("Downloading menu"), operationsLifetime.Token);
                    messages.Add("Interstellar Menu v" + result.LatestMenuRelease!.Version + " installed.");
                    ActivityText.Text = "Interstellar Menu updated.";
                }
                catch (OperationCanceledException) when (operationsLifetime.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    GameService.LogError("Update menu", error);
                    messages.Add("The menu update could not be installed. " + error.Message);
                }
            }
            operationsLifetime.Token.ThrowIfCancellationRequested();
            if (applyLoader && preferences.AutoUpdateLoader && !fullExitRequested && directory == preferences.GameDirectory)
            {
                try
                {
                    if (!IsVisible || hiddenToTray || WindowState == WindowState.Minimized) RestoreFromTray();
                    ShowUpdateProgress("Updating Interstellar Loader", "The loader will reopen when the update is ready.", "Downloading loader", 0);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operationsLifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromMinutes(3));
                    using var staged = await UpdateService.StageAsync(result.LatestLoaderRelease!, CreateUpdateProgress("Downloading loader"), timeout.Token);
                    UpdateProgressLabel.Text = "Preparing restart";
                    UpdateProgress.IsIndeterminate = true;
                    using var prepared = await LoaderSelfUpdate.PrepareAsync(staged.FilePath, staged.Candidate, timeout.Token);
                    await Task.Delay(1200, timeout.Token);
                    timeout.Token.ThrowIfCancellationRequested();
                    UpdateDescription.Text = "Restarting with Interstellar Loader v" + staged.Candidate.Version + ".";
                    UpdateProgressLabel.Text = "Restarting loader";
                    await prepared.LaunchAsync(timeout.Token);
                    restart = true;
                }
                catch (OperationCanceledException) when (operationsLifetime.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    GameService.LogError("Update loader", error);
                    messages.Add("The loader update could not be completed. Your current loader is still available. " + error.Message);
                }
            }
            SetUpdateStatus(string.Join(" ", messages));
            return restart;
        }
        finally
        {
            try
            {
                if (!restart && !windowClosed && !fullExitRequested) await RefreshStatusAsync();
                operationRunning = false;
                if (!windowClosed && !restart) RenderGameStatus();
            }
            finally
            {
                operationRunning = false;
                if (ReferenceEquals(activeOperationCompletion, settlement.Task)) activeOperationCompletion = null;
                settlement.TrySetResult(true);
            }
        }
    }

    private IProgress<double> CreateUpdateProgress(string phase) => new Progress<double>(value =>
    {
        if (windowClosed || !updateProgressVisible) return;
        double percent = Math.Max(0, Math.Min(100, value));
        UpdateProgress.IsIndeterminate = false;
        UpdateProgress.Value = percent;
        UpdateProgressLabel.Text = phase + " · " + percent.ToString("0") + "%";
    });

    private void SetUpdateStatus(string message)
    {
        if (windowClosed) return;
        ReleaseUpdateStatus.Text = message;
        SettingsUpdateStatus.Text = message;
    }

    internal void ShowStartupUpdateMessage(string message)
    {
        SetUpdateStatus(message);
        ActivityText.Text = message;
    }

    private void ShowUpdateProgress(string title, string description, string phase, double? percent = null)
    {
        bool alreadyVisible = updateProgressVisible;
        updateProgressVisible = true;
        UpdateTitle.Text = title;
        UpdateDescription.Text = description;
        UpdateProgressLabel.Text = phase;
        UpdateProgress.IsIndeterminate = !percent.HasValue;
        if (percent.HasValue) UpdateProgress.Value = Math.Max(0, Math.Min(100, percent.Value));
        BodyContent.IsEnabled = false;
        TitleBar.IsEnabled = false;
        UpdateOverlay.Visibility = Visibility.Visible;
        if (!alreadyVisible)
        {
            Transition(UpdateOverlay, 0, 1, 0, 0, 160);
            Transition(UpdateCard, 0, 1, 8, 0, 200, 0.98, 1);
        }
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
