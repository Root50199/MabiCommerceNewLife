using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MabiCommerceNewLife;

public sealed record AppRelease(string Tag, string AssetName, Uri DownloadUrl, long Size, string Sha256);

public sealed class ReleaseVersion : IComparable<ReleaseVersion>
{
    private readonly int[] _core;
    private readonly string[] _prerelease;
    private ReleaseVersion(int[] core, string[] prerelease) => (_core, _prerelease) = (core, prerelease);

    public static ReleaseVersion Parse(string text)
    {
        var match = Regex.Match(text, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) ||
            !int.TryParse(match.Groups[2].Value, out var minor) || !int.TryParse(match.Groups[3].Value, out var patch))
            throw new InvalidDataException($"Invalid release version: {text}");
        var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (prerelease.Any(part => part.Length > 1 && part.All(char.IsAsciiDigit) && part[0] == '0'))
            throw new InvalidDataException($"Invalid numeric prerelease version: {text}");
        return new ReleaseVersion([major, minor, patch], prerelease);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < _core.Length; i++)
            if (_core[i] != other._core[i]) return _core[i].CompareTo(other._core[i]);
        if (_prerelease.Length == 0 || other._prerelease.Length == 0)
            return (other._prerelease.Length > 0).CompareTo(_prerelease.Length > 0);
        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var left = _prerelease[i];
            var right = other._prerelease[i];
            var leftNumeric = left.All(char.IsAsciiDigit);
            var rightNumeric = right.All(char.IsAsciiDigit);
            var comparison = leftNumeric && rightNumeric
                ? left.Length == right.Length ? string.CompareOrdinal(left, right) : left.Length.CompareTo(right.Length)
                : leftNumeric != rightNumeric ? leftNumeric ? -1 : 1 : string.CompareOrdinal(left, right);
            if (comparison != 0) return comparison;
        }
        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }
}

public sealed class GitHubUpdateClient : IDisposable
{
    public const string Repository = "Root50199/MabiCommerceNewLife";
    public const long MaxPackageBytes = 512L * 1024 * 1024;
    private readonly HttpClient _http;

    public GitHubUpdateClient(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MabiCommerceNewLife-Updater/1.0");
    }

    public async Task<AppRelease?> CheckAsync(string currentVersion, CancellationToken cancellationToken)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await _http.GetAsync(
            $"https://api.github.com/repos/{Repository}/releases?per_page=100",
            HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("GitHub update checks are rate limited or denied. Try again later.");
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, requestTimeout.Token);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(requestTimeout.Token));
        var current = ReleaseVersion.Parse(currentVersion);
        AppRelease? newest = null;
        ReleaseVersion? newestVersion = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            var tag = release.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("Release tag is missing.");
            var version = ReleaseVersion.Parse(tag);
            if (version.CompareTo(current) <= 0 || version.CompareTo(newestVersion) <= 0) continue;
            var name = $"MabiCommerceNewLife-{tag}-win-x64.zip";
            var assets = release.GetProperty("assets").EnumerateArray()
                .Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
            if (assets.Length != 1)
                throw new InvalidDataException($"Release {tag} has no unique Windows x64 package.");
            var asset = assets[0];
            var url = new Uri(asset.GetProperty("browser_download_url").GetString()
                ?? throw new InvalidDataException("Release download URL is missing."));
            ValidateDownloadUrl(url, initial: true);
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.GetProperty("digest").GetString() ?? string.Empty;
            if (size is <= 0 or > MaxPackageBytes || !Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$"))
                throw new InvalidDataException($"Release {tag} lacks a valid SHA-256 digest or package size.");
            newest = new AppRelease(tag, name, url, size, digest[7..]);
            newestVersion = version;
        }
        return newest;
    }

    public async Task DownloadAsync(AppRelease release, string destination, CancellationToken cancellationToken)
    {
        ValidateDownloadUrl(release.DownloadUrl, initial: true);
        if (release.Size is <= 0 or > MaxPackageBytes) throw new InvalidDataException("Invalid package size.");
        var url = release.DownloadUrl;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            ValidateDownloadUrl(url, initial: redirects == 0);
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                url = response.Headers.Location is { } location
                    ? new Uri(url, location) : throw new InvalidDataException("GitHub redirect has no destination.");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != release.Size)
                throw new InvalidDataException("Update download size does not match GitHub metadata.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += count;
                if (total > release.Size) throw new InvalidDataException("Update download exceeded expected size.");
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update package SHA-256 verification failed. Nothing was installed.");
            return;
        }
        throw new HttpRequestException("Too many GitHub download redirects.");
    }

    private static void ValidateDownloadUrl(Uri url, bool initial)
    {
        var github = url.Host == "github.com" && url.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.Ordinal);
        var assetHost = url.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
        if (url.Scheme != "https" || url.Port != 443 || url.UserInfo.Length != 0 || url.Fragment.Length != 0 ||
            !(github || (!initial && assetHost)))
            throw new InvalidDataException("Update downloads must use this repository's GitHub HTTPS release assets.");
    }

    public void Dispose() => _http.Dispose();
}
