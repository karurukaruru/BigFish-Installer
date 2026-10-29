using System.IO.Compression;
using BigFish.Core;
using Xunit;

namespace BigFish.Tests;

public sealed class TempDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "bigfish-test-" + Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Root);
    public AppPaths Paths => new(Root, Path.Combine(Root, "workspace 中文 & spaces"));
    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
}
public class UnitTests
{
    [Theory]
    [InlineData("v24.22.1", true, true)] [InlineData("24.9.0", true, true)]
    [InlineData("v24.8.9", true, false)] [InlineData("22.19.0", true, false)]
    [InlineData("25.0.0", true, false)] [InlineData("v24.9.0-rc.1", false, false)]
    [InlineData("node version 24.9.0", false, false)] [InlineData("", false, false)]
    [InlineData("v999999999999999.0.0", false, false)]
    public void ParseNode(string text, bool parsed, bool supported)
    { Assert.Equal(parsed, NodeVersion.TryParse(text, out var v)); Assert.Equal(supported, v.Supported); }

    [Fact] public void DependencyDecisionProtectsOldSystemNode()
    {
        Assert.Equal(DependencyAction.InstallPrivate, DependencyResolver.Node(new("node", "npm", "18.20.0"), false));
        Assert.Equal(DependencyAction.Reuse, DependencyResolver.Node(new("node", "npm", "24.22.1"), false));
        Assert.Equal(DependencyAction.RepairPrivate, DependencyResolver.Node(null, true));
        Assert.Equal(DependencyAction.InstallPrivate, DependencyResolver.Node(new("node", "npm", "24.22.1"), false, true));
    }
    [Fact] public void CompatibilityIsAnExactReviewedPair()
    {
        var m = CompatibilityManifest.Load(); Assert.True(DependencyResolver.Compatible(m.Dsh.Version, m.Im.Version, m));
        Assert.False(DependencyResolver.Compatible("0.2.2-alpha.2", m.Im.Version, m));
    }
    [Fact] public void PathsPreserveUnicodeAndRejectTraversal()
    {
        using var temp = new TempDirectory(); var p = temp.Paths; p.Prepare();
        Assert.Contains("中文 & spaces", p.Workspace);
        Assert.Throws<BigFishException>(() => p.Release("../other"));
        Assert.Throws<ArgumentException>(() => new AppPaths("relative"));
        Assert.False(AppPaths.IsWithin(p.Root, p.Root + "-outside/file"));
    }
    [Fact] public async Task StateRoundtripAndInterruptedStageRecovery()
    {
        using var temp = new TempDirectory(); var store = new InstallationStateStore(temp.Paths.StateFile);
        Assert.Null((await store.LoadAsync()).ActiveRelease);
        await store.SaveAsync(new() { ActiveRelease = "dsh-test", LastStage = "QQ / 微信插件", FailureCode = "network" });
        var reopened = await new InstallationStateStore(temp.Paths.StateFile).LoadAsync();
        Assert.Equal("dsh-test", reopened.ActiveRelease); Assert.Equal("network", reopened.FailureCode);
        await File.WriteAllTextAsync(temp.Paths.StateFile, "{corrupt");
        await Assert.ThrowsAsync<BigFishException>(() => store.LoadAsync());
        Assert.Equal("{corrupt", await File.ReadAllTextAsync(temp.Paths.StateFile));
    }
    [Fact] public async Task ProfileBackupAndSafeDefaultsNeverOverwriteUserData()
    {
        using var temp = new TempDirectory(); var paths = temp.Paths; paths.Prepare(); Directory.CreateDirectory(paths.Profile);
        var source = Path.Combine(paths.Profile, "cordis.patch.yml"); await File.WriteAllTextAsync(source, "# user's settings\n");
        var config = new ConfigurationManager(paths); var backup = await config.BackupProfileAsync(default);
        Assert.Equal(await File.ReadAllTextAsync(source), await File.ReadAllTextAsync(Path.Combine(backup, "cordis.patch.yml")));
        await config.EnsureBiliSafeDefaultsAsync(default);
        var bili = Path.Combine(paths.Home, "bili-duty", "config.json"); var first = await File.ReadAllTextAsync(bili);
        Assert.Contains("\"dmMode\": \"off\"", first); Assert.Contains("\"autoComment\": false", first);
        await File.WriteAllTextAsync(bili, "{\"custom\":true}"); await config.EnsureBiliSafeDefaultsAsync(default);
        Assert.Equal("{\"custom\":true}", await File.ReadAllTextAsync(bili));
    }
    [Fact] public async Task CancellationDoesNotReplaceAtomicFile()
    {
        using var t = new TempDirectory(); var file = Path.Combine(t.Root, "config.json"); await File.WriteAllTextAsync(file, "old");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicFile.WriteAsync(file, "new", cts.Token));
        Assert.Equal("old", await File.ReadAllTextAsync(file));
    }
    [Theory]
    [InlineData("https://127.0.0.1:3080/?token=secret", false)]
    [InlineData("http://example.com:3080/?token=secret", false)]
    [InlineData("http://127.0.0.1:3080/?token=secret", true)]
    [InlineData("http://evil@127.0.0.1:3080/?token=secret", false)]
    public void LaunchUriMustBeLocal(string url, bool valid) => Assert.Equal(valid, HealthChecker.IsLocalLaunchUri(new(url)));
    [Fact] public void SecretsAreRedacted()
    {
        var result = LogManager.Redact("https://host/?token=private123 api_key=sk-secret987 Authorization: Bearer auth456 \"clientSecret\":\"abc123\" https://me:pass@example.org");
        foreach (var secret in new[] { "private123", "sk-secret987", "auth456", "abc123", "me:pass" }) Assert.DoesNotContain(secret, result);
        Assert.DoesNotContain("other-secret", LogManager.Redact("Cookie: session=secret; another=other-secret"));
    }
    [Fact] public void ErrorCategoriesAreActionable()
    {
        Assert.Equal("access-denied", ErrorClassifier.Code(new UnauthorizedAccessException()));
        Assert.Equal("network", ErrorClassifier.Code(new HttpRequestException()));
        Assert.Equal("cancelled", ErrorClassifier.Code(new OperationCanceledException()));
        Assert.Equal("integrity", ErrorClassifier.Code(new BigFishException("integrity", "")));
    }
    [Fact] public void ArchiveTraversalIsRejected()
    {
        using var t = new TempDirectory(); var zip = Path.Combine(t.Root, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { using var writer = new StreamWriter(archive.CreateEntry("../escape.txt").Open()); writer.Write("bad"); }
        Assert.Throws<BigFishException>(() => VerifiedDownloader.ExtractSafe(zip, Path.Combine(t.Root, "extract"), default));
        Assert.False(File.Exists(Path.Combine(t.Root, "escape.txt")));
    }
    [Fact] public void UntrustedDownloadSourcesAreRejected()
    {
        Assert.Throws<BigFishException>(() => VerifiedDownloader.ValidateSource(new("http://nodejs.org/node.zip")));
        Assert.Throws<BigFishException>(() => VerifiedDownloader.ValidateSource(new("https://mirror.example/node.zip")));
        VerifiedDownloader.ValidateSource(new("https://nodejs.org/dist/v24.22.1/node.zip"));
    }
    [Fact] public void CommandsUseArgumentListWithoutShellExpansion()
    {
        using var t = new TempDirectory();
        var info = ProcessRunner.StartInfo(new("node.exe", ["C:\\中文 & path\\cli.js", "$(do-not-execute)", "a\"b"], t.Root));
        Assert.False(info.UseShellExecute); Assert.True(info.CreateNoWindow);
        Assert.Equal("$(do-not-execute)", info.ArgumentList[1]); Assert.Empty(info.Arguments);
    }
    [Fact] public void OperationLockRejectsConcurrentInstaller()
    {
        using var t = new TempDirectory(); using var one = new OperationLock(t.Paths);
        Assert.Throws<BigFishException>(() => new OperationLock(t.Paths));
    }
    [Fact] public async Task ExternalDshReferenceMustBeAbsolute()
    {
        using var t = new TempDirectory(); var marker = Path.Combine(t.Root, "external-dsh.json");
        await File.WriteAllTextAsync(marker, "\"relative/cli.js\"");
        Assert.Throws<BigFishException>(() => DshInstaller.Cli(t.Root));
        var absolute = Path.Combine(t.Root, "other", "bin.js");
        await File.WriteAllTextAsync(marker, System.Text.Json.JsonSerializer.Serialize(absolute));
        Assert.Equal(absolute, DshInstaller.Cli(t.Root));
    }
}
