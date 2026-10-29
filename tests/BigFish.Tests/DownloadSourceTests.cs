using System.Net;
using System.Text.Json;
using BigFish.Core;
using Xunit;

namespace BigFish.Tests;

public class DownloadSourceTests
{
    [Fact] public void ChinaAndGlobalSelectDifferentVerifiedSources()
    {
        var china = new DownloadSources(DownloadRegion.China); var global = new DownloadSources(DownloadRegion.Global);
        Assert.Equal(DownloadSources.ChinaRegistry, china.Registry); Assert.Equal(DownloadSources.OfficialRegistry, global.Registry);
        var node = new Uri("https://nodejs.org/dist/v24.22.1/node.zip");
        Assert.Equal("npmmirror.com", china.ArtifactCandidates(node).First().Host);
        Assert.Equal(node, china.ArtifactCandidates(node).Last()); Assert.Single(global.ArtifactCandidates(node));
    }
    [Fact] public void LockFileMirrorRewritePreservesIntegrityAndUnrelatedUrls()
    {
        var original = "{\"packages\":{\"a\":{\"resolved\":\"https://registry.npmjs.org/a/-/a-1.tgz\",\"integrity\":\"sha512-original\"},\"b\":{\"resolved\":\"https://example.com/file\"}}}";
        using var doc = JsonDocument.Parse(DownloadSources.RewriteLock(original, DownloadSources.ChinaRegistry));
        var packages = doc.RootElement.GetProperty("packages");
        Assert.Equal("https://registry.npmmirror.com/a/-/a-1.tgz", packages.GetProperty("a").GetProperty("resolved").GetString());
        Assert.Equal("sha512-original", packages.GetProperty("a").GetProperty("integrity").GetString());
        Assert.Equal("https://example.com/file", packages.GetProperty("b").GetProperty("resolved").GetString());
        Assert.Throws<BigFishException>(() => DownloadSources.RewriteLock(original, "https://unknown.example/"));
    }
    private sealed class ArtifactFixture(bool integrity = false) : IArtifactDownloader
    {
        public int Calls;
        public Task DownloadAsync(Uri uri, string path, string hash, IProgress<double>? progress, CancellationToken ct)
        {
            Calls++; if (Calls == 1) return Task.FromException(integrity ? new BigFishException("integrity", "mismatch") : new HttpRequestException("network"));
            return Task.CompletedTask;
        }
    }
    [Fact] public async Task MirrorNetworkFailureFallsBackButIntegrityFailureStops()
    {
        using var t = new TempDirectory(); var fixture = new ArtifactFixture();
        await new MirrorArtifactDownloader(fixture, new(DownloadRegion.China), new(t.Paths.Logs)).DownloadAsync(new("https://nodejs.org/dist/file.zip"), "unused", "hash", null, default);
        Assert.Equal(2, fixture.Calls);
        var corrupted = new ArtifactFixture(true);
        await Assert.ThrowsAsync<BigFishException>(() => new MirrorArtifactDownloader(corrupted, new(DownloadRegion.China), new(t.Paths.Logs)).DownloadAsync(new("https://nodejs.org/dist/file.zip"), "unused", "hash", null, default));
        Assert.Equal(1, corrupted.Calls);
    }
    private sealed class MetadataFixture(bool corrupted) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; var pin = CompatibilityManifest.Load().Dsh;
            if (!corrupted && request.RequestUri!.Host == "registry.npmmirror.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                name = pin.Name, version = pin.Version, dist = new { integrity = corrupted ? "wrong" : pin.Integrity }, repository = new { url = pin.Repository } })) });
        }
    }
    [Fact] public async Task UnsyncedMetadataFallsBackToOfficial()
    {
        var handler = new MetadataFixture(false); using var http = new HttpClient(handler);
        await new PackageVerifier(http, new(DownloadRegion.China)).VerifyRegistryAsync(CompatibilityManifest.Load().Dsh, default);
        Assert.Equal(2, handler.Calls);
    }
    [Fact] public async Task AlteredMirrorMetadataIsRejectedWithoutOfficialFallback()
    {
        var handler = new MetadataFixture(true); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<BigFishException>(() => new PackageVerifier(http, new(DownloadRegion.China)).VerifyRegistryAsync(CompatibilityManifest.Load().Dsh, default));
        Assert.Equal(1, handler.Calls);
    }
    private sealed class RunnerFixture(string error) : ICommandRunner
    {
        public int Calls;
        public Task<CommandResult> RunAsync(Command command, CancellationToken ct) => Task.FromResult(++Calls == 1 ? new CommandResult(1, error) : new(0, "ok"));
    }
    [Fact] public async Task PackageCommandsFallbackOnlyForRecognizedNetworkErrors()
    {
        using var t = new TempDirectory(); var paths = new AppPaths(t.Paths.Root, t.Paths.Workspace, DownloadRegion.China); paths.Prepare();
        var node = new NodeRuntime("node", "npm", "24.22.1"); var network = new RunnerFixture("ETIMEDOUT");
        Assert.Equal(0, (await RegistryExecution.RunAsync(paths, node, paths.Root, network, r => ["--registry", r], TimeSpan.FromSeconds(1), default)).ExitCode);
        Assert.Equal(2, network.Calls); var corrupt = new RunnerFixture("EINTEGRITY checksum failed");
        Assert.Equal(1, (await RegistryExecution.RunAsync(paths, node, paths.Root, corrupt, r => [r], TimeSpan.FromSeconds(1), default)).ExitCode);
        Assert.Equal(1, corrupt.Calls);
    }
    [Fact] public async Task ChineseFirstRunDefaultsNeverOverwriteExistingLocale()
    {
        using var t = new TempDirectory(); var config = new ConfigurationManager(t.Paths); t.Paths.Prepare();
        await config.EnsureChineseDefaultsAsync(default); var file = Path.Combine(t.Paths.Home, "settings.yaml");
        Assert.Contains("zh-CN", await File.ReadAllTextAsync(file));
        await File.WriteAllTextAsync(file, "locale:\n  preference: en-US\ncustom: preserved\n");
        await config.EnsureChineseDefaultsAsync(default); Assert.Contains("custom: preserved", await File.ReadAllTextAsync(file));
        Assert.Contains("en-US", await File.ReadAllTextAsync(file));
    }
    [Fact] public void WorkspaceCannotUseTheRuntimeOrItsExactRoot()
    {
        using var t = new TempDirectory();
        foreach (var workspace in new[] { t.Paths.Root, t.Paths.Home, t.Paths.Releases, t.Paths.Downloads, t.Paths.Logs, Path.Combine(t.Paths.Root, "runtime", "nested") })
            Assert.Throws<ArgumentException>(() => new AppPaths(t.Paths.Root, workspace));
    }
}
