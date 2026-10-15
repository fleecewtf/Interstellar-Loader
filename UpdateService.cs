using System.IO;

namespace Interstellar.Loader;

internal enum UpdateProduct
{
    Loader,
    Menu
}

internal sealed class UpdateCandidate
{
    public UpdateCandidate(UpdateProduct product, string version, string sha256, long length, Uri downloadUri)
    {
        Product = product;
        Version = version ?? throw new ArgumentNullException(nameof(version));
        Sha256 = sha256 ?? throw new ArgumentNullException(nameof(sha256));
        Length = length;
        DownloadUri = downloadUri ?? throw new ArgumentNullException(nameof(downloadUri));
    }

    public UpdateProduct Product { get; }
    public string Version { get; }
    public string Sha256 { get; }
    public long Length { get; }
    public Uri DownloadUri { get; }
}

internal sealed class UpdateCheckResult
{
    internal UpdateCheckResult(bool sourceConfigured, string statusText, UpdateCandidate? loader = null, UpdateCandidate? menu = null)
    {
        SourceConfigured = sourceConfigured;
        StatusText = statusText;
        LoaderUpdate = loader;
        MenuUpdate = menu;
        CheckedAtUtc = DateTime.UtcNow;
    }

    public bool SourceConfigured { get; }
    public bool LoaderUpdateAvailable => LoaderUpdate != null;
    public bool MenuUpdateAvailable => MenuUpdate != null;
    public string StatusText { get; }
    public DateTime CheckedAtUtc { get; }
    public UpdateCandidate? LoaderUpdate { get; }
    public UpdateCandidate? MenuUpdate { get; }
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
    public const string CurrentLoaderVersion = "1.0.0";
    public const string CurrentMenuVersion = "1.0.0";
    public const string UnavailableStatusText = "Updates are not connected yet.";

    private static readonly UpdateCoordinator coordinator = new UpdateCoordinator(new UnavailableUpdateSource());

    public static bool IsSourceConfigured => coordinator.IsSourceConfigured;

    public static Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        => coordinator.CheckAsync(cancellationToken);

    private sealed class UnavailableUpdateSource : IUpdateSource
    {
        public bool IsConfigured => false;
        public bool AllowsDownload(Uri uri) => false;

        public Task<IReadOnlyList<UpdateCandidate>> CheckAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException(UnavailableStatusText);
    }
}

internal sealed class UpdateCoordinator
{
    private readonly IUpdateSource source;

    internal bool IsSourceConfigured => source.IsConfigured;

    internal UpdateCoordinator(IUpdateSource source)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
    }

    internal async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
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
            if (ReadVersion(candidate.Version).CompareTo(CurrentVersion(candidate.Product)) > 0) available.Add(candidate);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return new UpdateCheckResult(true, available.Count == 0 ? "You have the latest versions." : "Updates are available.",
            available.FirstOrDefault(candidate => candidate.Product == UpdateProduct.Loader),
            available.FirstOrDefault(candidate => candidate.Product == UpdateProduct.Menu));
    }

    private void ValidateMetadata(UpdateCandidate candidate)
    {
        if (candidate.Product != UpdateProduct.Loader && candidate.Product != UpdateProduct.Menu)
            throw new InvalidDataException("The update product is invalid.");
        ReadVersion(candidate.Version);
        long maximum = candidate.Product == UpdateProduct.Loader ? 67108864 : 33554432;
        if (candidate.Length < 512 || candidate.Length > maximum) throw new InvalidDataException("The update file size is invalid.");
        if (candidate.Sha256.Length != 64 || candidate.Sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("The update file check is invalid.");
        Uri uri = candidate.DownloadUri;
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)
            || !source.AllowsDownload(uri)) throw new InvalidDataException("The update download location is not trusted.");
    }

    private static Version CurrentVersion(UpdateProduct product)
        => ReadVersion(product == UpdateProduct.Loader ? UpdateService.CurrentLoaderVersion : UpdateService.CurrentMenuVersion);

    private static Version ReadVersion(string value)
    {
        string[] parts = value.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || part.Length > 5 || part.Any(character => character < '0' || character > '9'))
            || !Version.TryParse(value, out Version? version) || version.Major > 65535 || version.Minor > 65535 || version.Build > 65535)
            throw new InvalidDataException("The update version is invalid.");
        return version;
    }
}
