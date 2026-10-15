using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Interstellar.Loader;

internal sealed class GitHubReleaseSource : IUpdateSource, IUpdateSourceStatus, IUpdateTransport, IDisposable
{
    private const int MaximumMetadataLength = 1048576;
    private const int MaximumFeedLength = 262144;
    private readonly HttpClient client;
    private readonly Func<DateTime> clock;
    private readonly string stagingRoot;
    private readonly SemaphoreSlim checkGate = new SemaphoreSlim(1, 1);
    private readonly Queue<DateTime> metadataRequests = new Queue<DateTime>();
    private readonly ReleaseState[] releases =
    {
        new ReleaseState(UpdateProduct.Loader, "Interstellar-Loader", "Interstellar Loader.exe"),
        new ReleaseState(UpdateProduct.Menu, "Interstellar", "Interstellar Menu.dll")
    };
    private DateTime metadataBackoffUntil;

    internal GitHubReleaseSource(HttpMessageHandler? handler = null, Func<DateTime>? clock = null, string? stagingRoot = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12
        }, true) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Interstellar-Loader/" + UpdateService.CurrentLoaderVersion);
        this.clock = clock ?? (() => DateTime.UtcNow);
        this.stagingRoot = stagingRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Interstellar", "Updates");
    }

    public bool IsConfigured => true;
    public string StatusText { get; private set; } = "Checking Interstellar releases.";

    public bool AllowsDownload(Uri uri)
        => releases.Any(release => IsReleaseDownload(uri, release.Repository, release.AssetName, null));

    public async Task<IReadOnlyList<UpdateCandidate>> CheckAsync(CancellationToken cancellationToken)
    {
        await checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (ReleaseState release in releases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await CheckReleaseAsync(release, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    SetFailure(release, clock(), "check timed out");
                }
            }
            StatusText = string.Join(" ", releases.Select(release => release.Status));
            return releases.Where(release => release.Candidate != null).Select(release => release.Candidate!).ToArray();
        }
        finally
        {
            checkGate.Release();
        }
    }

    private async Task CheckReleaseAsync(ReleaseState release, CancellationToken cancellationToken)
    {
        DateTime now = clock();
        try
        {
            if (now >= release.NextFeedCheck)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://github.com/fleecewtf/" + release.Repository + "/releases.atom");
                request.Headers.Accept.ParseAdd("application/atom+xml");
                if (release.FeedETag != null) request.Headers.IfNoneMatch.Add(release.FeedETag);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                release.NextFeedCheck = now.AddSeconds(UpdateService.PollIntervalSeconds);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    release.Candidate = null;
                    release.FeedFingerprint = null;
                    release.FeedETag = null;
                    release.Status = release.DisplayName + " releases are not public yet.";
                    release.NextFeedCheck = now.AddSeconds(UpdateService.PollIntervalSeconds);
                    return;
                }
                if (IsThrottled(response))
                {
                    release.NextFeedCheck = GetRetryTime(response, now, release.Failures);
                    release.Failures = Math.Min(release.Failures + 1, 8);
                    release.Candidate = null;
                    release.FeedFingerprint = null;
                    release.FeedETag = null;
                    release.Status = release.DisplayName + " checks are waiting for GitHub to allow another request.";
                    return;
                }
                if (response.StatusCode != HttpStatusCode.NotModified)
                {
                    response.EnsureSuccessStatusCode();
                    byte[] feed = await ReadBodyAsync(response, MaximumFeedLength, cancellationToken).ConfigureAwait(false);
                    string fingerprint = ReadFeedFingerprint(feed, release.Repository);
                    if (!string.Equals(fingerprint, release.FeedFingerprint, StringComparison.Ordinal))
                    {
                        release.Candidate = null;
                        release.FeedFingerprint = fingerprint;
                        release.NextMetadataCheck = DateTime.MinValue;
                    }
                    release.FeedETag = response.Headers.ETag;
                }
                else if (release.FeedFingerprint == null)
                    throw new InvalidDataException("GitHub returned an empty cached release feed.");
                release.Failures = 0;
            }
            if (release.FeedFingerprint == null) return;
            if (release.FeedFingerprint.Length == 0)
            {
                release.Candidate = null;
                release.Status = "No " + release.DisplayName.ToLowerInvariant() + " release has been published yet.";
                return;
            }
            if (release.MetadataFingerprint == release.FeedFingerprint && release.Candidate != null)
            {
                release.Status = release.DisplayName + " v" + release.Candidate.Version + " is the latest published release.";
                return;
            }
            if (now < release.NextMetadataCheck || now < metadataBackoffUntil) return;
            while (metadataRequests.Count != 0 && now - metadataRequests.Peek() >= TimeSpan.FromHours(1)) metadataRequests.Dequeue();
            if (metadataRequests.Count >= 48)
            {
                release.Status = release.DisplayName + " checks are waiting for the GitHub request limit to reset.";
                return;
            }
            metadataRequests.Enqueue(now);
            release.NextMetadataCheck = now.AddMinutes(5);
            using var metadataRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/fleecewtf/" + release.Repository + "/releases/latest");
            metadataRequest.Headers.Accept.ParseAdd("application/vnd.github+json");
            metadataRequest.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var metadataResponse = await client.SendAsync(metadataRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (IsThrottled(metadataResponse))
            {
                metadataBackoffUntil = GetRetryTime(metadataResponse, now, release.Failures);
                release.Failures = Math.Min(release.Failures + 1, 8);
                release.Candidate = null;
                release.Status = release.DisplayName + " checks are waiting for GitHub to allow another request.";
                return;
            }
            ApplyRateLimit(metadataResponse, now);
            if (metadataResponse.StatusCode == HttpStatusCode.NotFound)
            {
                release.Candidate = null;
                release.Status = "No stable " + release.DisplayName.ToLowerInvariant() + " release has been published yet.";
                return;
            }
            metadataResponse.EnsureSuccessStatusCode();
            byte[] json = await ReadBodyAsync(metadataResponse, MaximumMetadataLength, cancellationToken).ConfigureAwait(false);
            release.Candidate = ParseRelease(json, release);
            release.MetadataFingerprint = release.FeedFingerprint;
            release.Status = release.DisplayName + " v" + release.Candidate.Version + " is the latest published release.";
            release.Failures = 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SetFailure(release, now, "check timed out");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException || error is IOException || error is InvalidDataException || error is XmlException || error is SerializationException
            || error is ArgumentException || error is InvalidOperationException)
        {
            SetFailure(release, now, error is InvalidDataException || error is SerializationException || error is XmlException
                ? "release is incomplete or failed validation" : "check could not connect");
        }
    }

    private void SetFailure(ReleaseState release, DateTime now, string reason)
    {
        if (release.Candidate != null) release.NextMetadataCheck = DateTime.MinValue;
        release.Candidate = null;
        release.Status = release.DisplayName + " " + reason + ". The next check will retry.";
        release.Failures = Math.Min(release.Failures + 1, 8);
        release.NextFeedCheck = now.AddSeconds(Math.Min(300, 15 * Math.Pow(2, release.Failures - 1)));
    }

    private static string ReadFeedFingerprint(byte[] bytes, string repository)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumFeedLength,
            MaxCharactersFromEntities = 0
        });
        XDocument document = XDocument.Load(reader);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        if (document.Root?.Name != atom + "feed") throw new InvalidDataException("The release feed is invalid.");
        var entries = new List<string>();
        foreach (XElement entry in document.Root.Elements(atom + "entry").Take(100))
        {
            string? href = entry.Elements(atom + "link").FirstOrDefault(link => (string?)link.Attribute("rel") == "alternate")?.Attribute("href")?.Value;
            if (!Uri.TryCreate(href, UriKind.Absolute, out Uri? uri) || !IsTrustedHttps(uri) || uri.Host != "github.com")
                throw new InvalidDataException("The release feed contains an invalid location.");
            string prefix = "/fleecewtf/" + repository + "/releases/tag/";
            if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) || uri.Query.Length != 0)
                throw new InvalidDataException("The release feed contains an unrelated release.");
            string tag = Uri.UnescapeDataString(uri.AbsolutePath.Substring(prefix.Length));
            if (!TryReadTag(tag, out _)) continue;
            entries.Add(tag + ":" + ((string?)entry.Element(atom + "updated") ?? ""));
        }
        return string.Join("|", entries);
    }

    private static UpdateCandidate ParseRelease(byte[] bytes, ReleaseState release)
    {
        using var stream = new MemoryStream(bytes, false);
        var serializer = new DataContractJsonSerializer(typeof(ReleaseMetadata), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 8192 });
        var metadata = serializer.ReadObject(stream) as ReleaseMetadata ?? throw new InvalidDataException("The release metadata is missing.");
        if (metadata.Draft != false || metadata.Prerelease != false || metadata.Id <= 0
            || !DateTimeOffset.TryParse(metadata.PublishedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
            || !TryReadTag(metadata.Tag, out string version)) throw new InvalidDataException("The latest release is not a stable Interstellar version.");
        if (metadata.Assets?.Any(asset => asset == null) == true) throw new InvalidDataException("The release file list is invalid.");
        AssetMetadata[] assets = metadata.Assets?.Where(asset => IsAssetName(asset.Name, release.AssetName)).ToArray()
            ?? Array.Empty<AssetMetadata>();
        if (assets.Length != 1) throw new InvalidDataException("The release must contain exactly one matching Interstellar file.");
        AssetMetadata asset = assets[0];
        long maximum = release.Product == UpdateProduct.Loader ? 67108864 : 33554432;
        if (asset.Id <= 0 || asset.State != "uploaded" || asset.Size < 512 || asset.Size > maximum
            || !IsBinaryContentType(asset.ContentType)) throw new InvalidDataException("The release file metadata is invalid.");
        if (asset.Digest == null || !asset.Digest.StartsWith("sha256:", StringComparison.Ordinal) || asset.Digest.Length != 71
            || asset.Digest.Substring(7).Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("The release file is missing its SHA-256 digest.");
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out Uri? uri) || !IsReleaseDownload(uri, release.Repository, release.AssetName, metadata.Tag)
            || !string.Equals(Uri.UnescapeDataString(uri.Segments.Last()), asset.Name, StringComparison.Ordinal))
            throw new InvalidDataException("The release file location is invalid.");
        string notes = metadata.Body ?? "";
        if (notes.Length > 32768) notes = notes.Substring(0, 32768);
        return new UpdateCandidate(release.Product, version, asset.Digest.Substring(7).ToUpperInvariant(), asset.Size, uri, notes);
    }

    private static bool IsBinaryContentType(string? contentType)
        => contentType == "application/octet-stream" || contentType == "application/x-msdownload" || contentType == "application/vnd.microsoft.portable-executable"
            || contentType == "application/x-dosexec" || contentType == "application/x-ms-dos-executable";

    internal static bool TryReadTag(string? tag, out string version)
    {
        version = "";
        if (tag == null || !tag.StartsWith("v", StringComparison.Ordinal)) return false;
        try
        {
            UpdateCoordinator.ReadVersion(tag.Substring(1));
            version = tag.Substring(1);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool IsReleaseDownload(Uri uri, string repository, string assetName, string? expectedTag)
    {
        if (!IsTrustedHttps(uri) || uri.Host != "github.com" || uri.Query.Length != 0) return false;
        string[] parts = uri.AbsolutePath.Split('/');
        return parts.Length == 7 && parts[1] == "fleecewtf" && parts[2] == repository && parts[3] == "releases" && parts[4] == "download"
            && TryReadTag(parts[5], out _) && (expectedTag == null || parts[5] == expectedTag)
            && IsAssetName(Uri.UnescapeDataString(parts[6]), assetName)
            && parts[6].IndexOf("%2f", StringComparison.OrdinalIgnoreCase) < 0 && parts[6].IndexOf("%5c", StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static bool IsAssetName(string? value, string original)
        => string.Equals(value, original, StringComparison.Ordinal) || string.Equals(value, original.Replace(' ', '.'), StringComparison.Ordinal);

    private static bool IsTrustedHttps(Uri uri)
        => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private static bool IsAssetRedirect(Uri uri)
        => IsTrustedHttps(uri) && (uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com")
            && uri.AbsolutePath.StartsWith("/github-production-release-asset", StringComparison.Ordinal);

    public async Task<StagedUpdate> StageAsync(UpdateCandidate candidate, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate == null) throw new ArgumentNullException(nameof(candidate));
        ReleaseState release = releases.FirstOrDefault(state => state.Product == candidate.Product)
            ?? throw new InvalidDataException("The update product is invalid.");
        if (!IsReleaseDownload(candidate.DownloadUri, release.Repository, release.AssetName, "v" + candidate.Version))
            throw new InvalidDataException("The update download location is not trusted.");
        UpdatePackageValidator.ValidateMetadata(candidate);
        string directory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var staged = new StagedUpdate(candidate, Path.Combine(directory, release.AssetName));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            cancellationToken = timeout.Token;
            Uri uri = candidate.DownloadUri;
            progress?.Report(0);
            for (int redirects = 0; redirects <= 4; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.ParseAdd("application/octet-stream");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode >= 300 && (int)response.StatusCode <= 399)
                {
                    Uri? location = response.Headers.Location;
                    if (location == null || !location.IsAbsoluteUri || !IsAssetRedirect(location) || redirects == 4)
                        throw new InvalidDataException("GitHub redirected the update to an untrusted location.");
                    uri = location;
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("The update download response is invalid.");
                if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != candidate.Length)
                    throw new InvalidDataException("The update download size does not match the release.");
                using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(staged.FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[81920];
                    long total = 0;
                    int reportedPercent = 0;
                    while (true)
                    {
                        int read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                        if (read == 0) break;
                        total += read;
                        if (total > candidate.Length) throw new InvalidDataException("The update download exceeded its declared size.");
                        await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                        int percent = (int)(total * 95d / candidate.Length);
                        if (percent > reportedPercent)
                        {
                            reportedPercent = percent;
                            progress?.Report(percent);
                        }
                    }
                    if (total != candidate.Length) throw new InvalidDataException("The update download was incomplete.");
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Run(() => UpdatePackageValidator.ValidateFile(staged.FilePath, candidate), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(100);
                return staged;
            }
            throw new InvalidDataException("The update download redirected too many times.");
        }
        catch
        {
            staged.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, int maximum, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("The GitHub response is too large.");
        using Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximum) throw new InvalidDataException("The GitHub response is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static bool IsThrottled(HttpResponseMessage response)
        => (int)response.StatusCode == 429 || response.StatusCode == HttpStatusCode.Forbidden;

    private void ApplyRateLimit(HttpResponseMessage response, DateTime now)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? remaining) && remaining.FirstOrDefault() == "0")
            metadataBackoffUntil = GetRetryTime(response, now, 0);
    }

    private static DateTime GetRetryTime(HttpResponseMessage response, DateTime now, int failures)
    {
        DateTime retry = now.AddSeconds(Math.Min(3600, 60 * Math.Pow(2, Math.Min(6, failures))));
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta && now.Add(delta) > retry) retry = now.Add(delta);
        if (response.Headers.RetryAfter?.Date is DateTimeOffset date && date.UtcDateTime > retry) retry = date.UtcDateTime;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out IEnumerable<string>? resets)
            && long.TryParse(resets.FirstOrDefault(), out long epoch) && epoch >= 0 && epoch <= 253402300799)
        {
            DateTime reset = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(epoch).AddSeconds(1);
            if (reset > retry) retry = reset;
        }
        return retry;
    }

    public void Dispose()
    {
        client.Dispose();
        checkGate.Dispose();
    }

    private sealed class ReleaseState
    {
        internal ReleaseState(UpdateProduct product, string repository, string assetName)
        {
            Product = product;
            Repository = repository;
            AssetName = assetName;
            Status = DisplayName + " releases have not been checked yet.";
        }

        internal UpdateProduct Product { get; }
        internal string Repository { get; }
        internal string AssetName { get; }
        internal string DisplayName => Product == UpdateProduct.Loader ? "Loader" : "Menu";
        internal EntityTagHeaderValue? FeedETag { get; set; }
        internal string? FeedFingerprint { get; set; }
        internal string? MetadataFingerprint { get; set; }
        internal DateTime NextFeedCheck { get; set; }
        internal DateTime NextMetadataCheck { get; set; }
        internal int Failures { get; set; }
        internal UpdateCandidate? Candidate { get; set; }
        internal string Status { get; set; }
    }

    [DataContract]
    private sealed class ReleaseMetadata
    {
        [DataMember(Name = "id")] public long Id { get; set; }
        [DataMember(Name = "tag_name")] public string? Tag { get; set; }
        [DataMember(Name = "draft")] public bool? Draft { get; set; }
        [DataMember(Name = "prerelease")] public bool? Prerelease { get; set; }
        [DataMember(Name = "published_at")] public string? PublishedAt { get; set; }
        [DataMember(Name = "body")] public string? Body { get; set; }
        [DataMember(Name = "assets")] public AssetMetadata[]? Assets { get; set; }
    }

    [DataContract]
    private sealed class AssetMetadata
    {
        [DataMember(Name = "id")] public long Id { get; set; }
        [DataMember(Name = "name")] public string? Name { get; set; }
        [DataMember(Name = "state")] public string? State { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
        [DataMember(Name = "content_type")] public string? ContentType { get; set; }
        [DataMember(Name = "digest")] public string? Digest { get; set; }
        [DataMember(Name = "browser_download_url")] public string? DownloadUrl { get; set; }
    }
}

