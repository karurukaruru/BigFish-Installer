namespace BigFish.Core;

public interface IInstallOperations
{
    Task StopAsync();
    Task<NodeRuntime> NodeAsync(InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken ct);
    Task<string> DshAsync(InstallState state, NodeRuntime node, CancellationToken ct);
    Task ImAsync(NodeRuntime node, string release, CancellationToken ct);
    Task BiliPackageAsync(NodeRuntime node, string release, CancellationToken ct);
    Task StartAsync(NodeRuntime node, string release, bool im, CancellationToken ct);
    Task BiliDependenciesAsync(NodeRuntime node, string release, CancellationToken ct);
}

public sealed class InstallationEngine(AppPaths paths, InstallationStateStore store, IInstallOperations operations, LogManager log)
{
    public async Task<InstallState> InstallAsync(InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        paths.Prepare(); using var operationLock = new OperationLock(paths);
        var state = await store.LoadAsync(ct);
        var wantsIm = options.InstallIm || state.ImInstalled;
        var total = 4 + (wantsIm ? 1 : 0) + (options.InstallBili ? 2 : 0); var completed = 0;
        async Task Stage(string name, string detail)
        {
            ct.ThrowIfCancellationRequested();
            state = state with { LastStage = name, FailureCode = null };
            await store.SaveAsync(state, ct); log.Write(name + "：" + detail);
            progress?.Report(new(name, completed, total, detail));
        }
        try
        {
            await Stage("准备", "保护现有配置，检查运行环境");
            await operations.StopAsync();
            // Refuse to modify a profile served by another BigFish window.
            using (new FileStream(Path.Combine(paths.Root, ".service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
            completed++;
            await Stage("Node.js", "检查实际版本和 npm 执行；必要时下载私有运行时");
            var nodeStep = completed;
            var nodeProgress = new InlineProgress<InstallProgress>(p => progress?.Report(p with { Completed = nodeStep, Total = total }));
            var node = await operations.NodeAsync(options, nodeProgress, ct); completed++;
            state = state with { NodePath = node.Executable, NpmPath = node.NpmCli };
            await Stage("DeepSeek Harness", "安装固定版本并验证 CLI；此阶段耗时取决于网络");
            var release = await operations.DshAsync(state, node, ct); completed++;
            state = state with { ActiveRelease = release };
            if (wantsIm)
            {
                await Stage("QQ / 微信插件", "校验发布来源、兼容组合并安装到 Web profile");
                await operations.ImAsync(node, release, ct);
                state = state with { ImInstalled = true }; completed++;
            }
            await Stage("启动与验证", "等待本机 HTTP 认证和插件真实响应");
            await operations.StartAsync(node, release, state.ImInstalled, ct); completed++;
            if (options.InstallBili)
            {
                await Stage("Bilibili 插件", "基础安装已通过；单独处理可选组件");
                try
                {
                    await operations.StopAsync();
                    await operations.BiliPackageAsync(node, release, ct); completed++;
                    await operations.StartAsync(node, release, state.ImInstalled, ct);
                    await Stage("Bilibili Python", "安装独立 Python 并验证依赖；不启用自动评论或私信");
                    await operations.BiliDependenciesAsync(node, release, ct); completed++;
                    state = state with { BiliInstalled = true };
                }
                catch (Exception optionalError) when (optionalError is not OperationCanceledException)
                {
                    log.Write("可选组件失败；尝试恢复已安装的基础服务。");
                    try { await operations.StartAsync(node, release, state.ImInstalled, ct); }
                    catch (Exception restartError) when (restartError is not OperationCanceledException) { log.Write("基础服务重启失败：" + restartError.Message); }
                    throw;
                }
            }
            state = state with { LastStage = "完成", FailureCode = null };
            await store.SaveAsync(state, ct);
            progress?.Report(new("完成", total, total, "环境及本机服务已通过检查；API Key、平台授权与实际收发由你完成。", 1));
            return state;
        }
        catch (Exception e)
        {
            state = state with { FailureCode = ErrorClassifier.Code(e) };
            await store.SaveAsync(state, CancellationToken.None);
            log.Write($"{state.LastStage}：{state.FailureCode}，{e.Message}");
            if (e is OperationCanceledException) await operations.StopAsync();
            throw;
        }
    }
}

public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

public sealed class BigFishController : IAsyncDisposable
{
    public AppPaths Paths { get; }
    public CompatibilityManifest Manifest { get; } = CompatibilityManifest.Load();
    public LogManager Log { get; }
    public IServiceManager Service { get; }
    public InstallationStateStore Store { get; }
    public SystemDetector Detector { get; }
    public InstallationEngine Engine { get; }
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(6) };
    public BigFishController(AppPaths paths)
    {
        Paths = paths; paths.Prepare(); Log = new(paths.Logs); Store = new(paths.StateFile);
        var runner = new ProcessRunner(Log); Detector = new(paths, runner); Service = new ServiceManager(paths, Log);
        var download = new MirrorArtifactDownloader(new VerifiedDownloader(http, Log), paths.Sources, Log);
        var verifier = new PackageVerifier(http, paths.Sources); var config = new ConfigurationManager(paths);
        var ops = new RealOperations(paths, new(paths, Manifest, download, Detector), new(paths, Manifest, runner, verifier),
            new(paths, Manifest, runner, verifier, config), new(paths, runner, download), Service, config, Manifest);
        Engine = new(paths, Store, ops, Log);
    }
    public async Task<IReadOnlyList<CheckResult>> CheckAsync(CancellationToken ct, bool includePython = false)
    {
        var rows = await Detector.ScanAsync(await Store.LoadAsync(ct), ct);
        if (includePython)
        {
            var python = await Detector.FindPythonAsync(ct);
            rows.Add(new("Python（B站）", python is null ? "○ 将准备隔离环境" : "✓ 可复用解释器", python?.Version ?? "仅选择 B站功能后安装，不修改系统 Python", python is not null));
        }
        var nodeIndex = Paths.Sources.Region == DownloadRegion.China ? "https://npmmirror.com/mirrors/node/index.json" : "https://nodejs.org/dist/index.json";
        rows.Add(new("下载方案", "✓ " + Paths.Sources.DisplayName, Paths.Sources.Registry, true));
        foreach (var (name, url) in new[] { ("Node.js 下载源", nodeIndex), ("npm Registry", Paths.Sources.Registry + "@deepseek-ai%2fdsh/" + Manifest.Dsh.Version) })
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                using var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                rows.Add(new(name, r.IsSuccessStatusCode ? "✓ 可访问" : "! 网络异常", $"HTTP {(int)r.StatusCode}", r.IsSuccessStatusCode));
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { rows.Add(new(name, "! 连接失败", e is OperationCanceledException ? "timeout" : ErrorClassifier.Code(e), false)); }
        }
        foreach (var (name, url) in new[] { ("DeepSeek API", "https://api.deepseek.com/"), ("微信扫码服务", "https://ilinkai.weixin.qq.com/") })
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                using var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var reachable = (int)r.StatusCode < 500;
                rows.Add(new(name, reachable ? "✓ 网络可达" : "! 服务异常", $"HTTP {(int)r.StatusCode}；仅检查网络，不代表密钥、余额或账号权限有效", reachable));
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { rows.Add(new(name, "! 连接失败", e is OperationCanceledException ? "timeout" : ErrorClassifier.Code(e), false)); }
        }
        rows.Add(new("端口", Service.IsRunning ? "✓ BigFish 实例运行中" : ServiceManager.ChoosePort(3080) == 3080 ? "✓ 3080 可用" : "○ 3080 已占用", "占用时不终止其他进程，使用系统分配的本机端口", true));
        return rows;
    }
    public Task<IReadOnlyList<string>> CheckUpdatesAsync(CancellationToken ct) => new UpdateManager(http, Manifest, Paths.Sources).CheckAsync(ct);
    public async Task StartAsync(CancellationToken ct)
    {
        using var l = new OperationLock(Paths);
        var s = await Store.LoadAsync(ct);
        if (s.ActiveRelease is null) throw new BigFishException("not-installed", "请先完成一键安装。");
        var node = s.NodePath is not null ? await Detector.ProbeNodeAsync(s.NodePath, ct) : null;
        node ??= await Detector.FindNodeAsync(ct);
        if (node is null) throw new BigFishException("runtime-broken", "Node.js 或 npm 无法运行，请点击修复安装。");
        if (!await new DshInstaller(Paths, Manifest, new ProcessRunner(Log), new PackageVerifier(http)).UsableAsync(Paths.Release(s.ActiveRelease), node, ct))
            throw new BigFishException("runtime-broken", "DSH 或插件管理器版本已变化/无法运行，请点击修复安装。");
        await Service.StartAsync(node, Paths.Release(s.ActiveRelease), s.ImInstalled, ct);
    }
    public async ValueTask DisposeAsync() { await Service.DisposeAsync(); http.Dispose(); }

