using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Interstellar.Loader;

public partial class MainWindow
{
    private LoaderPreferences preferences = new();
    private bool productionMode;
    private bool applyingPreferences;
    private bool operationRunning;
    private Task? activeOperationCompletion;
    private bool statusRefreshing;
    private GameStatus? gameStatus;
    private readonly CancellationTokenSource operationsLifetime = new();
    private DispatcherTimer? statusTimer;
    private PendingOperation? modalOperation;

    private sealed class PendingOperation
    {
        internal string Label = "";
        internal Func<Task> Action = () => Task.CompletedTask;
    }

    internal void InitializeProduction()
    {
        if (productionMode) return;
        productionMode = true;
        preferences = LoaderSettingsStore.Load();
        InitializeLifetime(preferences);
        applyingPreferences = true;
        CloseToTrayToggle.IsChecked = preferences.CloseToTray;
        LaunchWithMenuToggle.IsChecked = preferences.LaunchWithMenu;
        AutoUpdateMenuToggle.IsChecked = preferences.AutoUpdateMenu;
        AutoUpdateLoaderToggle.IsChecked = preferences.AutoUpdateLoader;
        applyingPreferences = false;
        SettingsFeedback.Text = LoaderSettingsStore.LastError ?? "";
        GameStatusText.Text = "Checking";
        GamePathLabel.Text = string.IsNullOrWhiteSpace(preferences.GameDirectory) ? "Detecting game directory" : preferences.GameDirectory;
        SettingsGamePath.Text = GamePathLabel.Text;
        LoadButtonLabel.Text = "Checking installation";
        LoadButton.IsEnabled = false;
        Loaded += async (_, _) =>
        {
            await RefreshStatusAsync();
            await CheckForUpdatesAsync(opening: true);
        };
        Closed += (_, _) =>
        {
            statusTimer?.Stop();
            updateTimer?.Stop();
            operationsLifetime.Cancel();
            DisposeLifetime();
        };
        statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        statusTimer.Tick += async (_, _) =>
        {
            if (IsVisible && !operationRunning) await RefreshStatusAsync();
        };
        statusTimer.Start();
        StartUpdatePolling();
    }

    private async Task RefreshStatusAsync()
    {
        if (!productionMode || statusRefreshing || windowClosed) return;
        statusRefreshing = true;
        var requestedDirectory = preferences.GameDirectory;
        try
        {
            var status = await GameService.ReadStatusAsync(requestedDirectory, operationsLifetime.Token);
            if (windowClosed || requestedDirectory != preferences.GameDirectory) return;
            gameStatus = status;
            if (status.GameInstalled && string.IsNullOrWhiteSpace(preferences.GameDirectory))
            {
                preferences.GameDirectory = status.GameDirectory;
                SavePreferences();
            }
            RenderGameStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            gameStatus = null;
            GameService.LogError("Read game status", error);
            GameStatusText.Text = "Unavailable";
            ActivityText.Text = error.Message;
            LoadButtonLabel.Text = "Retry";
            LoadButton.IsEnabled = !operationRunning;
        }
        finally { statusRefreshing = false; }
    }

    private void RenderGameStatus()
    {
        if (gameStatus == null) return;
        var status = gameStatus;
        GameStatusText.Text = status.GameRunning ? "Running" : status.GameInstalled ? "Not running" : "Not installed";
        GamePathLabel.Text = string.IsNullOrWhiteSpace(status.GameDirectory) ? "Select game directory" : status.GameDirectory;
        GamePathLabel.ToolTip = status.GameDirectory;
        SettingsGamePath.Text = GamePathLabel.Text;
        SettingsGamePath.ToolTip = status.GameDirectory;
        AboutLoaderVersion.Text = UpdateService.CurrentLoaderVersion;
        AboutMenuVersion.Text = status.MenuInstalled && !string.IsNullOrEmpty(status.InstalledFileVersion)
            ? status.InstalledFileVersion : latestReleaseCheck?.LatestMenuRelease?.Version ?? ReleaseService.CurrentMenuVersion;
        VersionLabel.Text = "v" + AboutMenuVersion.Text;
        MenuStatusText.Text = status.MenuLoaded ? "Interstellar loaded" : status.MenuInstalled ? "Interstellar installed" : "Ready to install · v" + AboutMenuVersion.Text;
        ManageMenuStatus.Text = status.MenuLoaded ? "Loaded · v" + status.InstalledVersion : status.MenuInstalled ? (status.MenuManaged ? "Installed · v" : "Manual install · v") + status.InstalledVersion : "Ready to install · v" + AboutMenuVersion.Text;
        BepInExStatusText.Text = status.BepInExInstalled ? "Installed" : "Not installed";
        ResetMenuButton.IsEnabled = !operationRunning && status.MenuManaged && status.HasResettableMenuSettings;
        LoadButtonLabel.Text = operationRunning ? "Working" : !status.GameInstalled ? "Install Gorilla Tag" : status.MenuLoaded ? "Interstellar loaded" : status.GameRunning ? status.BridgeAvailable ? "Load Interstellar" : "Game is running" : !status.BepInExInstalled ? "Set up BepInEx" : !status.MenuInstalled ? "Install Interstellar" : preferences.LaunchWithMenu ? "Launch with Interstellar" : "Launch Gorilla Tag";
        LoadButton.IsEnabled = !operationRunning && !status.MenuLoaded && (!status.GameRunning || status.BridgeAvailable);
        if (!operationRunning && !string.IsNullOrEmpty(status.Error)) ActivityText.Text = status.Error;
        else if (!operationRunning && status.GameRunning && !status.BridgeAvailable)
            ActivityText.Text = "Loading during a session requires the Interstellar component to be installed before the game starts.";
    }