internal sealed class StagedUpdate : IDisposable
{
    internal StagedUpdate(UpdateCandidate candidate, string filePath)
    {
        Candidate = candidate;
        FilePath = filePath;
    }

    public UpdateCandidate Candidate { get; }
    public string FilePath { get; }

    public void Dispose()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            string? directory = Path.GetDirectoryName(FilePath);
            if (directory != null && Directory.Exists(directory)) Directory.Delete(directory, false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class UpdatePackageValidator
{
    internal static void ValidateMetadata(UpdateCandidate candidate)
    {
        if (candidate.Product != UpdateProduct.Loader && candidate.Product != UpdateProduct.Menu) throw new InvalidDataException("The update product is invalid.");
        UpdateCoordinator.ReadVersion(candidate.Version);
        long maximum = candidate.Product == UpdateProduct.Loader ? 67108864 : 33554432;
        if (candidate.Length < 512 || candidate.Length > maximum || candidate.Sha256.Length != 64 || candidate.Sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("The update file metadata is invalid.");
    }

    internal static void ValidateFile(string path, UpdateCandidate candidate)
    {
        ValidateMetadata(candidate);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length != candidate.Length) throw new InvalidDataException("The update file size does not match the release.");
            using var sha = SHA256.Create();
            string hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            if (!string.Equals(hash, candidate.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update failed its SHA-256 check.");
            stream.Position = 0;
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            if (reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException("The update is not a Windows assembly.");
            stream.Position = 0x3c;
            int header = reader.ReadInt32();
            if (header < 64 || header > stream.Length - 256) throw new InvalidDataException("The update assembly header is invalid.");
            stream.Position = header;
            if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("The update assembly header is invalid.");
            ushort machine = reader.ReadUInt16();
            ushort sections = reader.ReadUInt16();
            stream.Position = header + 20;
            ushort optionalSize = reader.ReadUInt16();
            stream.Position = header + 22;
            bool dll = (reader.ReadUInt16() & 0x2000) != 0;
            ushort format = reader.ReadUInt16();
            if (candidate.Product == UpdateProduct.Loader && (machine != 0x8664 || format != 0x20b || dll)
                || candidate.Product == UpdateProduct.Menu && ((machine != 0x14c && machine != 0x8664) || !dll || (format != 0x10b && format != 0x20b)))
                throw new InvalidDataException("The update is not compatible with this product.");
            int optionalStart = header + 24;
            int directoriesOffset = format == 0x20b ? 112 : 96;
            if (sections == 0 || sections > 96 || optionalSize < directoriesOffset + 120 || optionalStart + optionalSize + sections * 40L > stream.Length)
                throw new InvalidDataException("The update assembly sections are invalid.");
            stream.Position = optionalStart + directoriesOffset + 112;
            uint runtimeRva = reader.ReadUInt32();
            uint runtimeSize = reader.ReadUInt32();
            if (runtimeRva == 0 || runtimeSize < 72) throw new InvalidDataException("The update is not a managed assembly.");
            long runtimeOffset = -1;
            for (int section = 0; section < sections; section++)
            {
                stream.Position = optionalStart + optionalSize + section * 40L + 12;
                uint address = reader.ReadUInt32();
                uint rawSize = reader.ReadUInt32();
                uint rawOffset = reader.ReadUInt32();
                if (runtimeRva >= address && (ulong)runtimeRva + 72 <= (ulong)address + rawSize)
                {
                    runtimeOffset = rawOffset + (long)runtimeRva - address;
                    break;
                }
            }
            if (runtimeOffset < 0 || runtimeOffset > stream.Length - 72) throw new InvalidDataException("The update runtime header is invalid.");
            stream.Position = runtimeOffset + 16;
            uint runtimeFlags = reader.ReadUInt32();
            if ((runtimeFlags & 1) == 0 || (runtimeFlags & 2) != 0 || (runtimeFlags & 16) != 0)
                throw new InvalidDataException("The update assembly is not compatible with the 64-bit game.");
        }
        AssemblyName assembly;
        try
        {
            assembly = AssemblyName.GetAssemblyName(path);
        }
        catch (Exception error) when (error is BadImageFormatException || error is FileLoadException || error is IOException)
        {
            throw new InvalidDataException("The update assembly could not be validated.", error);
        }
        string expectedName = candidate.Product == UpdateProduct.Loader ? "Interstellar Loader" : "Interstellar Menu";
        Version expectedVersion = UpdateCoordinator.ReadVersion(candidate.Version);
        Version? version = assembly.Version;
        if (assembly.Name != expectedName || version == null || version.Major != expectedVersion.Major || version.Minor != expectedVersion.Minor
            || version.Build != expectedVersion.Build || version.Revision != 0)
            throw new InvalidDataException("The update assembly identity does not match the release.");
    }
}
