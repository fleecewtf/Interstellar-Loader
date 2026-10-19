using Microsoft.Win32;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Interstellar.Loader;

internal sealed class GameStatus
{
    public string GameDirectory { get; internal set; } = "";
    public bool GameInstalled { get; internal set; }
    public bool GameRunning { get; internal set; }
    public int? ProcessId { get; internal set; }
    public bool MenuInstalled { get; internal set; }
    public bool MenuLoaded { get; internal set; }
    public bool BridgeAvailable { get; internal set; }
    public bool MenuManaged { get; internal set; }
    public bool HasResettableMenuSettings { get; internal set; }

    public bool BepInExInstalled { get; internal set; }

    public string InstalledVersion { get; internal set; } = "";
    public string InstalledFileVersion { get; internal set; } = "";
    public string InstalledSha256 { get; internal set; } = "";
    public string Error { get; internal set; } = "";
}

internal static class GameService
{
    public const string SteamAppId = "1533390";
    private const string ManifestRelative = ".interstellar-loader/install.xml";
    private const string StartupRelative = "BepInEx/config/Interstellar.Loader.cfg";
    private const string MenuConfigRelative = "BepInEx/config/org.Interstellar.gorillatag.Interstellarmenu.cfg";
    private static readonly object LogLock = new object();
    internal static readonly object ManagedFilesLock = new object();

    public static string LogDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Interstellar", "Loader", "Logs");
    public static string LogPath => Path.Combine(LogDirectory, "loader.log");