    private sealed class RealOperations(AppPaths paths, NodeInstaller node, DshInstaller dsh, PluginManager plugins,
        OptionalBiliInstaller bili, IServiceManager service, ConfigurationManager config, CompatibilityManifest manifest) : IInstallOperations
    {
        public Task StopAsync() => service.StopAsync();
        public Task<NodeRuntime> NodeAsync(InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken ct) => node.EnsureAsync(options.ForcePrivateNode, progress, ct);
        public async Task<string> DshAsync(InstallState state, NodeRuntime runtime, CancellationToken ct)
        { await config.EnsureChineseDefaultsAsync(ct); return await dsh.EnsureAsync(state, runtime, ct); }
        public Task ImAsync(NodeRuntime runtime, string release, CancellationToken ct) => plugins.EnsureAsync(manifest.Im, runtime, paths.Release(release), ct);
        public async Task BiliPackageAsync(NodeRuntime runtime, string release, CancellationToken ct)
        { await config.EnsureBiliSafeDefaultsAsync(ct); await plugins.EnsureAsync(manifest.Bili, runtime, paths.Release(release), ct); }
        public Task StartAsync(NodeRuntime runtime, string release, bool im, CancellationToken ct) => service.StartAsync(runtime, paths.Release(release), im, ct);
        public Task BiliDependenciesAsync(NodeRuntime runtime, string release, CancellationToken ct) => bili.EnsureAsync(runtime, paths.Release(release), service.Health!, ct);
    }
}
