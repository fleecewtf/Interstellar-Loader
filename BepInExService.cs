using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Xml;

namespace Interstellar.Loader;

internal static class BepInExService
{
    public const string PackageVersion = "5.4.23.5";
    public const string PackageUrl = "https://api.github.com/repos/BepInEx/BepInEx/releases/assets/352395699";
    public const string PackageSha256 = "82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4";
    private const string ReceiptRelative = ".interstellar-loader/bepinex.xml";
    private const string HistoryRelative = ".interstellar-loader/framework-history";
    private const int MaximumZipBytes = 2 * 1024 * 1024;
    private const int MaximumExpandedBytes = 20 * 1024 * 1024;
    private static readonly Version SupportedVersion = new Version(PackageVersion);
    private static readonly Dictionary<string, string> OfficialFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".doorstop_version"] = "75A2F501000D4FE28F74BC7EE66DAE5581959D28B6C44F0C1B0C05CD5BA261E6",
        ["doorstop_config.ini"] = "4D5C6DFA0F771C6A5B1B0C559ACA0BD0ECE7D08B08FFF894708DC3B73CE73CFC",
        ["winhttp.dll"] = "8C6CDBC38836DEE87E3368F5DE1994D7C0CCEBF29E4CE7ABA3C0981F9375412C",
        ["BepInEx/core/0Harmony.dll"] = "1A21CC03424FC82C3DD1346905D16494536B9595AE4162228D99FB7C285C1031",
        ["BepInEx/core/0Harmony20.dll"] = "C755A5AB2712915A32F9A850D580A8C1B70FF1653BC76600F2767AA8FFCCC663",
        ["BepInEx/core/BepInEx.dll"] = "8255B28902886085C578B9E427D3073C97002DB85176D2090CDEDA90EF14CE70",
        ["BepInEx/core/BepInEx.Harmony.dll"] = "00C8F2AC48593E4D45CE4EB26CE4E68B18113C5C489B38B8BF0850F65830D601",
        ["BepInEx/core/BepInEx.Preloader.dll"] = "55D3895351A9D16B63B6F35F1C01B44AC650979E853D0BD3A442B92A082AF64F",
        ["BepInEx/core/HarmonyXInterop.dll"] = "7CE1342D3AFA0334B59A3E38C0AED15E162B9ABE9D46F95F34BE44AF47F3B493",
        ["BepInEx/core/Mono.Cecil.dll"] = "7AE470288FFF4A402899C254D0A76CEFEF55877F5C54F96E83C797CC5BB6E2F6",
        ["BepInEx/core/Mono.Cecil.Mdb.dll"] = "5896D1898F616701FFF18F3B2C71E6B844D2390EF9F41E1C5FCCCE8CB27C698E",
        ["BepInEx/core/Mono.Cecil.Pdb.dll"] = "174DB44A067F58561510AF746F3CAEB032037762C57A31C8D9EE32DB25174984",
        ["BepInEx/core/Mono.Cecil.Rocks.dll"] = "54AC539FB5DDC8B44C0E9ACD0FCB7324F89D1A072EDF8EBC1B06DD691E3D3927",
        ["BepInEx/core/MonoMod.RuntimeDetour.dll"] = "40E49BB314391CD7BDDC2644F8553EEBA92C194B940836B103DF16955C464E0C",
        ["BepInEx/core/MonoMod.Utils.dll"] = "9D1495F147AC93C4F81F84538C1A326E8F8A6AEFC78D6289D798F3CE1162C5E9"
    };
    private static readonly HashSet<string> KnownProxies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CF9DD372CA0DDBE01153502C49F8F756197BB260001792FE766F6C0242DC7FC0",
        "D3B62F4C9C3E2196EF82603C52D6B98C043A0D6C125C081FD33D7CA3798B41B8",
        "93406D0A02E7C164B89828CBFE3B289930A112D2ECA50BD4A52E72ECE169E6A8",
        "C8D4F45CA81979ACDA889AAA1B4334E010B70E4C3960B15D323D2829AB424257",
        "8C6CDBC38836DEE87E3368F5DE1994D7C0CCEBF29E4CE7ABA3C0981F9375412C"
    };

    public static Task InstallAsync(string? gameDirectory, CancellationToken cancellationToken = default) =>
        InstallOrRepairAsync(gameDirectory, false, cancellationToken);

    public static Task RepairAsync(string? gameDirectory, CancellationToken cancellationToken = default) =>
        InstallOrRepairAsync(gameDirectory, true, cancellationToken);

    private static async Task InstallOrRepairAsync(string? gameDirectory, bool repair, CancellationToken cancellationToken)
    {
        lock (GameService.ManagedFilesLock)
        {
            string directory = GameService.RequireInstalled(gameDirectory);
            GameService.RequireGameClosed();
            ValidateExisting(directory, repair);
            EnsureNoIncompleteTransactions(directory);
        }
        byte[] archive = await DownloadPackageAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, byte[]> package = ReadPackage(archive, true);
        await Task.Run(() =>
        {
            lock (GameService.ManagedFilesLock)
            {
                string directory = GameService.RequireInstalled(gameDirectory);
                GameService.RequireGameClosed();
                ValidateExisting(directory, repair);
                EnsureNoIncompleteTransactions(directory);
                cancellationToken.ThrowIfCancellationRequested();
                RunTransaction(directory, package, repair ? "Repair" : "Install", cancellationToken);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public static Task UninstallAsync(string? gameDirectory, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        lock (GameService.ManagedFilesLock)
        {
            string directory = GameService.RequireInstalled(gameDirectory);
            GameService.RequireGameClosed();
            ReadReceipt(directory);
            EnsureNoIncompleteTransactions(directory);
            foreach (var file in OfficialFiles)
            {
                string path = GameService.SafeOwnedPath(directory, file.Key);
                if (File.Exists(path) && !HashFile(path).Equals(file.Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A framework file has changed. Repair BepInEx before uninstalling it. Plugins and settings have been left alone.");
            }
            RunTransaction(directory, null, "Uninstall", cancellationToken);
        }
    }, cancellationToken);

    private static void ValidateExisting(string directory, bool repair)
    {
        string receipt = GameService.SafeOwnedPath(directory, ReceiptRelative);
        bool managed = File.Exists(receipt);
        if (managed) ReadReceipt(directory);
        foreach (string foreign in new[] { "BepInEx/core/BepInEx.Core.dll", "BepInEx/core/BepInEx.Unity.IL2CPP.dll", "BepInEx/core/BepInEx.Unity.Mono.dll", "MelonLoader/MelonLoader.dll" })
            if (File.Exists(GameService.SafeOwnedPath(directory, foreign)))
                throw new InvalidOperationException("A different mod framework was found. Manage it separately before installing BepInEx 5.");
        string core = GameService.SafeOwnedPath(directory, "BepInEx/core/BepInEx.dll");
        if (File.Exists(core))
        {
            AssemblyName? identity = null;
            try { identity = AssemblyName.GetAssemblyName(core); }
            catch (BadImageFormatException) when (managed && repair) { }
            if (identity != null && (identity.Name != "BepInEx" || identity.Version == null || identity.Version.Major != 5 || identity.Version > SupportedVersion))
                throw new InvalidOperationException("Only BepInEx 5 at or below " + PackageVersion + " can be repaired. Other frameworks and newer versions are left alone.");
            if (!repair) throw new InvalidOperationException("BepInEx is already installed. Use Repair BepInEx to verify and update its framework files.");
        }
        string proxy = GameService.SafeOwnedPath(directory, "winhttp.dll");
        if (File.Exists(proxy) && !KnownProxies.Contains(HashFile(proxy)))
            throw new InvalidOperationException("An unrecognized winhttp.dll is present. It may belong to another mod loader. Remove or manage it separately; Interstellar will not overwrite it.");
        string doorstop = GameService.SafeOwnedPath(directory, "doorstop_config.ini");
        if (File.Exists(doorstop) && !managed)
        {
            if (new FileInfo(doorstop).Length > 65536 || File.ReadAllText(doorstop).IndexOf("BepInEx.Preloader.dll", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("The existing Doorstop configuration does not identify BepInEx. It has been left alone.");
        }
        if (!repair && OfficialFiles.Keys.Any(relative => File.Exists(GameService.SafeOwnedPath(directory, relative))))
            throw new InvalidOperationException("Existing framework files were found. Use Repair BepInEx instead of installing over them.");
    }

    private static async Task<byte[]> DownloadPackageAsync(CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Interstellar-Loader/1.0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        Uri location = new Uri(PackageUrl);
        for (int redirects = 0; redirects < 5; redirects++)
        {
            if (location.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(location.UserInfo) ||
                (location.Host != "api.github.com" && location.Host != "github.com" && location.Host != "release-assets.githubusercontent.com" && location.Host != "objects.githubusercontent.com"))
                throw new InvalidOperationException("GitHub redirected the framework download to an unexpected location.");
            using var response = await client.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
            {
                if (response.Headers.Location == null) throw new InvalidOperationException("GitHub returned an invalid download redirect.");
                location = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(location, response.Headers.Location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumZipBytes) throw new InvalidOperationException("The BepInEx download is larger than expected.");
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > MaximumZipBytes) throw new InvalidOperationException("The BepInEx download exceeded its size limit.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        throw new InvalidOperationException("GitHub returned too many download redirects. Try again later.");
    }

    private static Dictionary<string, byte[]> ReadPackage(byte[] bytes, bool verifyDigest)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumZipBytes || (verifyDigest && Hash(bytes) != PackageSha256))
            throw new InvalidOperationException("The BepInEx package failed its official SHA256 check. No game files have been changed.");
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var memory = new MemoryStream(bytes, false);
        using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
        if (zip.Entries.Count > 64) throw new InvalidOperationException("The framework package contains too many entries.");
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            string relative = entry.FullName;
            bool directory = relative.EndsWith("/", StringComparison.Ordinal);
            string check = directory ? relative.TrimEnd('/') : relative;
            if (check.Length == 0 || check.IndexOf('\\') >= 0 || check.IndexOf(':') >= 0 || check.StartsWith("/", StringComparison.Ordinal) ||
                check.Split('/').Any(part => part.Length == 0 || part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) || !seen.Add(relative))
                throw new InvalidOperationException("The framework package contains an unsafe or duplicate path.");
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000) throw new InvalidOperationException("The framework package contains a linked file.");
            expanded += entry.Length;
            if (entry.Length < 0 || entry.Length > 4 * 1024 * 1024 || expanded > MaximumExpandedBytes)
                throw new InvalidOperationException("The framework package exceeds its extraction limit.");
            if (directory) continue;
            bool allowed = OfficialFiles.ContainsKey(relative);
            bool documentation = relative == "changelog.txt" || (relative.StartsWith("BepInEx/core/", StringComparison.Ordinal) && relative.EndsWith(".xml", StringComparison.Ordinal));
            if (!allowed && !documentation) throw new InvalidOperationException("The framework package contains an unexpected file.");
            if (!allowed) continue;
            using var input = entry.Open();
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > entry.Length) throw new InvalidOperationException("The framework entry exceeded its declared size.");
                output.Write(buffer, 0, read);
            }
            byte[] content = output.ToArray();
            if (content.LongLength != entry.Length || Hash(content) != OfficialFiles[relative])
                throw new InvalidOperationException("A BepInEx framework file failed its official hash check.");
            files.Add(relative, content);
        }
        if (files.Count != OfficialFiles.Count) throw new InvalidOperationException("The BepInEx package is incomplete.");
        return files;
    }

    private static void ReadReceipt(string directory)
    {
        string path = GameService.SafeOwnedPath(directory, ReceiptRelative);
        if (!File.Exists(path)) throw new InvalidOperationException("BepInEx was not installed by this loader. Use Repair BepInEx first to verify and back up its framework files.");
        var document = ReadXml(path, 65536);
        var root = document.DocumentElement;
        if (root == null || root.Name != "Framework" || root.GetAttribute("product") != "BepInEx" || root.GetAttribute("version") != PackageVersion ||
            !Path.GetFullPath(root.GetAttribute("gameDirectory")).TrimEnd('\\', '/').Equals(directory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The framework ownership record is invalid. Game files have been left alone.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (XmlNode node in root.ChildNodes)
        {
            if (!(node is XmlElement file)) continue;
            string relative = file.GetAttribute("path");
            if (file.Name != "File" || !OfficialFiles.TryGetValue(relative, out var hash) || file.GetAttribute("sha256") != hash || !names.Add(relative))
                throw new InvalidOperationException("The framework ownership record contains an unexpected file.");
            GameService.SafeOwnedPath(directory, relative);
        }
        if (names.Count != OfficialFiles.Count) throw new InvalidOperationException("The framework ownership record is incomplete.");
    }

    private static XmlDocument ReadXml(string path, int maximum)
    {
        if (new FileInfo(path).Length > maximum) throw new InvalidOperationException("A framework record is too large.");
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximum });
        var document = new XmlDocument { XmlResolver = null };
        document.Load(reader);
        return document;
    }

    private static void EnsureNoIncompleteTransactions(string directory)
    {
        string history = GameService.SafeOwnedPath(directory, HistoryRelative);
        if (!Directory.Exists(history)) return;
        int count = 0;
        foreach (string entry in Directory.EnumerateDirectories(history))
        {
            if (++count > 1024) throw new InvalidOperationException("Framework history needs inspection before further changes.");
            string relative = HistoryRelative + "/" + Path.GetFileName(entry) + "/journal.xml";
            string journal = GameService.SafeOwnedPath(directory, relative);
            if (!File.Exists(journal)) throw new InvalidOperationException("A framework operation was interrupted. Inspect the framework history before making more changes.");
            string state = ReadXml(journal, 262144).DocumentElement?.GetAttribute("state") ?? "";
            if (state != "Committed" && state != "Restored")
                throw new InvalidOperationException("A framework operation needs recovery. Original framework backups were kept in .interstellar-loader/framework-history. Game files have been left alone.");
        }
    }

    private sealed class Original
    {
        internal string Relative = "";
        internal bool Exists;
        internal string Hash = "";
        internal string BackupRelative = "";
    }

    private static void RunTransaction(string directory, Dictionary<string, byte[]>? package, string operation, CancellationToken cancellationToken)
    {
        string history = HistoryRelative + "/" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(GameService.SafeOwnedPath(directory, history));
        var originals = new List<Original>();
        var touched = new List<Original>();
        byte[]? receiptBytes = package == null ? null : CreateReceipt(directory);
        foreach (string relative in OfficialFiles.Keys.Concat(new[] { ReceiptRelative }))
        {
            string path = GameService.SafeOwnedPath(directory, relative);
            originals.Add(new Original { Relative = relative, Exists = File.Exists(path), Hash = File.Exists(path) ? HashFile(path) : "", BackupRelative = history + "/original/" + originals.Count + ".bin" });
        }
        void Journal(string state)
        {
            var document = new XmlDocument();
            var root = document.CreateElement("FrameworkTransaction");
            root.SetAttribute("operation", operation);
            root.SetAttribute("state", state);
            document.AppendChild(root);
            foreach (var original in originals)
            {
                var file = document.CreateElement("File");
                file.SetAttribute("path", original.Relative);
                file.SetAttribute("existed", original.Exists ? "true" : "false");
                file.SetAttribute("sha256", original.Hash);
                file.SetAttribute("backup", original.BackupRelative);
                root.AppendChild(file);
            }
            WriteAtomic(directory, history + "/journal.xml", XmlBytes(document));
        }
        try
        {
            Journal("Preparing");
            foreach (var original in originals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!original.Exists) continue;
                string source = GameService.SafeOwnedPath(directory, original.Relative);
                if (new FileInfo(source).Length > MaximumExpandedBytes) throw new InvalidOperationException("An existing framework file is too large to back up safely.");
                byte[] bytes = File.ReadAllBytes(source);
                if (Hash(bytes) != original.Hash) throw new InvalidOperationException("Framework files changed during preparation. Try again with other file tools closed.");
                WriteAtomic(directory, original.BackupRelative, bytes);
            }
            Journal("Prepared");
            foreach (var original in originals.Where(file => file.Relative != ReceiptRelative))
            {
                cancellationToken.ThrowIfCancellationRequested();
                VerifyOriginal(directory, original);
                touched.Add(original);
                if (package == null) File.Delete(GameService.SafeOwnedPath(directory, original.Relative));
                else WriteAtomic(directory, original.Relative, package[original.Relative]);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = originals.First(file => file.Relative == ReceiptRelative);
            VerifyOriginal(directory, receipt);
            touched.Add(receipt);
            if (package == null) File.Delete(GameService.SafeOwnedPath(directory, ReceiptRelative));
            else WriteAtomic(directory, ReceiptRelative, receiptBytes!);

            Journal("Committed");
        }
        catch (Exception operationError)
        {
            var recoveryErrors = new List<string>();
            foreach (var original in touched.AsEnumerable().Reverse())
            {
                try
                {
                    string target = GameService.SafeOwnedPath(directory, original.Relative);
                    if (File.Exists(target))
                    {
                        string currentHash = HashFile(target);
                        if (original.Exists && currentHash == original.Hash) continue;
                        string expectedApplied = package == null ? "" : original.Relative == ReceiptRelative ? Hash(receiptBytes!) : OfficialFiles[original.Relative];
                        if (currentHash != expectedApplied)
                            throw new IOException("Another program changed a framework file during recovery. It has been left alone.");
                    }
                    else if (!original.Exists) continue;
                    if (original.Exists)
                    {
                        string backup = GameService.SafeOwnedPath(directory, original.BackupRelative);
                        if (!File.Exists(backup) || HashFile(backup) != original.Hash) throw new IOException("Recovery copy failed its hash check.");
                        WriteAtomic(directory, original.Relative, File.ReadAllBytes(backup));
                    }
                    else File.Delete(target);
                }
                catch (Exception error) when (IsFileFailure(error)) { recoveryErrors.Add(original.Relative); }
            }
            try { Journal(recoveryErrors.Count == 0 ? "Restored" : "RecoveryRequired"); }
            catch (Exception error) when (IsFileFailure(error)) { recoveryErrors.Add("journal"); }
            if (recoveryErrors.Count > 0)
                throw new InvalidOperationException("Framework changes could not be fully restored. Keep .interstellar-loader/framework-history for recovery and close apps locking game files. Plugins and configuration were not part of the operation.", operationError);
            throw;
        }
    }

    private static void VerifyOriginal(string directory, Original original)
    {
        string path = GameService.SafeOwnedPath(directory, original.Relative);
        if (File.Exists(path) != original.Exists || (original.Exists && HashFile(path) != original.Hash))
            throw new InvalidOperationException("Framework files changed during the operation. Existing copies will be restored.");
    }

    private static byte[] CreateReceipt(string directory)
    {
        var document = new XmlDocument();
        var root = document.CreateElement("Framework");
        root.SetAttribute("product", "BepInEx");
        root.SetAttribute("version", PackageVersion);
        root.SetAttribute("gameDirectory", directory);
        document.AppendChild(root);
        foreach (var pair in OfficialFiles)
        {
            var file = document.CreateElement("File");
            file.SetAttribute("path", pair.Key);
            file.SetAttribute("sha256", pair.Value);
            root.AppendChild(file);
        }
        return XmlBytes(document);
    }

    private static byte[] XmlBytes(XmlDocument document)
    {
        using var memory = new MemoryStream();
        document.Save(memory);
        return memory.ToArray();
    }

    private static void WriteAtomic(string directory, string relative, byte[] bytes)
    {
        string path = GameService.SafeOwnedPath(directory, relative);
        string parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        int slash = relative.LastIndexOf('/');
        string temporaryRelative = (slash < 0 ? "" : relative.Substring(0, slash + 1)) + Guid.NewGuid().ToString("N") + ".tmp";
        string temporary = GameService.SafeOwnedPath(directory, temporaryRelative);
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes, 0, bytes.Length); file.Flush(true); }

            using (var sealedFile = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var sha = SHA256.Create())
            {
                if (BitConverter.ToString(sha.ComputeHash(sealedFile)).Replace("-", "") != Hash(bytes))
                    throw new InvalidOperationException("A prepared framework file changed before activation.");
                GameService.SafeOwnedPath(directory, relative);
                if (!MoveFileEx(ExtendedPath(temporary), ExtendedPath(path), 0x1 | 0x8))
                    throw new IOException("A prepared framework file could not be activated.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }
    private static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }
    private static bool IsFileFailure(Exception error) => error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException || error is ArgumentException || error is BadImageFormatException || error is XmlException || error is System.Security.SecurityException;

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingName, string newName, uint flags);
    private static string ExtendedPath(string path) => path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path :
        path.StartsWith("\\\\", StringComparison.Ordinal) ? "\\\\?\\UNC\\" + path.Substring(2) : "\\\\?\\" + path;
}
