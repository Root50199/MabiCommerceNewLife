using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class AppUpdateTests
{
    [TestMethod]
    [DataRow("0.1.3-beta", "0.1.3", -1)]
    [DataRow("0.1.3-beta.9", "0.1.3-beta.10", -1)]
    [DataRow("0.1.3-beta", "0.1.4-alpha", -1)]
    [DataRow("0.2.0", "0.1.99", 1)]
    [DataRow("v1.0.0+build", "1.0.0", 0)]
    [DataRow("1.0.0-1", "1.0.0-alpha", -1)]
    [DataRow("1.0.0-beta.2", "1.0.0-beta", 1)]
    public void VersionsUseNumericCoreAndSemverPrereleaseOrder(string left, string right, int expected) =>
        Assert.AreEqual(expected, Math.Sign(ReleaseVersion.Parse(left).CompareTo(ReleaseVersion.Parse(right))));

    [TestMethod]
    [DataRow("latest")]
    [DataRow("1.0")]
    [DataRow("01.0.0")]
    [DataRow("1.0.0-beta.01")]
    [DataRow("1.0.0+build..1")]
    public void InvalidVersionsAreExplicitErrors(string text) =>
        Assert.ThrowsException<InvalidDataException>(() => ReleaseVersion.Parse(text));

    [TestMethod]
    public async Task ChecksIncludeBetaIgnoreDraftsAndSelectHighestVersionNotListOrder()
    {
        using var client = Client([
            Release("v0.1.5-beta.2"), Release("v0.1.4"), Release("v0.1.6", draft: true), Release("v0.1.5-beta.10")
        ]);
        var update = await client.CheckAsync("0.1.3-beta", default);
        Assert.AreEqual("v0.1.5-beta.10", update?.Tag);
        using var same = Client([Release("v0.1.3-beta"), Release("v0.1.2")]);
        Assert.IsNull(await same.CheckAsync("0.1.3-beta", default), "Never downgrade or reinstall same version.");
    }

    [TestMethod]
    public async Task UntrustedAssetsMissingHashAndRateLimitsSurfaceErrors()
    {
        using var foreign = Client([Release("v0.2.0", url: "https://example.com/update.zip")]);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => foreign.CheckAsync("0.1.3", default));
        using var missing = Client([Release("v0.2.0", digest: "")]);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => missing.CheckAsync("0.1.3", default));
        using var limited = new GitHubUpdateClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var error = await Assert.ThrowsExceptionAsync<HttpRequestException>(() => limited.CheckAsync("0.1.3", default));
        StringAssert.Contains(error.Message, "rate limited");
    }

    [TestMethod]
    public async Task DownloadRequiresExactSizeDigestAndApprovedRedirectHost()
    {
        var bytes = Encoding.ASCII.GetBytes("verified release bytes");
        var release = new AppRelease("v0.2.0", "app.zip",
            new Uri("https://github.com/Root50199/MabiCommerceNewLife/releases/download/v0.2.0/app.zip"),
            bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
        var directory = NewDirectory();
        try
        {
            using var client = new GitHubUpdateClient(new Handler(request =>
            {
                if (request.RequestUri!.Host == "github.com")
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri("https://release-assets.githubusercontent.com/asset?signature=test");
                    return redirect;
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }));
            await client.DownloadAsync(release, Path.Combine(directory, "good.zip"), default);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(directory, "good.zip")));
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
                client.DownloadAsync(release with { Sha256 = new string('0', 64) }, Path.Combine(directory, "bad.zip"), default));
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
                client.DownloadAsync(release with { Size = bytes.Length + 1 }, Path.Combine(directory, "short.zip"), default));
            using var untrusted = new GitHubUpdateClient(new Handler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://example.com/download");
                return response;
            }));
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
                untrusted.DownloadAsync(release, Path.Combine(directory, "foreign.zip"), default));
            Assert.IsFalse(File.Exists(Path.Combine(directory, "foreign.zip")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void PackagesExtractOnlyReleaseOwnedFilesAndPreserveUserFiles()
    {
        var directory = NewDirectory();
        try
        {
            var archive = Package(directory, "valid.zip");
            var files = AppUpdateInstaller.ExtractPackage(archive, Path.Combine(directory, "payload"));
            Assert.AreEqual(6, files.Count);
            var target = Path.Combine(directory, "app");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, AppUpdateInstaller.ExecutableName), "original app");
            File.WriteAllText(Path.Combine(target, "config.json"), "owner settings");
            AppUpdateInstaller.ValidateTarget(target, files);
            Assert.AreEqual("owner settings", File.ReadAllText(Path.Combine(target, "config.json")));
            File.WriteAllText(Path.Combine(target, "MabiCommerceNewLife.csproj"), "source");
            Assert.ThrowsException<IOException>(() => AppUpdateInstaller.ValidateTarget(target, files));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DataRow(@"..\outside.exe")]
    [DataRow(@"Data\..\config.json")]
    [DataRow("config.json")]
    [DataRow("community-credentials.json")]
    [DataRow("trade-history.json")]
    [DataRow(@"Data\file:stream")]
    [DataRow(@"Data\bad.\file")]
    [DataRow(@"C:\outside.exe")]
    [DataRow(@"Data\CON.txt")]
    public void UnsafeOrUserOwnedPackagePathsAreRejected(string path) =>
        Assert.ThrowsException<InvalidDataException>(() => AppUpdateInstaller.ValidatePackageFile(path));

    [TestMethod]
    public void IncompleteDuplicateAndTraversalArchivesCannotInstall()
    {
        var directory = NewDirectory();
        try
        {
            foreach (var (name, extra) in new[] { ("traversal.zip", @"..\outside.exe"), ("duplicate.zip", "LICENSE"), ("settings.zip", "config.json") })
                Assert.ThrowsException<InvalidDataException>(() =>
                    AppUpdateInstaller.ExtractPackage(Package(directory, name, extra), Path.Combine(directory, name + "-payload")));
            var incomplete = Path.Combine(directory, "incomplete.zip");
            using (var zip = ZipFile.Open(incomplete, ZipArchiveMode.Create))
                zip.CreateEntry("MabiCommerceNewLife/LICENSE");
            Assert.ThrowsException<InvalidDataException>(() =>
                AppUpdateInstaller.ExtractPackage(incomplete, Path.Combine(directory, "incomplete")));
            Assert.IsFalse(File.Exists(Path.Combine(directory, "outside.exe")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static GitHubUpdateClient Client(object[] releases) => new(new Handler(request =>
    {
        Assert.AreEqual("api.github.com", request.RequestUri!.Host);
        StringAssert.Contains(request.Headers.UserAgent.ToString(), "MabiCommerceNewLife-Updater");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(releases))
        };
    }));

    private static object Release(string tag, bool draft = false, string? url = null, string? digest = null) => new
    {
        tag_name = tag, draft, prerelease = tag.Contains('-'),
        assets = new[] { new { name = $"MabiCommerceNewLife-{tag}-win-x64.zip", size = 100,
            browser_download_url = url ?? $"https://github.com/Root50199/MabiCommerceNewLife/releases/download/{tag}/MabiCommerceNewLife-{tag}-win-x64.zip",
            digest = digest ?? "sha256:" + new string('a', 64) } }
    };

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "MabiUpdateTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Package(string directory, string name, string? extra = null)
    {
        var path = Path.Combine(directory, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in new[] { AppUpdateInstaller.ExecutableName, @"Data\commerce-catalog.json",
                     @"x64\tesseract55.dll", @"x64\leptonica-1.85.0.dll", "LICENSE", "THIRD-PARTY-NOTICES.txt" }
                     .Concat(extra is null ? [] : new[] { extra }))
        {
            using var writer = new StreamWriter(zip.CreateEntry("MabiCommerceNewLife/" + file.Replace('\\', '/')).Open());
            writer.Write("fixture");
        }
        return path;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