    public static async Task<GameStatus> ReadStatusAsync(string? preferredDirectory, CancellationToken cancellationToken = default)
    {
        var status = await Task.Run(() => ReadLocalStatus(preferredDirectory, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (status.ProcessId is int pid)
        {
            var bridge = await RuntimeBridgeClient.QueryAsync(pid, cancellationToken).ConfigureAwait(false);
            status.BridgeAvailable = bridge.Available;
            status.MenuLoaded = bridge.Available && bridge.Loaded;

            if (!string.IsNullOrEmpty(bridge.MenuVersion)) status.InstalledVersion = bridge.MenuVersion;
            if (bridge.Available && !string.IsNullOrEmpty(bridge.Error)) status.Error = bridge.Error;
        }
        return status;
    }

    private static GameStatus ReadLocalStatus(string? preferredDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new GameStatus();
        try
        {
            result.GameDirectory = !string.IsNullOrWhiteSpace(preferredDirectory)
                ? NormalizeDirectory(preferredDirectory!) : DetectGameDirectoryFromLibraries(GetSteamLibraries()) ?? "";
            result.GameInstalled = ValidateGameDirectory(result.GameDirectory);
            if (!result.GameInstalled)
            {
                if (!string.IsNullOrWhiteSpace(preferredDirectory)) result.Error = "The selected folder does not contain Gorilla Tag. Choose the folder containing GorillaTag.exe.";
                return result;
            }
            cancellationToken.ThrowIfCancellationRequested();
            result.BepInExInstalled = File.Exists(Path.Combine(result.GameDirectory, "BepInEx", "core", "BepInEx.dll"));
            var menu = FindMenuFile(result.GameDirectory);
            result.MenuInstalled = menu != null;
            if (menu != null)
            {
                try
                {
                    result.InstalledFileVersion = AssemblyName.GetAssemblyName(menu).Version?.ToString(3) ?? "";
                    result.InstalledVersion = result.InstalledFileVersion;
                    result.InstalledSha256 = Hash(menu);
                }
                catch (Exception e) when (e is IOException || e is BadImageFormatException || e is UnauthorizedAccessException) { result.Error = "The installed menu file could not be read."; }
            }
            if (File.Exists(Path.Combine(result.GameDirectory, ManifestRelative)))
            {
                try
                {
                    var owned = ReadOwnedManifest(result.GameDirectory);
                    result.MenuManaged = owned.Files.Any(f => f.Kind == "menu" && File.Exists(f.FullPath));
                    result.HasResettableMenuSettings = owned.Files.Any(f => f.Kind == "settings" &&
                        f.RelativePath.Equals(MenuConfigRelative, StringComparison.OrdinalIgnoreCase) && File.Exists(f.FullPath));
                }
                catch (Exception e) when (e is IOException || e is InvalidOperationException || e is XmlException || e is UnauthorizedAccessException) { result.Error = "The loader installation record is invalid. Existing files have been left alone."; }
            }
            var running = GetRunningGames();
            var process = running.FirstOrDefault(p => string.Equals(Path.GetDirectoryName(p.Path), result.GameDirectory, StringComparison.OrdinalIgnoreCase));
            if (process != null)
            {
                result.ProcessId = process.Id;
                result.GameRunning = true;
            }

            if (running.Any(p => p.Path.Length == 0)) result.Error = "Windows could not confirm the location of a running Gorilla Tag process. Close the game before managing files.";
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is Win32Exception || e is System.Security.SecurityException)
        {
            result.Error = Redact(e.Message);
        }
        return result;
    }

    public static bool ValidateGameDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            string path = NormalizeDirectory(directory!);
            return File.Exists(Path.Combine(path, "GorillaTag.exe")) || File.Exists(Path.Combine(path, "Gorilla Tag.exe"));
        }
        catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException) { return false; }
    }

    public static string? DetectGameDirectoryFromLibraries(IEnumerable<string> libraries, bool requireSteamManifest = false)
    {
        foreach (string library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string steamApps = Path.Combine(NormalizeDirectory(library), "steamapps");
                string manifestPath = Path.Combine(steamApps, "appmanifest_" + SteamAppId + ".acf");
                if (File.Exists(manifestPath))
                {
                    var manifest = VdfParser.Parse(ReadBoundedText(manifestPath));
                    var state = manifest.FirstOrDefault(n => n.Key.Equals("AppState", StringComparison.OrdinalIgnoreCase));
                    string? appId = state?.Children.FirstOrDefault(n => n.Key.Equals("appid", StringComparison.OrdinalIgnoreCase))?.Value;
                    string? installDir = state?.Children.FirstOrDefault(n => n.Key.Equals("installdir", StringComparison.OrdinalIgnoreCase))?.Value;
                    if (appId == SteamAppId && IsLeafDirectory(installDir))
                    {
                        string candidate = Path.Combine(steamApps, "common", installDir!);
                        if (ValidateGameDirectory(candidate)) return NormalizeDirectory(candidate);
                    }
                }
                string fallback = Path.Combine(steamApps, "common", "Gorilla Tag");
                if (!requireSteamManifest && ValidateGameDirectory(fallback)) return NormalizeDirectory(fallback);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is FormatException) { }
        }
        return null;
    }

    public static IReadOnlyList<string> ParseLibraryFolders(string contents)
    {
        var result = new List<string>();
        foreach (var node in VdfParser.Parse(contents))
        {
            IEnumerable<VdfNode> entries = node.Key.Equals("libraryfolders", StringComparison.OrdinalIgnoreCase) ? node.Children : new[] { node };
            foreach (var entry in entries)
            {
                if (!int.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
                string? path = entry.Children.FirstOrDefault(n => n.Key.Equals("path", StringComparison.OrdinalIgnoreCase))?.Value ?? entry.Value;
                if (!string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path)) result.Add(NormalizeDirectory(path!));
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> GetSteamLibraries()
    {
        var roots = new List<string>();
        foreach (var pair in new[] { Tuple.Create(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"), Tuple.Create(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"), Tuple.Create(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath") })
        {
            try { using (var key = pair.Item1.OpenSubKey(pair.Item2)) { if (key?.GetValue(pair.Item3) is string value && Directory.Exists(value)) roots.Add(NormalizeDirectory(value)); } }
            catch (Exception e) when (e is UnauthorizedAccessException || e is System.Security.SecurityException || e is IOException) { }
        }
        string defaultSteam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (Directory.Exists(defaultSteam)) roots.Add(defaultSteam);
        var libraries = new List<string>(roots);
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(file)) continue;
            try { libraries.AddRange(ParseLibraryFolders(ReadBoundedText(file))); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is FormatException) { }
        }
        return libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static void InstallGame() => ShellOpen("steam://install/" + SteamAppId);

    public static void RepairGame(string? gameDirectory)
    {
        string directory = RequireInstalled(gameDirectory);
        RequireGameClosed();
        RequireSteamRegisteredDirectory(directory);
        ShellOpen("steam://validate/" + SteamAppId);
    }

    public static Task LaunchAsync(string? gameDirectory, bool withMenu, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                lock (ManagedFilesLock)
                {
                    string directory = RequireInstalled(gameDirectory);
                    cancellationToken.ThrowIfCancellationRequested();
                    RequireGameClosed();
                    RequireSteamRegisteredDirectory(directory);
                    string? menu = FindMenuFile(directory);
                    OwnedManifest? owned = null;
                    if (withMenu)
                    {
                        if (menu == null) ReleaseService.RequireAvailable();
                        owned = ReadOwnedManifest(directory);
                        if (!owned.Files.Any(f => f.Kind == "menu")) throw new InvalidOperationException("Install Interstellar through this loader before using managed launch.");
                    }
                    else if (menu != null)
                    {
                        if (!File.Exists(Path.Combine(directory, ManifestRelative))) throw new InvalidOperationException("Interstellar Menu is installed outside this loader. The loader cannot guarantee a launch without it and will not change those files.");
                        owned = ReadOwnedManifest(directory);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (owned == null) ShellOpen("steam://rungameid/" + SteamAppId);
                    else WriteStartupPreference(owned, withMenu, () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ShellOpen("steam://rungameid/" + SteamAppId);
                    });
                }
                completion.TrySetResult(true);
            }
            catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "Interstellar launch" };
        try
        {
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }
        catch (Exception error) { completion.TrySetException(error); }
        return completion.Task;
    }

    public static void OpenFolder(string directory)
    {
        string path = NormalizeDirectory(directory);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("That folder does not exist.");
        ShellOpen(path);
    }

    public static string[] GetManagedFiles(string gameDirectory) => ReadOwnedManifest(RequireInstalled(gameDirectory)).Files.Select(f => f.FullPath).ToArray();

    public static Task InstallBundledMenuAsync(string? gameDirectory, byte[] payload, string expectedSha256,
        bool loadOnStartup = false, CancellationToken cancellationToken = default, string? expectedVersion = null,
        bool preserveStartupPreference = false) => Task.Run(() =>
    {
        lock (ManagedFilesLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Version releaseVersion = ReadMenuVersion(expectedVersion ?? ReleaseService.CurrentMenuVersion);
            if (payload == null || payload.Length < 512 || payload.Length > 33554432 || expectedSha256 == null ||
                !Regex.IsMatch(expectedSha256, "\\A[0-9a-fA-F]{64}\\z") ||
                !HashBytes(payload).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The menu failed its file check. Download the release again.");
            string directory = RequireInstalled(gameDirectory);
            RequireGameClosed();
            if (!File.Exists(SafeOwnedPath(directory, "BepInEx/core/BepInEx.dll")))
                throw new InvalidOperationException("BepInEx 5 is required. Install it from Manage, run Gorilla Tag once, close the game, then install Interstellar.");
            EnsureNoIncompleteTransactions(directory);
            string receiptPath = SafeOwnedPath(directory, ManifestRelative);
            bool existing = File.Exists(receiptPath);
            OwnedManifest owned;
            if (existing) owned = ReadOwnedManifest(directory);
            else
            {
                var document = new XmlDocument { XmlResolver = null };
                var root = document.CreateElement("Installation");
                document.AppendChild(root);
                root.SetAttribute("product", "Interstellar Menu");
                root.SetAttribute("version", "1.0.0");
                root.SetAttribute("gameDirectory", directory);
                owned = new OwnedManifest(document, receiptPath, directory);
            }
            var menus = owned.Files.Where(file => file.Kind == "menu").ToArray();
            if (menus.Length > 1) throw new InvalidOperationException("The installation record contains more than one menu. Existing files have been left alone.");
            string menuRelative = menus.Length == 1 ? menus[0].RelativePath : "BepInEx/plugins/Interstellar/Interstellar Menu.dll";
            string menuPath = SafeOwnedPath(directory, menuRelative);
            foreach (string found in FindMenuFiles(directory))
                if (!existing || menus.Length == 0 || !found.Equals(menuPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("An Interstellar DLL was installed manually. Move that DLL out of BepInEx/plugins, then install through this loader. Other files have been left alone.");
            foreach (var file in owned.Files) VerifyCurrent(file, allowChangedSettings: true);
            string recordedVersion = owned.Document.DocumentElement!.GetAttribute("menuVersion");
            if (recordedVersion.Length > 0 && ReadMenuVersion(recordedVersion) > releaseVersion)
                throw new InvalidOperationException("A newer Interstellar Menu was installed here. An older build will not replace it.");
            if (File.Exists(menuPath))
            {
                var installed = AssemblyName.GetAssemblyName(menuPath);
                if (installed.Name != "Interstellar Menu" || installed.Version == null)
                    throw new InvalidOperationException("The installed file is not Interstellar Menu. Existing files have been left alone.");
                if (installed.Version > releaseVersion)
                    throw new InvalidOperationException("A newer Interstellar Menu is already installed. An older build will not replace it.");
            }
            OwnedFile menu = menus.Length == 1 ? menus[0] : AddOwnedFile(owned, menuRelative, "menu", expectedSha256);
            var startup = owned.Files.FirstOrDefault(file => file.RelativePath.Equals(StartupRelative, StringComparison.OrdinalIgnoreCase));
            if (startup == null)
            {
                if (File.Exists(SafeOwnedPath(directory, StartupRelative)))
                    throw new InvalidOperationException("An existing Interstellar startup configuration is not managed by this loader. Move it aside before installing.");
                startup = AddOwnedFile(owned, StartupRelative, "settings", HashBytes(Array.Empty<byte>()));
            }
            if (File.Exists(startup.FullPath) && !ReadBoundedText(startup.FullPath).Split('\r', '\n')
                .Any(line => line.Trim().Equals("managed=true", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The startup configuration is not managed by the loader. Existing files have been left alone.");
            if (preserveStartupPreference && File.Exists(startup.FullPath))
            {
                string[] choices = ReadBoundedText(startup.FullPath).Split('\r', '\n')
                    .Select(line => line.Trim()).Where(line => line.StartsWith("loadOnStartup=", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (choices.Length != 1 || !bool.TryParse(choices[0].Substring("loadOnStartup=".Length).Trim(), out loadOnStartup))
                    throw new InvalidOperationException("The Interstellar startup preference is invalid. Existing files have been left alone.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var flags = startup;
            RunTransaction(owned, new[] { menu, flags }, existing ? "Repair menu" : "Install menu", transaction =>
            {
                transaction.StageBytes(menu, payload, expectedSha256);
                var identity = AssemblyName.GetAssemblyName(transaction.GetStagedPath(menu));
                if (identity.Name != "Interstellar Menu" || identity.Version != releaseVersion)
                    throw new InvalidOperationException("The file does not match Interstellar Menu " + releaseVersion.ToString(3) + ".");
                transaction.StageText(flags, "[Loader]\nmanaged=true\nloadOnStartup=" + (loadOnStartup ? "true" : "false") + "\n");
                if (File.Exists(menu.FullPath) && !menu.Sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    transaction.PreservePrevious(menu);
                cancellationToken.ThrowIfCancellationRequested();
                RequireGameClosed();
                transaction.InstallStaged(menu);
                cancellationToken.ThrowIfCancellationRequested();
                transaction.InstallStaged(flags);
                menu.Element.SetAttribute("sha256", expectedSha256);
                flags.Element.SetAttribute("sha256", Hash(flags.FullPath));
                owned.Document.DocumentElement!.SetAttribute("menuVersion", releaseVersion.ToString(3));
                transaction.BeforeManifestCommit();
                SaveManifest(owned);
            });
        }
    }, cancellationToken);

    private static Version ReadMenuVersion(string value)
    {
        if (!Regex.IsMatch(value, "\\A(?:0|[1-9][0-9]{0,4})\\.(?:0|[1-9][0-9]{0,4})\\.(?:0|[1-9][0-9]{0,4})\\z") ||
            !Version.TryParse(value, out Version? version) || version.Major > 65535 || version.Minor > 65535 || version.Build > 65535)
            throw new InvalidOperationException("The menu release version is invalid.");
        return new Version(version.Major, version.Minor, version.Build, 0);
    }

    private static OwnedFile AddOwnedFile(OwnedManifest owned, string relative, string kind, string hash)
    {
        if (!IsAllowedOwnedFile(relative, kind)) throw new InvalidOperationException("Unexpected installer path.");
        var element = owned.Document.CreateElement("File");
        element.SetAttribute("path", relative);
        element.SetAttribute("kind", kind);
        element.SetAttribute("sha256", hash);
        owned.Document.DocumentElement!.AppendChild(element);
        var file = new OwnedFile(element, relative, SafeOwnedPath(owned.GameDirectory, relative), kind, hash);
        owned.Files.Add(file);
        return file;
    }

    private static string HashBytes(byte[] data)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
    }

    public static Task UninstallAsync(string? gameDirectory, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        lock (ManagedFilesLock)
        {
            string directory = RequireInstalled(gameDirectory);
            RequireGameClosed();
            var owned = ReadOwnedManifest(directory);
            foreach (var file in owned.Files) VerifyCurrent(file, allowChangedSettings: true);
            cancellationToken.ThrowIfCancellationRequested();
            RunTransaction(owned, owned.Files.ToArray(), "Uninstall", transaction =>
            {
                foreach (var file in owned.Files) { cancellationToken.ThrowIfCancellationRequested(); transaction.MoveToHistory(file); }
                transaction.BeforeManifestCommit();
                File.Move(owned.ManifestPath, Path.Combine(transaction.HistoryDirectory, "install.xml"));
            });
        }
    }, cancellationToken);

    public static Task ResetMenuSettingsAsync(string? gameDirectory, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        lock (ManagedFilesLock)
        {
            string directory = RequireInstalled(gameDirectory);
            RequireGameClosed();
            var owned = ReadOwnedManifest(directory);
            var settings = owned.Files.Where(f => f.Kind == "settings" && f.RelativePath.Equals(MenuConfigRelative, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (settings.Length == 0 || !settings.Any(f => File.Exists(f.FullPath))) throw new InvalidOperationException("No loader-managed menu settings are available to reset.");
            cancellationToken.ThrowIfCancellationRequested();
            RunTransaction(owned, settings, "Reset settings", transaction =>
            {
                foreach (var file in settings) { cancellationToken.ThrowIfCancellationRequested(); transaction.MoveToHistory(file); }
                foreach (var file in settings) { owned.Files.Remove(file); file.Element.ParentNode?.RemoveChild(file.Element); }
                transaction.BeforeManifestCommit();
                SaveManifest(owned);
            });
        }
    }, cancellationToken);

    public static Task RollbackAsync(string? gameDirectory, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        lock (ManagedFilesLock)
        {
            string directory = RequireInstalled(gameDirectory);
            RequireGameClosed();
            var owned = ReadOwnedManifest(directory);
            var files = owned.Files.Where(f => f.PreviousFullPath.Length > 0).ToArray();
            if (files.Length == 0) throw new InvalidOperationException("No previous loader-managed build is available to restore.");
            foreach (var file in files)
            {
                VerifyCurrent(file, allowChangedSettings: true);
                if (!File.Exists(file.PreviousFullPath) || !Hash(file.PreviousFullPath).Equals(file.PreviousSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The previous build failed its file check. Existing files have been left alone.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            RunTransaction(owned, files, "Rollback", transaction =>
            {
                foreach (var file in files) { cancellationToken.ThrowIfCancellationRequested(); transaction.StagePrevious(file); }
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (file.Kind == "menu")
                    {
                        var identity = AssemblyName.GetAssemblyName(transaction.GetStagedPath(file));
                        if (identity.Name != "Interstellar Menu" || identity.Version == null || identity.Version.Revision != 0)
                            throw new InvalidOperationException("The previous build is not a valid Interstellar Menu release.");
                        owned.Document.DocumentElement!.SetAttribute("menuVersion", identity.Version.ToString(3));
                    }
                    transaction.InstallStaged(file);
                    file.Element.SetAttribute("sha256", file.PreviousSha256);
                    file.Element.RemoveAttribute("previous");
                    file.Element.RemoveAttribute("previousSha256");
                }
                transaction.BeforeManifestCommit();
                SaveManifest(owned);
            });
        }
    }, cancellationToken);

    private static void WriteStartupPreference(OwnedManifest owned, bool loadOnStartup, Action launch)
    {
        foreach (var file in owned.Files) VerifyCurrent(file, allowChangedSettings: true);
        var flag = owned.Files.FirstOrDefault(f => f.RelativePath.Equals(StartupRelative, StringComparison.OrdinalIgnoreCase));
        if (flag == null) throw new InvalidOperationException("This installation has no loader-managed runtime connection. Use Install / repair menu first.");
        if (File.Exists(flag.FullPath))
        {
            string old = ReadBoundedText(flag.FullPath);
            if (!old.Split('\r', '\n').Any(line => line.Trim().Equals("managed=true", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("The startup configuration is not owned by the loader. Existing files have been left alone.");
        }
        RunTransaction(owned, new[] { flag }, "Launch preferences", transaction =>
        {
            string value = "[Loader]\nmanaged=true\nloadOnStartup=" + (loadOnStartup ? "true" : "false") + "\n";
            transaction.StageText(flag, value);
            transaction.InstallStaged(flag);
            flag.Element.SetAttribute("sha256", Hash(flag.FullPath));
            transaction.BeforeManifestCommit();
            SaveManifest(owned);

            launch();
        });
    }

    private static OwnedManifest ReadOwnedManifest(string directory)
    {
        string path = SafeOwnedPath(directory, ManifestRelative);
        if (!File.Exists(path)) throw new InvalidOperationException("This installation was not installed by the loader. Existing files have been left alone.");
        if (new FileInfo(path).Length > 262144) throw new InvalidOperationException("The loader installation record is invalid.");
        var document = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 262144 })) document.Load(reader);
        var root = document.DocumentElement;
        if (root == null || root.Name != "Installation" || root.GetAttribute("product") != "Interstellar Menu" || root.GetAttribute("version") != "1.0.0" || !NormalizeDirectory(root.GetAttribute("gameDirectory")).Equals(directory, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The loader installation record does not match this game folder.");
        if (root.HasAttribute("menuVersion")) ReadMenuVersion(root.GetAttribute("menuVersion"));
        var result = new OwnedManifest(document, path, directory);
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (XmlNode child in root.ChildNodes)
        {
            if (!(child is XmlElement element) || element.Name != "File") continue;
            if (result.Files.Count >= 128) throw new InvalidOperationException("The loader installation record is too large.");
            string relative = element.GetAttribute("path").Replace('\\', '/');
            string kind = element.GetAttribute("kind");
            if (!IsAllowedOwnedFile(relative, kind) || !duplicates.Add(relative)) throw new InvalidOperationException("The loader installation record contains an unrecognized file.");
            string sha = element.GetAttribute("sha256");
            if (!Regex.IsMatch(sha, "\\A[0-9a-fA-F]{64}\\z")) throw new InvalidOperationException("The loader installation record has an invalid file hash.");
            var file = new OwnedFile(element, relative, SafeOwnedPath(directory, relative), kind, sha);
            string previous = element.GetAttribute("previous").Replace('\\', '/');
            if (previous.Length > 0)
            {
                if (!previous.StartsWith(".interstellar-loader/backups/", StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(element.GetAttribute("previousSha256"), "\\A[0-9a-fA-F]{64}\\z")) throw new InvalidOperationException("The previous build record is invalid.");
                file.PreviousFullPath = SafeOwnedPath(directory, previous);
                file.PreviousSha256 = element.GetAttribute("previousSha256");
            }
            result.Files.Add(file);
        }
        if (result.Files.Count == 0) throw new InvalidOperationException("The loader installation record contains no files.");
        return result;
    }

    private static bool IsAllowedOwnedFile(string relative, string kind)
    {
        if (kind == "menu") return relative.Equals("BepInEx/plugins/Interstellar Menu.dll", StringComparison.OrdinalIgnoreCase) || relative.Equals("BepInEx/plugins/Interstellar/Interstellar Menu.dll", StringComparison.OrdinalIgnoreCase);
        if (kind == "settings") return relative.Equals(MenuConfigRelative, StringComparison.OrdinalIgnoreCase) || relative.Equals(StartupRelative, StringComparison.OrdinalIgnoreCase);
        return kind == "resource" && relative.StartsWith("InterstellarMenu/", StringComparison.OrdinalIgnoreCase);
    }

    internal static string SafeOwnedPath(string directory, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p.Length == 0 || p == "." || p == ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) throw new InvalidOperationException("The loader installation record contains an unsafe path.");
        string root = NormalizeDirectory(directory);
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The file is outside the selected game folder.");
        string current = root;
        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Loader-managed files cannot use linked folders.");
        foreach (string component in relative.Split('/', '\\'))
        {
            current = Path.Combine(current, component);
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Loader-managed files cannot use linked folders or files.");
        }
        return full;
    }

    private static void VerifyCurrent(OwnedFile file, bool allowChangedSettings)
    {
        if (!File.Exists(file.FullPath)) return;

        if (allowChangedSettings && file.Kind == "settings") return;
        if (!Hash(file.FullPath).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("An installed file has changed since the loader installed it: " + file.RelativePath + ". Existing files have been left alone.");
    }

    private static string CreateHistoryDirectory(string directory)
    {
        EnsureNoIncompleteTransactions(directory);
        string relative = ".interstellar-loader/history/" + Guid.NewGuid().ToString("N");
        string history = SafeOwnedPath(directory, relative);
        Directory.CreateDirectory(history);
        return history;
    }

    private static void RunTransaction(OwnedManifest owned, OwnedFile[] files, string operation, Action<ManagedTransaction> action)
    {
        using (var transaction = new ManagedTransaction(owned, files, operation))
        {
            try
            {
                transaction.Prepare();
                action(transaction);
                transaction.Commit();
            }
            catch (Exception error)
            {
                var recoveryErrors = transaction.RestoreOriginals();
                if (recoveryErrors.Count > 0) throw new InvalidOperationException(
                    "The operation failed and some original files could not be restored. Close apps using the game files. Recovery copies and the file journal are in " + transaction.HistoryDirectory + ". " + string.Join("; ", recoveryErrors), error);
                if (error is OperationCanceledException) throw;
                throw new InvalidOperationException("The operation failed. The original files and installation record were restored. " + error.Message, error);
            }
        }
    }

    private static void EnsureNoIncompleteTransactions(string directory)
    {
        string history = SafeOwnedPath(directory, ".interstellar-loader/history");
        if (!Directory.Exists(history)) return;
        int examined = 0;
        foreach (string entry in Directory.EnumerateDirectories(history))
        {
            if (++examined > 1024) throw new InvalidOperationException("There are too many loader history entries to confirm that file management is safe.");
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("The loader history contains a linked folder. Existing game files have been left alone.");
            string journal = Path.Combine(entry, ".transaction", "journal.xml");
            if (!File.Exists(journal)) continue;
            SafeOwnedPath(directory, Path.GetFullPath(journal).Substring(directory.TrimEnd(Path.DirectorySeparatorChar).Length + 1));
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(journal, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 262144 })) document.Load(reader);
            string state = document.DocumentElement?.GetAttribute("state") ?? "";
            if (state != "Committed" && state != "Restored") throw new InvalidOperationException("A previous loader operation needs recovery. Close apps using the game files and restore the original copies recorded in " + entry + " before making more changes.");
        }
    }

    private static void SaveManifest(OwnedManifest owned)
    {
        string temporary = owned.ManifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        owned.Document.Save(temporary);
        if (File.Exists(owned.ManifestPath)) File.Replace(temporary, owned.ManifestPath, null);
        else File.Move(temporary, owned.ManifestPath);
    }

    private static string Hash(string path)
    {
        using (var sha = SHA256.Create()) using (var file = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
    }

    private static string? FindMenuFile(string directory)
        => FindMenuFiles(directory).FirstOrDefault();

    private static IEnumerable<string> FindMenuFiles(string directory)
    {
        string plugins = Path.Combine(directory, "BepInEx", "plugins");
        if (!Directory.Exists(plugins)) yield break;
        var pending = new Queue<Tuple<string, int>>();
        pending.Enqueue(Tuple.Create(plugins, 0));
        int checkedDirectories = 0;
        int discoveredDirectories = 1;
        while (pending.Count > 0 && checkedDirectories++ < 128)
        {
            var next = pending.Dequeue();
            if ((File.GetAttributes(next.Item1) & FileAttributes.ReparsePoint) != 0) continue;
            string candidate = Path.Combine(next.Item1, "Interstellar Menu.dll");
            if (File.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) == 0) yield return candidate;
            if (next.Item2 >= 4 || discoveredDirectories >= 128) continue;
            foreach (string child in Directory.EnumerateDirectories(next.Item1))
            {
                pending.Enqueue(Tuple.Create(child, next.Item2 + 1));
                if (++discoveredDirectories >= 128) break;
            }
        }
    }

    internal static string RequireInstalled(string? directory)
    {
        if (!ValidateGameDirectory(directory)) throw new InvalidOperationException("Choose the Gorilla Tag game folder first.");
        return NormalizeDirectory(directory!);
    }

    internal static bool IsRegisteredGameDirectory(string directory, IEnumerable<string> steamLibraries)
    {
        var registered = steamLibraries.Select(library => DetectGameDirectoryFromLibraries(new[] { library }, requireSteamManifest: true))
            .Where(path => path != null).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        return registered.Length == 1 && NormalizeDirectory(directory).Equals(registered[0], StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireSteamRegisteredDirectory(string directory)
    {
        if (!IsRegisteredGameDirectory(directory, GetSteamLibraries())) throw new InvalidOperationException("Steam could not confirm this as the only registered Gorilla Tag installation. Select the Steam installation and resolve any duplicate copies before launching or repairing it.");
    }

    internal static void RequireGameClosed()
    {
        if (GetRunningGames().Count > 0) throw new InvalidOperationException("Close Gorilla Tag before changing its installation or launching another session.");
    }

    private static string NormalizeDirectory(string path)
    {
        string full = Path.GetFullPath(path.Trim().Trim('"'));
        return full.Equals(Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase) ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
    private static bool IsLeafDirectory(string? path) => !string.IsNullOrWhiteSpace(path) && path != "." && path != ".." && !Path.IsPathRooted(path) && path!.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    private static string ReadBoundedText(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 1048576) throw new IOException("A Steam configuration file is too large to read.");
        using var contents = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (contents.Length + count > 1048576) throw new IOException("A Steam configuration file is too large to read.");
            contents.Write(buffer, 0, count);
        }
        contents.Position = 0;
        using var reader = new StreamReader(contents, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    public static string DiagnosticText(GameStatus status, Exception? error = null)
    {
        var text = new StringBuilder();
        text.AppendLine("Interstellar Loader " + UpdateService.CurrentLoaderVersion);
        text.AppendLine("Menu release: " + ReleaseService.CurrentMenuVersion + " (" + ReleaseService.DevelopmentMessage + ")");
        text.AppendLine("Game folder: " + status.GameDirectory);
        text.AppendLine("Game installed: " + status.GameInstalled);
        text.AppendLine("Game running: " + status.GameRunning);
        text.AppendLine("Menu installed: " + status.MenuInstalled);
        text.AppendLine("Menu managed by loader: " + status.MenuManaged);
        text.AppendLine("Menu loaded: " + status.MenuLoaded);
        text.AppendLine("Runtime bridge: " + status.BridgeAvailable);
        text.AppendLine("Installed menu version: " + status.InstalledVersion);
        if (status.Error.Length > 0) text.AppendLine("Status error: " + status.Error);
        if (error != null) text.AppendLine("Last action error: " + error.GetType().Name + ": " + error.Message);
        return Redact(text.ToString());
    }

    public static void LogError(string action, Exception error)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(LogDirectory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1048576) File.WriteAllText(LogPath, "Previous log rotated.\n");
                File.AppendAllText(LogPath, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + Redact(action + ": " + error.GetType().Name + ": " + error.Message) + Environment.NewLine);
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException) { }
    }

    private static string Redact(string text)
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string value = string.IsNullOrEmpty(userProfile) ? text : Regex.Replace(text, Regex.Escape(userProfile), "%USERPROFILE%", RegexOptions.IgnoreCase);
        return Regex.Replace(value, @"(?<![A-Za-z0-9])(?:%USERPROFILE%(?:[\\/]|$)|[A-Za-z]:[\\/]|\\\\|(?<!:)//)[^\r\n]*",
            match => match.Value.StartsWith("%USERPROFILE%", StringComparison.OrdinalIgnoreCase) ? "%USERPROFILE%" : "%PATH%",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static void ShellOpen(string target)
    {
        long result = ShellExecute(IntPtr.Zero, "open", target, null, null, 1).ToInt64();
        if (result <= 32) throw new InvalidOperationException("Windows could not open Steam or the selected folder (error " + result.ToString(CultureInfo.InvariantCulture) + "). Make sure Steam is installed.");
    }

    private static List<GameProcess> GetRunningGames()
    {
        var result = new List<GameProcess>();
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry)), ExecutableFile = "" };
            if (!Process32First(snapshot, ref entry))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 18) throw new Win32Exception(error);
                return result;
            }
            do
            {
                if (!entry.ExecutableFile.Equals("GorillaTag.exe", StringComparison.OrdinalIgnoreCase) && !entry.ExecutableFile.Equals("Gorilla Tag.exe", StringComparison.OrdinalIgnoreCase)) continue;
                string path = "";
                IntPtr process = OpenProcess(0x1000, false, entry.ProcessId);
                if (process != IntPtr.Zero)
                {
                    try { var buffer = new StringBuilder(32768); int length = buffer.Capacity; if (QueryFullProcessImageName(process, 0, buffer, ref length)) path = buffer.ToString(); }
                    finally { CloseHandle(process); }
                }
                result.Add(new GameProcess((int)entry.ProcessId, path));
            } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    private sealed class GameProcess
    {
        public int Id { get; }
        public string Path { get; }
        public GameProcess(int id, string path) { Id = id; Path = path; }
    }
    private sealed class OwnedManifest
    {
        public XmlDocument Document { get; }
        public string ManifestPath { get; }
        public string GameDirectory { get; }
        public List<OwnedFile> Files { get; } = new List<OwnedFile>();
        public OwnedManifest(XmlDocument document, string path, string directory) { Document = document; ManifestPath = path; GameDirectory = directory; }
    }
    private sealed class OwnedFile
    {
        public XmlElement Element { get; }
        public string RelativePath { get; }
        public string FullPath { get; }
        public string Kind { get; }
        public string Sha256 { get; }
        public string PreviousFullPath { get; set; } = "";
        public string PreviousSha256 { get; set; } = "";
        public OwnedFile(XmlElement element, string relative, string full, string kind, string sha) { Element = element; RelativePath = relative; FullPath = full; Kind = kind; Sha256 = sha; }
    }

    private sealed class ManagedTransaction : IDisposable
    {
        private readonly OwnedManifest owned;
        private readonly OwnedFile[] files;
        private readonly string operation;
        private readonly List<OriginalFile> originals = new List<OriginalFile>();
        private readonly Dictionary<string, string> staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> stagedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string HistoryDirectory { get; }
        private string ControlDirectory => Path.Combine(HistoryDirectory, ".transaction");

        public ManagedTransaction(OwnedManifest owned, OwnedFile[] files, string operation)
        {
            this.owned = owned;
            this.files = files;
            this.operation = operation;
            HistoryDirectory = CreateHistoryDirectory(owned.GameDirectory);
            Directory.CreateDirectory(ControlDirectory);
        }

        public void Prepare()
        {
            WriteJournal("Staging");
            Capture(ManifestRelative, owned.ManifestPath);
            foreach (var file in files)
            {
                Capture(file.RelativePath, file.FullPath);
                var original = originals[originals.Count - 1];
                if (original.Existed && file.Kind != "settings" && !original.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("An installed file changed during staging. Existing files have been left alone.");
            }
            WriteJournal("Prepared");
        }

        private void Capture(string relative, string path)
        {
            SafeOwnedPath(owned.GameDirectory, relative);
            int index = originals.Count;
            var original = new OriginalFile(index, relative, path, Path.Combine(ControlDirectory, "o", index.ToString(CultureInfo.InvariantCulture) + ".bin"), File.Exists(path));
            if (original.Existed)
            {
                var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                try
                {
                    original.Attributes = File.GetAttributes(path);
                    original.LastWriteUtc = File.GetLastWriteTimeUtc(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(original.CopyPath)!);
                    using (var copy = new FileStream(original.CopyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough)) { handle.CopyTo(copy); copy.Flush(true); }
                    original.Sha256 = Hash(original.CopyPath);
                    original.ReadHandle = handle;
                }
                catch { handle.Dispose(); throw; }
            }
            originals.Add(original);
        }

        public void StagePrevious(OwnedFile file)
        {
            SafeOwnedPath(owned.GameDirectory, Relative(file.PreviousFullPath));
            string path = StagingPath(file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            File.Copy(file.PreviousFullPath, path, false);
            if (!Hash(path).Equals(file.PreviousSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The previous build changed during staging. Existing files have been left alone.");
            staged.Add(file.FullPath, path);
            stagedHashes.Add(file.FullPath, file.PreviousSha256);
        }

        public void StageText(OwnedFile file, string value)
        {
            string path = StagingPath(file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value, new UTF8Encoding(false));
            staged.Add(file.FullPath, path);
            stagedHashes.Add(file.FullPath, Hash(path));
        }

        public void StageBytes(OwnedFile file, byte[] value, string expectedHash)
        {
            string path = StagingPath(file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            { output.Write(value, 0, value.Length); output.Flush(true); }
            if (!Hash(path).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The bundled menu changed while staging.");
            staged.Add(file.FullPath, path);
            stagedHashes.Add(file.FullPath, expectedHash);
        }

        public string GetStagedPath(OwnedFile file) => staged[file.FullPath];

        public void PreservePrevious(OwnedFile file)
        {
            var original = originals.Single(item => item.Path.Equals(file.FullPath, StringComparison.OrdinalIgnoreCase));
            if (!original.Existed) return;
            string relative = ".interstellar-loader/backups/" + Guid.NewGuid().ToString("N") + "/Interstellar Menu.dll";
            string destination = SafeOwnedPath(owned.GameDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(original.CopyPath, destination, false);
            if (!Hash(destination).Equals(original.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The previous menu could not be backed up.");
            file.Element.SetAttribute("previous", relative);
            file.Element.SetAttribute("previousSha256", original.Sha256);
        }

        private string StagingPath(OwnedFile file) => Path.Combine(ControlDirectory, "s", Array.IndexOf(files, file).ToString(CultureInfo.InvariantCulture) + ".bin");

        public void MoveToHistory(OwnedFile file)
        {
            SafeOwnedPath(owned.GameDirectory, file.RelativePath);
            var original = originals.Single(item => item.Path.Equals(file.FullPath, StringComparison.OrdinalIgnoreCase));
            if (!File.Exists(file.FullPath)) return;
            string backup = Path.Combine(HistoryDirectory, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Move(file.FullPath, backup);
            original.Touched = true;
        }

        public void InstallStaged(OwnedFile file)
        {
            string replacement = staged[file.FullPath];

            SafeOwnedPath(owned.GameDirectory, Relative(replacement));
            SafeOwnedPath(owned.GameDirectory, file.RelativePath);
            using (var input = new FileStream(replacement, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                string hash;
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
                if (!hash.Equals(stagedHashes[file.FullPath], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A staged installation file changed. Existing files will be restored.");
                MoveToHistory(file);
                var original = originals.Single(item => item.Path.Equals(file.FullPath, StringComparison.OrdinalIgnoreCase));
                original.Touched = true;
                Directory.CreateDirectory(Path.GetDirectoryName(file.FullPath)!);
                File.Move(replacement, file.FullPath);
            }
        }

        public void BeforeManifestCommit()
        {
            originals.Single(item => item.RelativePath == ManifestRelative).Touched = true;
        }

        public void Commit() => WriteJournal("Committed");

        public List<string> RestoreOriginals()
        {
            var failures = new List<string>();
            foreach (var original in originals.AsEnumerable().Reverse())
            {
                if (!original.Touched) continue;
                try
                {
                    SafeOwnedPath(owned.GameDirectory, original.RelativePath);
                    if (original.Existed)
                    {
                        if (File.Exists(original.Path) && Hash(original.Path).Equals(original.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!File.Exists(original.CopyPath) || !Hash(original.CopyPath).Equals(original.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("The original recovery copy failed its hash check.");
                        string ready = Path.Combine(ControlDirectory, "r", original.Index.ToString(CultureInfo.InvariantCulture) + ".bin");
                        Directory.CreateDirectory(Path.GetDirectoryName(ready)!);
                        File.Copy(original.CopyPath, ready, false);
                        Directory.CreateDirectory(Path.GetDirectoryName(original.Path)!);
                        if (File.Exists(original.Path))
                        {
                            string replaced = Path.Combine(ControlDirectory, "f", original.Index.ToString(CultureInfo.InvariantCulture) + ".bin");
                            Directory.CreateDirectory(Path.GetDirectoryName(replaced)!);
                            File.Replace(ready, original.Path, replaced);
                        }
                        else File.Move(ready, original.Path);
                        File.SetAttributes(original.Path, original.Attributes);
                        File.SetLastWriteTimeUtc(original.Path, original.LastWriteUtc);
                        if (!Hash(original.Path).Equals(original.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("The restored file failed its hash check.");
                    }
                    else if (File.Exists(original.Path))
                    {
                        string removed = Path.Combine(ControlDirectory, "f", original.Index.ToString(CultureInfo.InvariantCulture) + ".bin");
                        Directory.CreateDirectory(Path.GetDirectoryName(removed)!);
                        File.Move(original.Path, removed);
                    }
                }
                catch (Exception error) { failures.Add(original.RelativePath + ": " + error.Message); }
            }
            try { WriteJournal(failures.Count == 0 ? "Restored" : "RecoveryRequired", failures); }
            catch (Exception error) { failures.Add("Recovery journal: " + error.Message); }
            return failures;
        }

        private string Relative(string path) => Path.GetFullPath(path).Substring(owned.GameDirectory.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/');

        private void WriteJournal(string state, List<string>? errors = null)
        {
            var document = new XmlDocument();
            var root = document.CreateElement("Transaction");
            document.AppendChild(root);
            root.SetAttribute("operation", operation);
            root.SetAttribute("state", state);
            root.SetAttribute("gameDirectory", owned.GameDirectory);
            foreach (var original in originals)
            {
                var file = document.CreateElement("Original");
                file.SetAttribute("path", original.RelativePath);
                file.SetAttribute("existed", original.Existed ? "true" : "false");
                file.SetAttribute("sha256", original.Sha256);
                file.SetAttribute("copy", ".transaction/o/" + original.Index.ToString(CultureInfo.InvariantCulture) + ".bin");
                root.AppendChild(file);
            }
            foreach (string error in errors ?? new List<string>()) { var item = document.CreateElement("Error"); item.InnerText = error; root.AppendChild(item); }
            string path = Path.Combine(ControlDirectory, "journal.xml");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough)) { document.Save(stream); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        public void Dispose() { foreach (var original in originals) original.ReadHandle?.Dispose(); }

        private sealed class OriginalFile
        {
            public int Index { get; }
            public string RelativePath { get; }
            public string Path { get; }
            public string CopyPath { get; }
            public bool Existed { get; }
            public string Sha256 { get; set; } = "";
            public bool Touched { get; set; }
            public FileAttributes Attributes { get; set; }
            public DateTime LastWriteUtc { get; set; }
            public FileStream? ReadHandle { get; set; }
            public OriginalFile(int index, string relative, string path, string copy, bool existed) { Index = index; RelativePath = relative; Path = path; CopyPath = copy; Existed = existed; }
        }
    }

    private sealed class VdfNode
    {
        public string Key { get; }
        public string? Value { get; set; }
        public List<VdfNode> Children { get; } = new List<VdfNode>();
        public VdfNode(string key) { Key = key; }
    }
    private static class VdfParser
    {
        public static List<VdfNode> Parse(string text)
        {
            if (text.Length > 1048576) throw new FormatException("The Steam configuration is too large.");
            int position = 0;
            var result = ReadNodes(text, ref position, 0, false);
            return result;
        }
        private static List<VdfNode> ReadNodes(string text, ref int position, int depth, bool needsClose)
        {
            if (depth > 32) throw new FormatException("The Steam configuration is too deeply nested.");
            var result = new List<VdfNode>();
            while (true)
            {
                string? key = Token(text, ref position);
                if (key == null) { if (needsClose) throw new FormatException("The Steam configuration has an incomplete section."); return result; }
                if (key == "}") { if (!needsClose) throw new FormatException("The Steam configuration has an unexpected section ending."); return result; }
                if (key == "{") throw new FormatException("The Steam configuration has an unnamed section.");
                string? value = Token(text, ref position);
                if (value == null || value == "}") throw new FormatException("The Steam configuration has an incomplete entry.");
                var node = new VdfNode(key);
                if (value == "{") node.Children.AddRange(ReadNodes(text, ref position, depth + 1, true));
                else node.Value = value;
                result.Add(node);
            }
        }
        private static string? Token(string text, ref int position)
        {
            while (position < text.Length)
            {
                if (char.IsWhiteSpace(text[position])) { position++; continue; }
                if (text[position] == '/' && position + 1 < text.Length && text[position + 1] == '/') { while (position < text.Length && text[position] != '\n') position++; continue; }
                break;
            }
            if (position == text.Length) return null;
            char first = text[position++];
            if (first == '{' || first == '}') return first.ToString();
            var token = new StringBuilder();
            if (first == '"')
            {
                while (position < text.Length)
                {
                    char next = text[position++];
                    if (next == '"') return token.ToString();
                    if (next == '\\' && position < text.Length && (text[position] == '\\' || text[position] == '"')) next = text[position++];
                    token.Append(next);
                }
                throw new FormatException("The Steam configuration has an incomplete string.");
            }
            token.Append(first);
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] != '{' && text[position] != '}') token.Append(text[position++]);
            return token.ToString();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeap;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder fileName, ref int size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("shell32.dll", EntryPoint = "ShellExecuteW", CharSet = CharSet.Unicode)] private static extern IntPtr ShellExecute(IntPtr window, string operation, string file, string? parameters, string? directory, int showCommand);
}
