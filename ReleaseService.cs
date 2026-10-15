using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace Interstellar.Loader;

internal static class ReleaseService
{
    public const string CurrentMenuVersion = "1.0.0";
    public const string DevelopmentMessage = "Interstellar Menu 1.0.0 beta";
    public const string MenuSha256 = "62C92D2E393FB81E320B518E3E59F27BD967BC74AA29D49F88BCA02366B968A7";

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
        byte[] payload = await Task.Run(GetMenuPayload, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await GameService.InstallBundledMenuAsync(gameDirectory, payload, MenuSha256, false, cancellationToken).ConfigureAwait(false);
    }
}
