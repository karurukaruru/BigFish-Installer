using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using BigFish.Core;
using Xunit;

namespace BigFish.Tests;

public class IntegrationTests
{
    private sealed class FakeOperations : IInstallOperations
    {
        public bool HasNode, HasDsh, HasIm, Running;
        public int NodeInstalls, DshInstalls, ImInstalls, OptionalCalls, FailuresRemaining;
        public bool StartupTimeout;
        public Task StopAsync() { Running = false; return Task.CompletedTask; }
        public Task<NodeRuntime> NodeAsync(InstallOptions options, IProgress<InstallProgress>? p, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); if (!HasNode) { NodeInstalls++; HasNode = true; }
            return Task.FromResult(new NodeRuntime("node", "npm-cli.js", "24.22.1"));
        }
        public Task<string> DshAsync(InstallState state, NodeRuntime node, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailuresRemaining-- > 0) throw new HttpRequestException("simulated interrupted package download");
            if (!HasDsh) { DshInstalls++; HasDsh = true; }
            return Task.FromResult("verified-release");
        }
        public Task ImAsync(NodeRuntime n, string r, CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (!HasIm) { ImInstalls++; HasIm = true; } return Task.CompletedTask; }
        public Task BiliPackageAsync(NodeRuntime n, string r, CancellationToken ct) { OptionalCalls++; throw new HttpRequestException("optional package failed"); }
        public Task BiliDependenciesAsync(NodeRuntime n, string r, CancellationToken ct) { OptionalCalls++; return Task.CompletedTask; }
        public Task StartAsync(NodeRuntime n, string r, bool im, CancellationToken ct)
        { if (StartupTimeout) throw new BigFishException("startup-timeout", "simulated timeout"); Running = true; return Task.CompletedTask; }
    }
    [Theory]
    [InlineData(false, false, false, 1, 1, 1)]
    [InlineData(true, true, true, 0, 0, 0)]
    [InlineData(true, false, false, 0, 1, 1)]
    [InlineData(true, true, false, 0, 0, 1)]
    public async Task EmptyCompleteAndPartialEnvironments(bool node, bool dsh, bool im, int n, int d, int i)
    {
        using var t = new TempDirectory(); var ops = new FakeOperations { HasNode = node, HasDsh = dsh, HasIm = im };
        var store = new InstallationStateStore(t.Paths.StateFile); var engine = new InstallationEngine(t.Paths, store, ops, new(t.Paths.Logs));
        var state = await engine.InstallAsync(new(), null, default);
        Assert.Equal(n, ops.NodeInstalls); Assert.Equal(d, ops.DshInstalls); Assert.Equal(i, ops.ImInstalls);
        Assert.True(ops.Running); Assert.True(state.ImInstalled); Assert.Equal(0, ops.OptionalCalls);
    }
    [Fact] public async Task FailedInstallResumesAndRepeatDoesNotReinstall()
    {
        using var t = new TempDirectory(); var ops = new FakeOperations { FailuresRemaining = 1 };
        var store = new InstallationStateStore(t.Paths.StateFile); var engine = new InstallationEngine(t.Paths, store, ops, new(t.Paths.Logs));
        await Assert.ThrowsAsync<HttpRequestException>(() => engine.InstallAsync(new(), null, default));
        Assert.Equal("network", (await store.LoadAsync()).FailureCode);
        await engine.InstallAsync(new(), null, default);
        var sentinel = Path.Combine(t.Paths.Home, "session-sentinel"); await File.WriteAllTextAsync(sentinel, "history");
        await new InstallationEngine(t.Paths, new(t.Paths.StateFile), ops, new(t.Paths.Logs)).InstallAsync(new(), null, default);
        Assert.Equal(1, ops.NodeInstalls); Assert.Equal(1, ops.DshInstalls); Assert.Equal(1, ops.ImInstalls);
        Assert.Equal("history", await File.ReadAllTextAsync(sentinel));
    }
    [Theory]
    [InlineData(false, 4)] [InlineData(true, 5)]
    public async Task ProgressCountsOnlyExecutedStages(bool im, int expected)
    {
        using var t = new TempDirectory(); var events = new List<InstallProgress>();
        await new InstallationEngine(t.Paths, new(t.Paths.StateFile), new FakeOperations(), new(t.Paths.Logs))
            .InstallAsync(new(InstallIm: im), new InlineProgress<InstallProgress>(events.Add), default);
        Assert.All(events, e => Assert.Equal(expected, e.Total));
        Assert.Equal(expected, events[^1].Completed);
        Assert.Equal(Enumerable.Range(0, expected + 1), events.Select(e => e.Completed));
    }
    [Fact] public async Task StartupTimeoutDoesNotReportSuccess()
    {
        using var t = new TempDirectory(); var ops = new FakeOperations { StartupTimeout = true }; var store = new InstallationStateStore(t.Paths.StateFile);
        await Assert.ThrowsAsync<BigFishException>(() => new InstallationEngine(t.Paths, store, ops, new(t.Paths.Logs)).InstallAsync(new(), null, default));
        Assert.Equal("startup-timeout", (await store.LoadAsync()).FailureCode); Assert.False(ops.Running);
    }
    [Fact] public async Task OptionalFailurePreservesBaseCheckpoint()
    {
        using var t = new TempDirectory(); var ops = new FakeOperations(); var store = new InstallationStateStore(t.Paths.StateFile);
        await Assert.ThrowsAsync<HttpRequestException>(() => new InstallationEngine(t.Paths, store, ops, new(t.Paths.Logs)).InstallAsync(new(InstallBili: true), null, default));
        var state = await store.LoadAsync(); Assert.Equal("verified-release", state.ActiveRelease); Assert.True(state.ImInstalled);
        Assert.False(state.BiliInstalled); Assert.Equal(1, ops.OptionalCalls);
        Assert.True(ops.Running);
    }
    [Fact] public async Task CancelledInstallDoesNotExecuteLaterStages()
    {
        using var t = new TempDirectory(); var ops = new FakeOperations(); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new InstallationEngine(t.Paths, new(t.Paths.StateFile), ops, new(t.Paths.Logs)).InstallAsync(new(), null, cts.Token));
        Assert.Equal(0, ops.NodeInstalls + ops.DshInstalls + ops.ImInstalls);
    }
    [Fact] public void OccupiedPortUsesOsAssignmentWithoutKillingOwner()
    {
        var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        try { Assert.Equal(0, ServiceManager.ChoosePort(((IPEndPoint)server.LocalEndpoint).Port)); Assert.True(server.Server.IsBound); }
        finally { server.Stop(); }
    }
    [Fact] public async Task RealLocalHttpAuthenticationAndPluginReadiness()
    {
        await using var server = new LocalDshFixture(); using var health = new HealthChecker();
        await health.AuthenticateAsync(new(server.Origin, "?token=fixture-secret"), default);
        await health.WaitForImAsync(TimeSpan.FromSeconds(2), default);
        var rows = await health.ConnectionStatusAsync(default);
        Assert.All(rows, s => Assert.Contains("已配置 0 个", s)); Assert.True(server.SawCookie);
    }
    [Fact] public async Task UnrelatedHttpServerIsNotDsh()
    {
        await using var server = new LocalDshFixture(unrelated: true); using var health = new HealthChecker();
        await Assert.ThrowsAsync<BigFishException>(() => health.AuthenticateAsync(new(server.Origin, "?token=fixture"), default));
    }
    [Fact] public async Task LocalDshFixtureCanStopBeforeOrDuringAccept()
    {
        for (var i = 0; i < 100; i++)
        {
            var server = new LocalDshFixture();
            if (i % 2 == 0) await Task.Yield();
            await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    [Fact] public async Task ActualChildProcessCancellationTerminatesPromptly()
    {
        using var t = new TempDirectory(); var runner = new ProcessRunner(new(t.Paths.Logs)); using var cts = new CancellationTokenSource(300);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(new("node", ["-e", "setInterval(()=>{},1000)"], t.Root), cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8));
    }
    [Fact] public async Task ActualSilentChildHitsReadinessTimeoutAndIsStopped()
    {
        using var t = new TempDirectory(); var paths = t.Paths; paths.Prepare();
        var release = paths.Release("fixture"); var cli = DshInstaller.Cli(release);
        Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
        await File.WriteAllTextAsync(cli, "export async function runCli(){setInterval(()=>{},1000);}");
        await using var service = new ServiceManager(paths, new(paths.Logs), TimeSpan.FromMilliseconds(600));
        var error = await Assert.ThrowsAsync<BigFishException>(() => service.StartAsync(new("node", "unused", "24.22.1"), release, false, default));
        Assert.Equal("startup-timeout", error.Code); Assert.False(service.IsRunning); Assert.Null(service.LaunchUri);
        using var released = new FileStream(Path.Combine(paths.Root, ".service.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    [Fact] public async Task ShutdownOutputFailureStillReleasesServiceLock()
    {
        using var t = new TempDirectory(); var paths = t.Paths; paths.Prepare();
        await using var fixture = new LocalDshFixture();
        var release = paths.Release("fixture"); var cli = DshInstaller.Cli(release);
        Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
        await File.WriteAllTextAsync(cli, "export async function runCli(){process.on('SIGINT',()=>{console.log('shutdown-log-failure');setTimeout(()=>process.exit(0),20);});console.log('dsh web: " + fixture.Origin + "?token=fixture');}");
        var log = new LogManager(paths.Logs);
        log.Line += line => { if (line.Contains("shutdown-log-failure", StringComparison.Ordinal)) throw new IOException("fixture sink failure"); };
        await using var service = new ServiceManager(paths, log);
        await service.StartAsync(new("node", "unused", "24.22.1"), release, false, default);
        await Assert.ThrowsAsync<IOException>(() => service.StopAsync());
        Assert.False(service.IsRunning); Assert.Null(service.Health); Assert.Null(service.LaunchUri);
        using var released = new FileStream(Path.Combine(paths.Root, ".service.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    [Fact] public async Task FailedProcessLaunchReleasesServiceOwnership()
    {
        using var t = new TempDirectory(); var paths = t.Paths; paths.Prepare();
        await using var service = new ServiceManager(paths, new(paths.Logs));
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => service.StartAsync(new("missing-bigfish-executable", "unused", "24.22.1"), paths.Root, false, default));
        using var released = new FileStream(Path.Combine(paths.Root, ".service.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    [Fact] public async Task PluginLauncherUsesPinnedPnpmWithoutWindowsCmdPathSplitting()
    {
        using var t = new TempDirectory(); var paths = t.Paths; paths.Prepare();
        var launcher = Path.Combine(paths.Root, "plugin-launcher.mjs");
        var fakeCli = Path.Combine(paths.Workspace, "fake dsh & spaces.mjs");
        var fakePnpm = Path.Combine(paths.Workspace, "fake pnpm & spaces.cjs");
        await File.WriteAllTextAsync(launcher, PluginManager.PluginLauncherSource);
        await File.WriteAllTextAsync(fakePnpm, "process.stdout.write('PINNED_PNPM_OK')");
        await File.WriteAllTextAsync(fakeCli, """
            export async function runCli({packageManager: pm}) {
              const {spawnSync} = await import('node:child_process');
              const result = spawnSync(pm.command, [...pm.args, '--version'], {
                shell: false, encoding: 'utf8', env: pm.env
              });
              if (result.status !== 0) throw new Error(result.stderr || 'pnpm launch failed');
              process.stdout.write(JSON.stringify({argv: process.argv.slice(2), output: result.stdout}));
            }
            """);
        var runner = new ProcessRunner(new(paths.Logs));
        var result = await runner.RunAsync(new("node", [launcher, fakeCli, fakePnpm, "plugin", "--profile", "web", "add", "@test/hello@1.0.0"], paths.Workspace), default);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PINNED_PNPM_OK", result.Output);
        Assert.Contains("\"plugin\"", result.Output);
        Assert.Contains("@test/hello@1.0.0", result.Output);
    }

    [Fact] public async Task DetectorExecutesNodeAndNpmInsteadOfTrustingFiles()
    {
        using var t = new TempDirectory(); var candidate = Path.Combine(t.Root, "node.exe"); await File.WriteAllTextAsync(candidate, "fixture");
        var npm = Path.Combine(t.Root, "node_modules", "npm", "bin", "npm-cli.js"); Directory.CreateDirectory(Path.GetDirectoryName(npm)!); await File.WriteAllTextAsync(npm, "fixture");
        var fake = new FixtureRunner(); var detector = new SystemDetector(t.Paths, fake);
        Assert.NotNull(await detector.ProbeNodeAsync(candidate, default)); Assert.Equal(2, fake.Calls);
        fake.BrokenNpm = true; Assert.Null(await detector.ProbeNodeAsync(candidate, default));
    }
    private sealed class FixtureRunner : ICommandRunner
    {
        public int Calls; public bool BrokenNpm;
        public Task<CommandResult> RunAsync(Command c, CancellationToken ct)
        { Calls++; return Task.FromResult(c.Arguments[0] == "-p" ? new CommandResult(0, "{\"version\":\"v24.22.1\",\"arch\":\"x64\"}") : new(BrokenNpm ? 1 : 0, "11.19.1")); }
    }
    private sealed class DownloadFixtureHandler(int failures, byte[] bytes) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; return Task.FromResult(new HttpResponseMessage(Calls <= failures ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { RequestMessage = request, Content = new ByteArrayContent(bytes) });
        }
    }
    [Fact] public async Task DownloadsRetryVerifyCacheAndRejectCorruption()
    {
        using var t = new TempDirectory(); byte[] bytes = Encoding.UTF8.GetBytes("verified payload"); var handler = new DownloadFixtureHandler(1, bytes);
        using var http = new HttpClient(handler); var downloader = new VerifiedDownloader(http, new(t.Paths.Logs)); var file = Path.Combine(t.Root, "download.zip");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)); var uri = new Uri("https://nodejs.org/dist/fixture.zip");
        await downloader.DownloadAsync(uri, file, hash, null, default); Assert.Equal(2, handler.Calls);
        await downloader.DownloadAsync(uri, file, hash, null, default); Assert.Equal(2, handler.Calls);
        await Assert.ThrowsAsync<BigFishException>(() => downloader.DownloadAsync(uri, file, new string('0', 64), null, default));
        Assert.False(File.Exists(file + ".partial"));
    }
    [Fact] public async Task DownloadRetryIsBounded()
    {
        using var t = new TempDirectory(); var handler = new DownloadFixtureHandler(9, []); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => new VerifiedDownloader(http, new(t.Paths.Logs)).DownloadAsync(new("https://nodejs.org/fixture"), Path.Combine(t.Root, "artifact"), new string('0', 64), null, default));
        Assert.Equal(3, handler.Calls);
    }
    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.OK); }
    }
    [Fact] public async Task NetworkTimeoutIsNotReportedAsUserCancellation()
    {
        using var t = new TempDirectory(); using var http = new HttpClient(new TimeoutHandler()) { Timeout = TimeSpan.FromMilliseconds(30) };
        await Assert.ThrowsAsync<TimeoutException>(() => new VerifiedDownloader(http, new(t.Paths.Logs)).DownloadAsync(new("https://nodejs.org/fixture"), Path.Combine(t.Root, "artifact"), new string('0', 64), null, default));
    }
}

internal sealed class LocalDshFixture : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    public Uri Origin { get; }
    public bool SawCookie { get; private set; }
    public LocalDshFixture(bool unrelated = false, Func<string, string, string>? apiResponse = null)
    {
        listener.Start(); Origin = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        loop = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token); using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var line = await reader.ReadLineAsync(stop.Token) ?? ""; var path = line.Split(' ').ElementAtOrDefault(1) ?? "/";
                var length = 0; var chunked = false;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(stop.Token)))
                {
                    if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) && line.Contains("chunked", StringComparison.OrdinalIgnoreCase)) chunked = true;
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Split(':')[1]);
                    if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) SawCookie = true;
                }
                var requestBody = "";
                if (chunked)
                {
                    var chunks = new StringBuilder();
                    while (true)
                    {
                        var header = await reader.ReadLineAsync(stop.Token) ?? throw new IOException("missing chunk");
                        var size = Convert.ToInt32(header.Split(';')[0], 16);
                        if (size == 0) { while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { } break; }
                        var content = new char[size]; var consumed = 0;
                        while (consumed < size) { var n = await reader.ReadAsync(content.AsMemory(consumed), stop.Token); if (n == 0) throw new IOException("short chunk"); consumed += n; }
                        chunks.Append(content); await reader.ReadLineAsync(stop.Token);
                    }
                    requestBody = chunks.ToString();
                }
                else if (length > 0)
                {
                    var body = new char[length]; var read = 0;
                    while (read < length) { var n = await reader.ReadAsync(body.AsMemory(read), stop.Token); if (n == 0) break; read += n; }
                    requestBody = new string(body, 0, read);
                }
                var login = path.Contains("?token=", StringComparison.Ordinal) && !unrelated;
                var payload = path.StartsWith("/api/") ? "{\"result\":{\"ok\":true,\"value\":{\"bots\":[]}}}" : unrelated ? "other service" : "<title>DeepSeek Harness</title>";
                if (path.StartsWith("/api/", StringComparison.Ordinal) && apiResponse is not null) payload = apiResponse(path, requestBody);
                var bytes = Encoding.UTF8.GetBytes(payload);
                var response = $"HTTP/1.1 {(login ? "303 See Other" : "200 OK")}\r\nConnection: close\r\nContent-Length: {bytes.Length}\r\nContent-Type: {(path.StartsWith("/api/") ? "application/json" : "text/html")}\r\n" +
                    (login ? "Set-Cookie: fixture=session; Path=/; HttpOnly\r\nLocation: /\r\n" : "") + "\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), stop.Token); await stream.WriteAsync(bytes, stop.Token);
            }
        });
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await loop; }
        catch (Exception e) when (e is OperationCanceledException or SocketException) { }
        finally { listener.Stop(); stop.Dispose(); }
    }
}