    private async Task PerformPrimaryActionAsync()
    {
        if (gameStatus == null) { await RefreshStatusAsync(); return; }
        if (!gameStatus.GameInstalled)
        {
            await ExecuteOperationAsync("Open Steam installation", () => { GameService.InstallGame(); return Task.CompletedTask; });
            return;
        }
        if (gameStatus.GameRunning)
        {
            if (gameStatus.MenuLoaded) return;
            if (!gameStatus.BridgeAvailable)
            {
                ShowModal("Game is already running", "The Interstellar component must be installed before the game starts to load the menu during a session.", "Close", false);
                return;
            }
            var pid = gameStatus.ProcessId;
            if (!pid.HasValue) { await RefreshStatusAsync(); return; }
            await ExecuteOperationAsync("Load Interstellar", async () =>
            {
                var reply = await RuntimeBridgeClient.LoadAsync(pid.Value, operationsLifetime.Token);
                if (!reply.Available || !reply.Loaded) throw new InvalidOperationException(string.IsNullOrWhiteSpace(reply.Error) ? "The game did not confirm that Interstellar loaded. Open logs for details." : reply.Error);
            });
            return;
        }
        if (!gameStatus.BepInExInstalled)
        {
            SelectPage("Manage");
            ManageFeedback.Text = "Install BepInEx, then install Interstellar. Gorilla Tag must be closed.";
            return;
        }
        if (!gameStatus.MenuInstalled)
        {
            await ExecuteOperationAsync("Install Interstellar", () => ReleaseService.InstallAsync(preferences.GameDirectory, operationsLifetime.Token));
            return;
        }
        var withMenu = gameStatus.MenuInstalled && preferences.LaunchWithMenu;
        await ExecuteOperationAsync(withMenu ? "Launch with Interstellar" : "Launch Gorilla Tag", () => GameService.LaunchAsync(preferences.GameDirectory, withMenu, operationsLifetime.Token));
    }

    private async Task ExecuteOperationAsync(string label, Func<Task> action)
    {
        if (operationRunning || windowClosed || fullExitRequested) return;
        var settlement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        activeOperationCompletion = settlement.Task;
        operationRunning = true;
        try
        {
            OperationProgress.Visibility = Visibility.Visible;
            LoadButton.IsEnabled = false;
            ManageButton.IsEnabled = false;
            RefreshButton.IsEnabled = false;
            ActivityText.Text = label + "…";
            ManageFeedback.Text = label + "…";
            await action();
            ActivityText.Text = label.StartsWith("Launch", StringComparison.Ordinal) ? "Launch requested through Steam." : label == "Load Interstellar" ? "Interstellar is loaded." : label.StartsWith("Open Steam", StringComparison.Ordinal) ? "Opened in Steam." : "Done.";
            ManageFeedback.Text = ActivityText.Text;
        }
        catch (OperationCanceledException) when (operationsLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            GameService.LogError(label, error);
            ActivityText.Text = error.Message;
            ManageFeedback.Text = error.Message;
            if (!fullExitRequested)
            {
                ShowModal(label, error.Message + "\n\nYou can open the logs or copy diagnostics in Settings.", "Retry", false);
                modalOperation = new PendingOperation { Label = label, Action = action };
            }
        }
        finally
        {
            try
            {
                if (!windowClosed) await RefreshStatusAsync();
                operationRunning = false;
                OperationProgress.Visibility = Visibility.Collapsed;
                ManageButton.IsEnabled = true;
                RefreshButton.IsEnabled = true;
                if (!windowClosed) RenderGameStatus();
            }
            finally
            {
                operationRunning = false;
                if (ReferenceEquals(activeOperationCompletion, settlement.Task)) activeOperationCompletion = null;
                settlement.TrySetResult(true);
            }
        }
    }

    private void ConfirmOperation(string title, string description, string actionLabel, Func<Task> action)
    {
        if (operationRunning || fullExitRequested || windowClosed) return;
        ShowModal(title, description, actionLabel, false);
        modalOperation = new PendingOperation { Label = title, Action = action };
    }

