using System.IO;
using System.Reflection;

namespace Interstellar.Loader;

internal enum UpdateProduct
{
    Loader,
    Menu
}

internal sealed class UpdateCandidate
{
    public UpdateCandidate(UpdateProduct product, string version, string sha256, long length, Uri downloadUri, string releaseNotes = "")
    {
        Product = product;
        Version = version ?? throw new ArgumentNullException(nameof(version));
        Sha256 = sha256 ?? throw new ArgumentNullException(nameof(sha256));
        Length = length;
        DownloadUri = downloadUri ?? throw new ArgumentNullException(nameof(downloadUri));
        ReleaseNotes = releaseNotes ?? "";
    }

    public UpdateProduct Product { get; }
    public string Version { get; }
    public string Sha256 { get; }
    public long Length { get; }
    public Uri DownloadUri { get; }
    public string ReleaseNotes { get; }
}

internal sealed class UpdateCheckResult
{
    internal UpdateCheckResult(bool sourceConfigured, string statusText, UpdateCandidate? loader = null, UpdateCandidate? menu = null,
        UpdateCandidate? latestLoader = null, UpdateCandidate? latestMenu = null)
    {
        SourceConfigured = sourceConfigured;
        StatusText = statusText;
        LoaderUpdate = loader;
        MenuUpdate = menu;
        LatestLoaderRelease = latestLoader;
        LatestMenuRelease = latestMenu;
        CheckedAtUtc = DateTime.UtcNow;
    }

    public bool SourceConfigured { get; }
    public bool LoaderUpdateAvailable => LoaderUpdate != null;
    public bool MenuUpdateAvailable => MenuUpdate != null;
    public string StatusText { get; }
    public DateTime CheckedAtUtc { get; }
    public UpdateCandidate? LoaderUpdate { get; }
    public UpdateCandidate? MenuUpdate { get; }
    public UpdateCandidate? LatestLoaderRelease { get; }
    public UpdateCandidate? LatestMenuRelease { get; }
}

internal interface IUpdateSourceStatus
{
    string StatusText { get; }
}

internal interface IUpdateTransport
{
    Task<StagedUpdate> StageAsync(UpdateCandidate candidate, IProgress<double>? progress, CancellationToken cancellationToken);
}

internal interface IUpdateSource
{
    bool IsConfigured { get; }
    bool AllowsDownload(Uri uri);
    Task<IReadOnlyList<UpdateCandidate>> CheckAsync(CancellationToken cancellationToken);
}

internal static class UpdateService
{
    public const int PollIntervalSeconds = 15;
    public static string CurrentLoaderVersion => FormatVersion(Assembly.GetExecutingAssembly().GetName().Version);
    public const string CurrentMenuVersion = "1.0.1";
    public const string UnavailableStatusText = "Updates are currently unavailable.";

    private static readonly UpdateCoordinator coordinator = new UpdateCoordinator(new GitHubReleaseSource());

    public static bool IsSourceConfigured => coordinator.IsSourceConfigured;

    public static Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default, string? currentMenuVersion = null)
        => coordinator.CheckAsync(cancellationToken, currentMenuVersion);

    public static Task<StagedUpdate> StageAsync(UpdateCandidate candidate, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        => coordinator.StageAsync(candidate, progress, cancellationToken);

    private static string FormatVersion(Version? version)
        => version == null ? "1.0.1" : version.Major + "." + version.Minor + "." + Math.Max(0, version.Build);
}

internal sealed class UpdateCoordinator
{
    private readonly IUpdateSource source;

    internal bool IsSourceConfigured => source.IsConfigured;

    internal UpdateCoordinator(IUpdateSource source)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
    }

    internal async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default, string? currentMenuVersion = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!source.IsConfigured)
        {
            return new UpdateCheckResult(false, UpdateService.UnavailableStatusText);
        }

        IReadOnlyList<UpdateCandidate> candidates = await source.CheckAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The update list is missing.");
        cancellationToken.ThrowIfCancellationRequested();
        if (candidates.Count > 2) throw new InvalidDataException("The update list contains duplicate products.");
        var products = new HashSet<UpdateProduct>();
        var available = new List<UpdateCandidate>();
        foreach (UpdateCandidate candidate in candidates)
        {
            if (candidate == null) throw new InvalidDataException("The update list contains an empty release.");
            ValidateMetadata(candidate);
            if (!products.Add(candidate.Product)) throw new InvalidDataException("The update list contains duplicate products.");
            string installed = candidate.Product == UpdateProduct.Loader ? UpdateService.CurrentLoaderVersion : currentMenuVersion ?? UpdateService.CurrentMenuVersion;
            if (ReadVersion(candidate.Version).CompareTo(ReadVersion(installed)) > 0) available.Add(candidate);
        }

        cancellationToken.ThrowIfCancellationRequested();

        string status = (source as IUpdateSourceStatus)?.StatusText ?? (available.Count == 0 ? "You have the latest versions." : "Updates are available.");
        return new UpdateCheckResult(true, status,
            available.FirstOrDefault(candidate => candidate.Product == UpdateProduct.Loader),
            available.FirstOrDefault(candidate => candidate.Product == UpdateProduct.Menu),
            candidates.FirstOrDefault(candidate => candidate.Product == UpdateProduct.Loader),
            candidates.FirstOrDefault(candidate => candidate.Product == UpdateProduct.Menu));
    }

    internal Task<StagedUpdate> StageAsync(UpdateCandidate candidate, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate == null) throw new ArgumentNullException(nameof(candidate));
        ValidateMetadata(candidate);
        if (!(source is IUpdateTransport transport)) throw new InvalidOperationException("This update source cannot download releases.");
        return transport.StageAsync(candidate, progress, cancellationToken);
    }

    private void ValidateMetadata(UpdateCandidate candidate)
    {
        UpdatePackageValidator.ValidateMetadata(candidate);
        Uri uri = candidate.DownloadUri;
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)
            || !source.AllowsDownload(uri)) throw new InvalidDataException("The update download location is not trusted.");
    }

    internal static Version ReadVersion(string value)
    {
        string[] parts = value.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || part.Length > 5 || part.Length > 1 && part[0] == '0' || part.Any(character => character < '0' || character > '9'))
            || !Version.TryParse(value, out Version? version) || version.Major > 65535 || version.Minor > 65535 || version.Build > 65535)
            throw new InvalidDataException("The update version is invalid.");
        return version;
    }
}
