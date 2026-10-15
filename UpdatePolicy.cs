namespace Interstellar.Loader;

internal static class UpdatePolicy
{
    internal static bool NeedsUpdate(UpdateCandidate? release, string installedVersion, string installedSha256)
    {
        if (release == null || !Version.TryParse(installedVersion, out Version? installed) || installed.Build < 0)
            return false;
        Version available = UpdateCoordinator.ReadVersion(release.Version);
        int comparison = available.CompareTo(new Version(installed.Major, installed.Minor, installed.Build));
        return comparison > 0 || comparison == 0 && installedSha256.Length == 64 &&
            !release.Sha256.Equals(installedSha256, StringComparison.OrdinalIgnoreCase);
    }
}