    private void SavePreferences()
    {
        if (!productionMode) return;
        SettingsFeedback.Text = LoaderSettingsStore.Save(preferences) ? "" : LoaderSettingsStore.LastError ?? "The settings could not be saved.";
    }

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized || applyingPreferences) return;
        preferences.CloseToTray = CloseToTrayToggle.IsChecked == true;
        preferences.LaunchWithMenu = LaunchWithMenuToggle.IsChecked == true;
        preferences.AutoUpdateMenu = AutoUpdateMenuToggle.IsChecked == true;
        preferences.AutoUpdateLoader = AutoUpdateLoaderToggle.IsChecked == true;
        SavePreferences();
        RenderGameStatus();
    }

    private void Manage_Click(object sender, RoutedEventArgs e) => SelectPage("Manage");
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();
    private async void InstallMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!productionMode) return;
        await ExecuteOperationAsync("Install / repair Interstellar", () => ReleaseService.InstallAsync(preferences.GameDirectory, operationsLifetime.Token));
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(manual: true);

    private async void InstallBepInEx_Click(object sender, RoutedEventArgs e)
    {
        if (!productionMode) return;
        await ExecuteOperationAsync("Install BepInEx", () => BepInExService.InstallAsync(preferences.GameDirectory, operationsLifetime.Token));
    }

    private void RepairBepInEx_Click(object sender, RoutedEventArgs e)
    {
        if (!productionMode) return;
        ConfirmOperation("Repair BepInEx", "Restore the official BepInEx 5 framework files. Existing framework files are backed up. Plugins, patchers and configuration stay in place. Gorilla Tag must be closed.", "Repair", () => BepInExService.RepairAsync(preferences.GameDirectory, operationsLifetime.Token));
    }

    private void UninstallBepInEx_Click(object sender, RoutedEventArgs e)
    {
        if (!productionMode) return;
        ConfirmOperation("Uninstall BepInEx", "This disables all BepInEx mods, including Interstellar, until BepInEx is installed again. Plugins, patchers and settings stay in place. Only recorded framework files are removed and backed up. For an existing manual BepInEx installation, use Repair first. Gorilla Tag must be closed.", "Uninstall", () => BepInExService.UninstallAsync(preferences.GameDirectory, operationsLifetime.Token));
    }

    private async void InstallGame_Click(object sender, RoutedEventArgs e)
    {
        if (!productionMode) return;
        await ExecuteOperationAsync("Open Steam installation", () => { GameService.InstallGame(); return Task.CompletedTask; });
    }

    private async void RepairGame_Click(object sender, RoutedEventArgs e)
    {
        if (!productionMode) return;
        await ExecuteOperationAsync("Open Steam repair", () => { GameService.RepairGame(preferences.GameDirectory); return Task.CompletedTask; });
    }

    private async void LaunchWithoutMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!productionMode) return;
        await ExecuteOperationAsync("Launch Gorilla Tag", () => GameService.LaunchAsync(preferences.GameDirectory, false, operationsLifetime.Token));
    }

    private async void OpenGameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!productionMode) return;
        await ExecuteOperationAsync("Open game folder", () => { GameService.OpenFolder(preferences.GameDirectory); return Task.CompletedTask; });
    }

    private void UninstallMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!productionMode) return;
        try
        {
            var files = GameService.GetManagedFiles(preferences.GameDirectory);
            if (files.Length == 0) throw new InvalidOperationException("This installation has no files managed by Interstellar Loader.");
            var description = "These Interstellar files will be backed up and removed. Other mods will be kept.\n\n" + string.Join("\n", files.Select(Path.GetFileName));
            ConfirmOperation("Remove Interstellar", description, "Remove", () => GameService.UninstallAsync(preferences.GameDirectory, operationsLifetime.Token));
        }
        catch (Exception error) { ShowModal("Remove Interstellar", error.Message, "Close", false); }
    }

    private void ResetMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!productionMode) return;
        ConfirmOperation("Reset menu settings", "Back up and reset the menu settings managed by this loader. Your loader settings and other mods will be kept.", "Reset settings", () => GameService.ResetMenuSettingsAsync(preferences.GameDirectory, operationsLifetime.Token));
    }

    private void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (!productionMode) return;
        ConfirmOperation("Restore previous build", "Restore the previous verified Interstellar build saved by the loader. Gorilla Tag must be closed. Automatic menu updates will turn off so the restored build stays installed.", "Restore", async () =>
        {
            await GameService.RollbackAsync(preferences.GameDirectory, operationsLifetime.Token);
            applyingPreferences = true;
            preferences.AutoUpdateMenu = false;
            AutoUpdateMenuToggle.IsChecked = false;
            applyingPreferences = false;
            SavePreferences();
        });
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (!productionMode) { SettingsFeedback.Text = "Logs are available when the loader is running normally."; return; }
        try { Directory.CreateDirectory(GameService.LogDirectory); GameService.OpenFolder(GameService.LogDirectory); }
        catch (Exception error) { SettingsFeedback.Text = error.Message; }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (!productionMode || gameStatus == null) { SettingsFeedback.Text = "Refresh the game status first."; return; }
        try { Clipboard.SetText(GameService.DiagnosticText(gameStatus, null)); SettingsFeedback.Text = "Diagnostics copied."; }
        catch (Exception error) { SettingsFeedback.Text = error.Message; }
    }
}
