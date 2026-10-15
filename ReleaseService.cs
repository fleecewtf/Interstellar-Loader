using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace Interstellar.Loader;

internal static class ReleaseService
{
    public const string CurrentMenuVersion = "1.0.1";
    public const string DevelopmentMessage = "Interstellar Menu 1.0.1";
    public const string MenuSha256 = "9A8941EACEB989422D072DA5B39E3E2B074B2ED54309759738E3FDEFA9BFF895";

    internal static byte[] GetMenuPayload()
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Interstellar.Loader.BetaMenu.dll")
            ?? throw new InvalidOperationException("The beta menu is missing. Download a fresh loader.");
        if (source.Length < 512 || source.Length > 16777216) throw new InvalidOperationException("The beta menu size is invalid.");
        byte[] bytes = new byte[(int)source.Length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = source.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new InvalidOperationException("The beta menu size is invalid.");
            offset += read;
        }
        using var sha = SHA256.Create();
        string hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        if (!hash.Equals(MenuSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The beta menu failed its file check. Download a fresh loader.");
        return bytes;
    }

    public static void RequireAvailable() { GetMenuPayload(); }

    public static Task InstallAsync(string? gameDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return InstallPreparedAsync(gameDirectory, cancellationToken);
    }

    private static async Task InstallPreparedAsync(string? gameDirectory, CancellationToken cancellationToken)
    {
        using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        checkTimeout.CancelAfter(TimeSpan.FromSeconds(12));
        UpdateCandidate? release = null;
        try
        {
            var result = await UpdateService.CheckAsync(checkTimeout.Token).ConfigureAwait(false);
            release = result.LatestMenuRelease;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException || error is System.Net.Http.HttpRequestException)
        {
            GameService.LogError("Check menu release", error);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (release != null)
        {
            await InstallReleaseAsync(gameDirectory, release, null, cancellationToken).ConfigureAwait(false);
            return;
        }
        byte[] payload = await Task.Run(GetMenuPayload, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await GameService.InstallBundledMenuAsync(gameDirectory, payload, MenuSha256, false, cancellationToken,
            CurrentMenuVersion, true).ConfigureAwait(false);
    }

    internal static async Task InstallReleaseAsync(string? gameDirectory, UpdateCandidate release,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (release.Product != UpdateProduct.Menu) throw new InvalidOperationException("This release is not Interstellar Menu.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var staged = await UpdateService.StageAsync(release, progress, timeout.Token).ConfigureAwait(false);
        byte[] payload = await Task.Run(() => File.ReadAllBytes(staged.FilePath), timeout.Token).ConfigureAwait(false);
        await GameService.InstallBundledMenuAsync(gameDirectory, payload, release.Sha256, false, timeout.Token,
            release.Version, true).ConfigureAwait(false);
    }
}
