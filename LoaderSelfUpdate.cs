using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Interstellar.Loader;

internal static class LoaderSelfUpdate
{
    private const string HelperArgument = "--interstellar-update-helper";
    private const string StartupArgument = "--interstellar-update-startup";
    private const string RecoveryArgument = "--interstellar-update-recovery";
    private const string ManifestIdentity = "Interstellar.Loader.Update.1";
    private const string AssemblyIdentity = "Interstellar Loader";
    private static readonly TimeSpan HelperReadyTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ParentExitTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    internal static Task<PreparedLoaderUpdate> PrepareAsync(string stagedPath, UpdateCandidate candidate, CancellationToken cancellationToken = default)
    {
        if (candidate == null) throw new ArgumentNullException(nameof(candidate));
        return Task.Run(() => Prepare(stagedPath, candidate, cancellationToken), cancellationToken);
    }

    internal static bool TryRunHelper(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args == null || args.Length == 0 || !string.Equals(args[0], HelperArgument, StringComparison.Ordinal)) return false;
        try
        {
            Session session = ReadSession(args, HelperArgument);
            exitCode = RunHelper(session);
        }
        catch
        {
            exitCode = 1;
        }
        return true;
    }

    internal static void MarkStartupHealthy(string[] args)
    {
        if (!IsStartupArgument(args)) return;
        try
        {
            Session session = ReadSession(args, args[0]);
            string currentPath = CurrentImagePath();
            RequireSamePath(currentPath, session.TargetPath);
            string expectedHash = args[0] == StartupArgument ? session.NewHash : session.OldHash;
            long expectedLength = args[0] == StartupArgument ? session.NewLength : session.OldLength;
            ValidateHash(currentPath, expectedHash, expectedLength);
            if (args[0] == StartupArgument)
            {
                using var healthy = EventWaitHandle.OpenExisting(session.EventName("Healthy"));
                healthy.Set();
            }
            _ = Task.Run(() => CleanupAfterHelper(session));
        }
        catch
        {
        }
    }

    internal static string? GetStartupMessage(string[] args)
    {
        if (!IsStartupArgument(args)) return null;
        try
        {
            Session session = ReadSession(args, args[0]);
            RequireSamePath(CurrentImagePath(), session.TargetPath);
            ValidateHash(session.TargetPath, args[0] == StartupArgument ? session.NewHash : session.OldHash,
                args[0] == StartupArgument ? session.NewLength : session.OldLength);
            if (args[0] == StartupArgument) return "The loader was updated to v" + session.NewVersion + ".";
            return ReadResult(session) ?? "The loader update could not start. The previous version was restored.";
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsBlocked(string sha256)
    {
        RequireHash(sha256);
        string root = UpdateRoot();
        if (!Directory.Exists(root)) return false;
        EnsureSafeDirectory(root);
        return File.Exists(Path.Combine(root, "blocked-" + sha256.ToLowerInvariant() + ".txt"));
    }

    internal static void ClearFailure(string sha256)
    {
        RequireHash(sha256);
        string root = UpdateRoot();
        if (!Directory.Exists(root)) return;
        EnsureSafeDirectory(root);
        string path = Path.Combine(root, "blocked-" + sha256.ToLowerInvariant() + ".txt");
        if (!File.Exists(path)) return;
        EnsureSafeFile(path);
        File.Delete(path);
    }

    private static PreparedLoaderUpdate Prepare(string stagedPath, UpdateCandidate candidate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate.Product != UpdateProduct.Loader) throw new InvalidDataException("The update is not a loader release.");
        RequireHash(candidate.Sha256);
        if (IsBlocked(candidate.Sha256)) throw new InvalidOperationException("This loader release could not be applied. Check for updates manually to retry it, or wait for a different release.");
        string currentPath = CurrentImagePath();
        EnsureSafeFile(currentPath);
        EnsureSafeDirectory(Path.GetDirectoryName(currentPath)!);
        string sourcePath = Path.GetFullPath(stagedPath ?? throw new ArgumentNullException(nameof(stagedPath)));
        EnsureSafeFile(sourcePath);
        if (SamePath(sourcePath, currentPath)) throw new InvalidDataException("The update must be staged separately from the running loader.");
        ValidateExecutable(sourcePath, candidate.Sha256, candidate.Length, candidate.Version);
        string oldHash = HashFile(currentPath);
        long oldLength = new FileInfo(currentPath).Length;
        Version oldVersion = ReadAssemblyVersion(currentPath);
        Version newVersion = ReadVersion(candidate.Version);
        if (newVersion.CompareTo(oldVersion) < 0) throw new InvalidDataException("An older loader version cannot replace this version.");
        if (string.Equals(oldHash, candidate.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("This loader release is already installed.");
        string root = UpdateRoot();
        Directory.CreateDirectory(root);
        EnsureSafeDirectory(root);
        string id = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(root, id);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("The update staging location is already in use.");
        Directory.CreateDirectory(directory);
        EnsureSafeDirectory(directory);
        using var current = Process.GetCurrentProcess();
        var session = new Session(id, directory, currentPath, current.Id, current.StartTime.ToUniversalTime().Ticks,
            oldHash, oldLength, oldVersion.ToString(3), candidate.Sha256.ToLowerInvariant(), candidate.Length, newVersion.ToString(3));
        try
        {
            CopyVerified(sourcePath, session.PayloadPath, session.NewHash, session.NewLength, cancellationToken);
            CopyVerified(currentPath, session.HelperPath, session.OldHash, session.OldLength, cancellationToken);
            WriteManifest(session);
            cancellationToken.ThrowIfCancellationRequested();
            return new PreparedLoaderUpdate(session);
        }
        catch
        {
            CleanupSession(session);
            throw;
        }
    }

    private static int RunHelper(Session session)
    {
        RequireSamePath(CurrentImagePath(), session.HelperPath);
        ValidateHash(session.HelperPath, session.OldHash, session.OldLength);
        ValidateExecutable(session.PayloadPath, session.NewHash, session.NewLength, session.NewVersion);
        using Process parent = OpenVerifiedParent(session);
        using var ready = EventWaitHandle.OpenExisting(session.EventName("Ready"));
        using var commit = EventWaitHandle.OpenExisting(session.EventName("Commit"));
        using var cancel = EventWaitHandle.OpenExisting(session.EventName("Cancel"));
        ready.Set();
        if (WaitHandle.WaitAny(new WaitHandle[] { commit, cancel }, HelperReadyTimeout) != 0) return 2;
        var elapsed = Stopwatch.StartNew();
        while (!parent.WaitForExit(100))
        {
            if (cancel.WaitOne(0) || elapsed.Elapsed >= ParentExitTimeout) return 2;
        }
        Process? updated = null;
        bool replaced = false;
        bool startupHealthy = false;
        try
        {
            ValidateTarget(session, false);
            ValidateExecutable(session.PayloadPath, session.NewHash, session.NewLength, session.NewVersion);
            RequireMissing(session.IncomingPath);
            RequireMissing(session.BackupPath);
            RequireMissing(session.RejectedPath);
            CopyVerified(session.PayloadPath, session.IncomingPath, session.NewHash, session.NewLength, CancellationToken.None);
            ValidateTarget(session, false);
            File.Replace(session.IncomingPath, session.TargetPath, session.BackupPath, false);
            replaced = true;
            ValidateTarget(session, true);
            using var healthy = new EventWaitHandle(false, EventResetMode.ManualReset, session.EventName("Healthy"));
            updated = StartInstalled(session, StartupArgument);
            elapsed.Restart();
            while (!healthy.WaitOne(100))
            {
                if (updated.HasExited) throw new InvalidOperationException("The updated loader closed before startup completed.");
                if (elapsed.Elapsed >= StartupTimeout) throw new TimeoutException("The updated loader did not finish starting.");
            }
            startupHealthy = true;
            WriteResult(session, "The loader was updated to v" + session.NewVersion + ".");
            TryDeleteVerifiedFile(session.BackupPath, session.OldHash, session.OldLength);
            return 0;
        }
        catch (Exception exception)
        {
            if (startupHealthy) return 0;
            string reason = FailureReason(exception);
            if (replaced)
            {
                try
                {
                    StopUnhealthyChild(updated, session);
                    ValidateTarget(session, true);
                    ValidateHash(session.BackupPath, session.OldHash, session.OldLength);
                    RequireMissing(session.RejectedPath);
                    File.Replace(session.BackupPath, session.TargetPath, session.RejectedPath, false);
                    ValidateTarget(session, false);
                    DeleteVerifiedFile(session.RejectedPath, session.NewHash, session.NewLength);
                }
                catch
                {
                    WriteResult(session, "The loader update failed and could not restore the previous version. Close the loader and reinstall it from the official release. " + reason);
                    BlockRelease(session);
                    return 3;
                }
            }
            WriteResult(session, "The loader update failed. The previous version was restored. " + reason);
            BlockRelease(session);
            try
            {
                ValidateTarget(session, false);
                using Process restored = StartInstalled(session, RecoveryArgument);
            }
            catch
            {
                WriteResult(session, "The loader update failed. Open the previous loader again to continue. " + reason);
            }
            return 1;
        }
        finally
        {
            updated?.Dispose();
            TryDeleteVerifiedFile(session.IncomingPath, session.NewHash, session.NewLength);
        }
    }

    private static Process OpenVerifiedParent(Session session)
    {
        Process parent = Process.GetProcessById(session.ParentId);
        try
        {
            if (parent.StartTime.ToUniversalTime().Ticks != session.ParentStartedUtcTicks || parent.HasExited
                || !SamePath(parent.MainModule?.FileName ?? string.Empty, session.TargetPath))
                throw new InvalidDataException("The loader process changed before the update started.");
            ValidateTarget(session, false);
            return parent;
        }
        catch
        {
            parent.Dispose();
            throw;
        }
    }

    private static Process StartInstalled(Session session, string argument)
    {
        var start = new ProcessStartInfo(session.TargetPath, argument + " " + session.Id + " " + session.ManifestHash)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(session.TargetPath)!
        };
        return Process.Start(start) ?? throw new IOException("The loader could not be restarted.");
    }

    private static void StopUnhealthyChild(Process? child, Session session)
    {
        if (child == null || child.HasExited) return;
        try
        {
            long started = child.StartTime.ToUniversalTime().Ticks;
            if (!SamePath(child.MainModule?.FileName ?? string.Empty, session.TargetPath))
                throw new InvalidDataException("The updated process identity changed.");
            child.CloseMainWindow();
            if (child.WaitForExit(5000)) return;
            if (child.StartTime.ToUniversalTime().Ticks != started || !SamePath(child.MainModule?.FileName ?? string.Empty, session.TargetPath))
                throw new InvalidDataException("The updated process identity changed.");
            ValidateTarget(session, true);
            child.Kill();
            if (!child.WaitForExit(5000)) throw new IOException("The updated loader could not be closed for recovery.");
        }
        catch (InvalidOperationException) when (child.HasExited)
        {
        }
    }

    private static Session ReadSession(string[] args, string expectedArgument)
    {
        if (args.Length != 3 || args[0] != expectedArgument || !Guid.TryParseExact(args[1], "N", out Guid parsed))
            throw new InvalidDataException("The update session is invalid.");
        RequireHash(args[2]);
        string id = parsed.ToString("N");
        string root = UpdateRoot();
        string directory = Path.Combine(root, id);
        EnsureSafeDirectory(directory);
        string manifestPath = Path.Combine(directory, "manifest.dat");
        EnsureSafeFile(manifestPath);
        var info = new FileInfo(manifestPath);
        if (info.Length < 100 || info.Length > 32768) throw new InvalidDataException("The update session is invalid.");
        ValidateHash(manifestPath, args[2], info.Length);
        using var input = new BinaryReader(new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read), Encoding.UTF8);
        if (input.ReadString() != ManifestIdentity || input.ReadString() != id) throw new InvalidDataException("The update session is invalid.");
        string target = input.ReadString();
        int parentId = input.ReadInt32();
        long parentStarted = input.ReadInt64();
        string oldHash = input.ReadString();
        long oldLength = input.ReadInt64();
        string oldVersion = input.ReadString();
        string newHash = input.ReadString();
        long newLength = input.ReadInt64();
        string newVersion = input.ReadString();
        if (input.BaseStream.Position != input.BaseStream.Length || target.Length > 4096 || !Path.IsPathRooted(target)
            || !string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase) || parentId <= 0
            || parentStarted <= 0 || oldLength < 512 || oldLength > 67108864 || newLength < 512 || newLength > 67108864)
            throw new InvalidDataException("The update session is invalid.");
        RequireHash(oldHash);
        RequireHash(newHash);
        if (ReadVersion(newVersion).CompareTo(ReadVersion(oldVersion)) < 0 || string.Equals(oldHash, newHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update session is invalid.");
        string canonicalTarget = Path.GetFullPath(target);
        if (!string.Equals(canonicalTarget, target, StringComparison.OrdinalIgnoreCase) || IsInside(canonicalTarget, root))
            throw new InvalidDataException("The update target is invalid.");
        EnsureSafeDirectory(Path.GetDirectoryName(canonicalTarget)!);
        return new Session(id, directory, canonicalTarget, parentId, parentStarted, oldHash, oldLength, oldVersion,
            newHash, newLength, newVersion) { ManifestHash = args[2].ToLowerInvariant() };
    }

    private static void WriteManifest(Session session)
    {
        using (var output = new BinaryWriter(new FileStream(session.ManifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None), Encoding.UTF8))
        {
            output.Write(ManifestIdentity);
            output.Write(session.Id);
            output.Write(session.TargetPath);
            output.Write(session.ParentId);
            output.Write(session.ParentStartedUtcTicks);
            output.Write(session.OldHash);
            output.Write(session.OldLength);
            output.Write(session.OldVersion);
            output.Write(session.NewHash);
            output.Write(session.NewLength);
            output.Write(session.NewVersion);
            output.Flush();
            ((FileStream)output.BaseStream).Flush(true);
        }
        session.ManifestHash = HashFile(session.ManifestPath);
    }

    private static void ValidateTarget(Session session, bool updated)
    {
        EnsureSafeDirectory(Path.GetDirectoryName(session.TargetPath)!);
        ValidateExecutable(session.TargetPath, updated ? session.NewHash : session.OldHash,
            updated ? session.NewLength : session.OldLength, updated ? session.NewVersion : session.OldVersion);
    }

    private static void ValidateExecutable(string path, string expectedHash, long expectedLength, string expectedVersion)
    {
        ValidateHash(path, expectedHash, expectedLength);
        Version version = ReadAssemblyVersion(path);
        if (version != ReadVersion(expectedVersion)) throw new InvalidDataException("The loader version does not match its release.");
        using var input = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), Encoding.UTF8);
        if (input.ReadUInt16() != 0x5a4d) throw new InvalidDataException("The loader executable is invalid.");
        input.BaseStream.Position = 0x3c;
        int offset = input.ReadInt32();
        if (offset < 64 || offset > expectedLength - 128) throw new InvalidDataException("The loader executable is invalid.");
        input.BaseStream.Position = offset;
        if (input.ReadUInt32() != 0x00004550 || input.ReadUInt16() != 0x8664) throw new InvalidDataException("The loader requires a Windows x64 executable.");
        input.BaseStream.Position = offset + 24;
        if (input.ReadUInt16() != 0x020b) throw new InvalidDataException("The loader executable is invalid.");
        input.BaseStream.Position = offset + 24 + 68;
        if (input.ReadUInt16() != 2) throw new InvalidDataException("The loader executable is invalid.");
        FileVersionInfo fileVersion = FileVersionInfo.GetVersionInfo(path);
        if (fileVersion.FileMajorPart != version.Major || fileVersion.FileMinorPart != version.Minor || fileVersion.FileBuildPart != version.Build)
            throw new InvalidDataException("The loader file version does not match its release.");
    }

    private static Version ReadAssemblyVersion(string path)
    {
        AssemblyName assembly = AssemblyName.GetAssemblyName(path);
        if (!string.Equals(assembly.Name, AssemblyIdentity, StringComparison.Ordinal) || assembly.Version == null || assembly.Version.Revision != 0)
            throw new InvalidDataException("The update is not an Interstellar Loader executable.");
        return new Version(assembly.Version.Major, assembly.Version.Minor, assembly.Version.Build);
    }

    private static Version ReadVersion(string text)
    {
        string[] parts = text.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || part.Length > 5 || (part.Length > 1 && part[0] == '0')
            || part.Any(character => character < '0' || character > '9'))
            || !Version.TryParse(text, out Version? version) || version.Major > 65535 || version.Minor > 65535 || version.Build > 65535)
            throw new InvalidDataException("The loader version is invalid.");
        return version;
    }

    private static void ValidateHash(string path, string expectedHash, long expectedLength)
    {
        RequireHash(expectedHash);
        EnsureSafeFile(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != expectedLength) throw new InvalidDataException("The loader update file size is incorrect.");
        using var algorithm = SHA256.Create();
        string actual = Hex(algorithm.ComputeHash(input));
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The loader update file check failed.");
    }

    private static string HashFile(string path)
    {
        EnsureSafeFile(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var algorithm = SHA256.Create();
        return Hex(algorithm.ComputeHash(input));
    }

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();

    private static void RequireHash(string value)
    {
        if (value == null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("The loader update file check is invalid.");
    }

    private static void CopyVerified(string source, string destination, string hash, long length, CancellationToken cancellationToken)
    {
        EnsureSafeDirectory(Path.GetDirectoryName(destination)!);
        RequireMissing(destination);
        EnsureSafeFile(source);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (input.Length != length) throw new InvalidDataException("The loader update file size is incorrect.");
            byte[] buffer = new byte[81920];
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, count);
            }
            output.Flush(true);
        }
        ValidateHash(destination, hash, length);
    }

    private static void EnsureSafeDirectory(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (!Directory.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The update location must be an existing regular folder.");
            DirectoryInfo? parent = Directory.GetParent(current);
            current = parent?.FullName;
        }
    }

    private static void EnsureSafeFile(string path)
    {
        EnsureSafeDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("The update location must be a regular file.");
    }

    private static void RequireMissing(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("An update file already exists at the target location.");
    }

    private static string UpdateRoot()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) throw new IOException("The update storage location is unavailable.");
        return Path.GetFullPath(Path.Combine(local, "Interstellar", "Updates"));
    }

    private static string CurrentImagePath()
    {
        using var current = Process.GetCurrentProcess();
        string path = current.MainModule?.FileName ?? throw new IOException("The running loader path is unavailable.");
        return Path.GetFullPath(path);
    }

    private static bool SamePath(string left, string right)
        => !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right) && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void RequireSamePath(string left, string right)
    {
        if (!SamePath(left, right)) throw new InvalidDataException("The loader update process does not match its session.");
    }

    private static bool IsInside(string path, string directory)
        => Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsStartupArgument(string[] args)
        => args != null && args.Length > 0 && (args[0] == StartupArgument || args[0] == RecoveryArgument);

    private static string FailureReason(Exception exception)
    {
        if (exception is UnauthorizedAccessException) return "Windows denied permission to replace the loader.";
        if (exception is TimeoutException) return "The updated loader did not finish starting.";
        if (exception is InvalidDataException || exception is BadImageFormatException) return "The update failed its file checks.";
        if (exception is IOException) return "Windows could not replace or restart the loader.";
        return "The updated loader did not start successfully.";
    }

    private static void WriteResult(Session session, string text)
    {
        EnsureSafeDirectory(session.DirectoryPath);
        if (File.Exists(session.ResultPath)) EnsureSafeFile(session.ResultPath);
        File.WriteAllText(session.ResultPath, text, new UTF8Encoding(false));
    }

    private static string? ReadResult(Session session)
    {
        if (!File.Exists(session.ResultPath)) return null;
        EnsureSafeFile(session.ResultPath);
        if (new FileInfo(session.ResultPath).Length > 4096) throw new InvalidDataException("The update result is invalid.");
        return File.ReadAllText(session.ResultPath, Encoding.UTF8);
    }

    private static void BlockRelease(Session session)
    {
        try
        {
            string root = UpdateRoot();
            EnsureSafeDirectory(root);
            string path = Path.Combine(root, "blocked-" + session.NewHash + ".txt");
            if (File.Exists(path)) EnsureSafeFile(path);
            File.WriteAllText(path, "The loader could not apply this release.", new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    private static void CleanupAfterHelper(Session session)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(30))
        {
            try
            {
                if (File.Exists(session.ResultPath))
                {
                    using (new FileStream(session.HelperPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                    }
                    CleanupSession(session);
                    return;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            Thread.Sleep(250);
        }
    }

    private static void CleanupSession(Session session)
    {
        try
        {
            if (!Directory.Exists(session.DirectoryPath)) return;
            EnsureSafeDirectory(session.DirectoryPath);
            RequireSamePath(session.DirectoryPath, Path.Combine(UpdateRoot(), session.Id));
            foreach (string name in new[] { "manifest.dat", "payload.exe", "updater.exe", "result.txt" })
            {
                string path = Path.Combine(session.DirectoryPath, name);
                if (!File.Exists(path)) continue;
                EnsureSafeFile(path);
                File.Delete(path);
            }
            Directory.Delete(session.DirectoryPath, false);
        }
        catch
        {
        }
    }

    private static void DeleteVerifiedFile(string path, string hash, long length)
    {
        if (!File.Exists(path)) return;
        ValidateHash(path, hash, length);
        File.Delete(path);
    }

    private static void TryDeleteVerifiedFile(string path, string hash, long length)
    {
        try
        {
            DeleteVerifiedFile(path, hash, length);
        }
        catch
        {
        }
    }

    internal sealed class PreparedLoaderUpdate : IDisposable
    {
        private readonly Session session;
        private int launched;
        private int disposed;

        internal PreparedLoaderUpdate(Session session)
        {
            this.session = session;
        }

        internal Task LaunchAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(PreparedLoaderUpdate));
            if (Interlocked.CompareExchange(ref launched, 1, 0) != 0) throw new InvalidOperationException("The loader update has already been started.");
            return Task.Run(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateTarget(session, false);
                    ValidateExecutable(session.PayloadPath, session.NewHash, session.NewLength, session.NewVersion);
                    using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, session.EventName("Ready"));
                    using var commit = new EventWaitHandle(false, EventResetMode.ManualReset, session.EventName("Commit"));
                    using var cancel = new EventWaitHandle(false, EventResetMode.ManualReset, session.EventName("Cancel"));
                    Process? helper = null;
                    bool committed = false;
                    try
                    {
                        ValidateHash(session.HelperPath, session.OldHash, session.OldLength);
                        helper = Process.Start(new ProcessStartInfo(session.HelperPath, HelperArgument + " " + session.Id + " " + session.ManifestHash)
                        {
                            UseShellExecute = false,
                            WorkingDirectory = session.DirectoryPath
                        }) ?? throw new IOException("The loader update helper could not be started.");
                        var elapsed = Stopwatch.StartNew();
                        while (!ready.WaitOne(50))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (helper.HasExited) throw new IOException("The loader update helper could not prepare the update.");
                            if (elapsed.Elapsed >= HelperReadyTimeout) throw new TimeoutException("The loader update helper did not respond.");
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        commit.Set();
                        committed = true;
                    }
                    finally
                    {
                        if (!committed)
                        {
                            cancel.Set();
                            if (helper != null && !helper.HasExited) helper.WaitForExit(2000);
                            Volatile.Write(ref launched, 0);
                        }
                        helper?.Dispose();
                    }
                }
                catch
                {
                    Volatile.Write(ref launched, 0);
                    throw;
                }
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            if (Volatile.Read(ref launched) == 0) CleanupSession(session);
        }
    }

    internal sealed class Session
    {
        internal Session(string id, string directoryPath, string targetPath, int parentId, long parentStartedUtcTicks,
            string oldHash, long oldLength, string oldVersion, string newHash, long newLength, string newVersion)
        {
            Id = id;
            DirectoryPath = directoryPath;
            TargetPath = targetPath;
            ParentId = parentId;
            ParentStartedUtcTicks = parentStartedUtcTicks;
            OldHash = oldHash;
            OldLength = oldLength;
            OldVersion = oldVersion;
            NewHash = newHash;
            NewLength = newLength;
            NewVersion = newVersion;
        }

        internal string Id { get; }
        internal string DirectoryPath { get; }
        internal string TargetPath { get; }
        internal int ParentId { get; }
        internal long ParentStartedUtcTicks { get; }
        internal string OldHash { get; }
        internal long OldLength { get; }
        internal string OldVersion { get; }
        internal string NewHash { get; }
        internal long NewLength { get; }
        internal string NewVersion { get; }
        internal string ManifestHash { get; set; } = string.Empty;
        internal string HelperPath => Path.Combine(DirectoryPath, "updater.exe");
        internal string PayloadPath => Path.Combine(DirectoryPath, "payload.exe");
        internal string ManifestPath => Path.Combine(DirectoryPath, "manifest.dat");
        internal string ResultPath => Path.Combine(DirectoryPath, "result.txt");
        internal string IncomingPath => Path.Combine(Path.GetDirectoryName(TargetPath)!, ".interstellar-update-" + Id + ".exe");
        internal string BackupPath => Path.Combine(Path.GetDirectoryName(TargetPath)!, ".interstellar-rollback-" + Id + ".exe");
        internal string RejectedPath => Path.Combine(Path.GetDirectoryName(TargetPath)!, ".interstellar-rejected-" + Id + ".exe");
        internal string EventName(string purpose) => "Local\\Interstellar.Loader.Update." + Id + "." + purpose;
    }
}
