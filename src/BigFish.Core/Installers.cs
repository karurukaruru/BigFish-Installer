using System.Text.Json;
using System.Text.Json.Nodes;

namespace BigFish.Core;

public sealed class NodeInstaller(AppPaths paths, CompatibilityManifest manifest, IArtifactDownloader downloader,
    SystemDetector detector)
{
    public async Task<NodeRuntime> EnsureAsync(bool privateOnly, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var existing = await detector.FindNodeAsync(ct, privateOnly);
        if (existing is not null) return existing;
        if (!OperatingSystem.IsWindows()) throw new BigFishException("platform", "自动安装 Portable Node.js 仅支持 Windows x64。");
        var name = $"node-v{manifest.NodeVersion}-win-x64";
        var zip = Path.Combine(paths.Downloads, name + ".zip");
        await downloader.DownloadAsync(new Uri($"https://nodejs.org/dist/v{manifest.NodeVersion}/{name}.zip"), zip, manifest.NodeSha256,
            new Progress<double>(p => progress?.Report(new("安装 Node.js", 0, 5, "下载官方运行时并校验 SHA-256", p))), ct);
        var staging = Path.Combine(paths.Root, "runtime", "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await Task.Run(() => VerifiedDownloader.ExtractSafe(zip, staging, ct), ct);
            var probe = await detector.ProbeNodeAsync(Path.Combine(staging, name, "node.exe"), ct)
                ?? throw new BigFishException("runtime-broken", "下载的 Node.js 或 npm 未通过实际执行检查。");
            ct.ThrowIfCancellationRequested();
            var backup = paths.PrivateNode + ".backup-" + Guid.NewGuid().ToString("N");
            var hadPrevious = Directory.Exists(paths.PrivateNode);
            if (hadPrevious) Directory.Move(paths.PrivateNode, backup);
            try { Directory.Move(Path.Combine(staging, name), paths.PrivateNode); }
            catch { if (hadPrevious && !Directory.Exists(paths.PrivateNode)) Directory.Move(backup, paths.PrivateNode); throw; }
            return probe with { Executable = Path.Combine(paths.PrivateNode, "node.exe"), NpmCli = Path.Combine(paths.PrivateNode, "node_modules", "npm", "bin", "npm-cli.js") };
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
}

public static class RegistryMetadata
{
    public static async Task<JsonElement> ReadAsync(HttpClient http, DownloadSources sources, string name, string version, CancellationToken ct)
    {
        var registries = sources.Registries.ToArray();
        for (var i = 0; i < registries.Length; i++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                using var response = await http.GetAsync(registries[i] + Uri.EscapeDataString(name) + "/" + version, timeout.Token);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token)); return doc.RootElement.Clone();
            }
            catch (Exception e) when (i + 1 < registries.Length && !ct.IsCancellationRequested && e is HttpRequestException or OperationCanceledException) { }
            catch (OperationCanceledException e) when (!ct.IsCancellationRequested) { throw new TimeoutException("npm 发布元数据请求超时。", e); }
        }
        throw new InvalidOperationException("未配置依赖源。");
    }
}
public sealed class PackageVerifier(HttpClient http, DownloadSources? sources = null)
{
    public async Task VerifyRegistryAsync(PackagePin pin, CancellationToken ct)
    {
        var r = await RegistryMetadata.ReadAsync(http, sources ?? new(DownloadSources.BuildDefault), pin.Name, pin.Version, ct);
        if (r.GetProperty("name").GetString() != pin.Name || r.GetProperty("version").GetString() != pin.Version ||
            r.GetProperty("dist").GetProperty("integrity").GetString() != pin.Integrity ||
            r.GetProperty("repository").GetProperty("url").GetString() != pin.Repository)
            throw new BigFishException("integrity", $"{pin.Name} 发布元数据与审核清单不一致，已停止安装。");
    }
}

public sealed class DshInstaller(AppPaths paths, CompatibilityManifest manifest, ICommandRunner runner, PackageVerifier verifier)
{
    public static string Cli(string release)
    {
        var reference = Path.Combine(release, "external-dsh.json");
        if (File.Exists(reference))
        {
            var path = JsonSerializer.Deserialize<string>(File.ReadAllText(reference));
            if (path is null || !Path.IsPathFullyQualified(path)) throw new BigFishException("invalid-state", "外部 DSH 引用必须是绝对路径。");
            return path;
        }
        return Path.Combine(release, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
    }
    public static IEnumerable<string> ExistingCliCandidates(NodeRuntime node)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Concat([Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                Path.GetDirectoryName(node.Executable) ?? "", Path.GetFullPath(Path.Combine(Path.GetDirectoryName(node.Executable) ?? ".", "..", "lib"))]);
        foreach (var dir in dirs.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            yield return Path.Combine(dir.Trim('"'), "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
    }
    private async Task<string?> FindExistingAsync(NodeRuntime node, CancellationToken ct)
    {
        foreach (var cli in ExistingCliCandidates(node))
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(cli) || AppPaths.IsWithin(paths.Releases, cli)) continue;
            try
            {
                var package = JsonNode.Parse(await File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cli)!, "..", "package.json")), ct));
                if (package?["name"]?.GetValue<string>() != manifest.Dsh.Name || package?["version"]?.GetValue<string>() != manifest.Dsh.Version) continue;
                var result = await runner.RunAsync(new(node.Executable, [cli, "--version"], paths.Workspace,
                    RuntimeEnvironment.Create(paths, node, paths.Root), TimeSpan.FromSeconds(25)), ct);
                if (result.ExitCode == 0 && result.Output.Trim() == manifest.Dsh.Version) return Path.GetFullPath(cli);
            }
            catch (Exception e) when (e is not OperationCanceledException) { }
        }
        return null;
    }
    public async Task<bool> UsableAsync(string release, NodeRuntime node, CancellationToken ct)
    {
        if (!File.Exists(Cli(release))) return false;
        var env = RuntimeEnvironment.Create(paths, node, release);
        var v = await runner.RunAsync(new(node.Executable, [Cli(release), "--version"], paths.Workspace, env, TimeSpan.FromSeconds(25)), ct);
        if (v.ExitCode != 0 || v.Output.Trim() != manifest.Dsh.Version) return false;
        var help = await runner.RunAsync(new(node.Executable, [Cli(release), "--help"], paths.Workspace, env, TimeSpan.FromSeconds(25)), ct);
        if (help.ExitCode != 0 || !help.Output.Contains("--profile", StringComparison.Ordinal)) return false;
        var pnpm = Path.Combine(release, "node_modules", "pnpm", "bin", "pnpm.cjs");
        if (!File.Exists(pnpm)) return false;
        var p = await runner.RunAsync(new(node.Executable, [pnpm, "--version"], paths.Workspace, env, TimeSpan.FromSeconds(25)), ct);
        return p.ExitCode == 0 && p.Output.Trim() == manifest.Pnpm.Version;
    }
    public async Task<string> EnsureAsync(InstallState state, NodeRuntime node, CancellationToken ct)
    {
        if (state.ActiveRelease is { } active && await UsableAsync(paths.Release(active), node, ct)) return active;
        await verifier.VerifyRegistryAsync(manifest.Dsh, ct); await verifier.VerifyRegistryAsync(manifest.Pnpm, ct);
        var id = "dsh-" + manifest.Dsh.Version.Replace('.', '-') + "-" + Guid.NewGuid().ToString("N");
        var release = paths.Release(id); Directory.CreateDirectory(release);
        var externalCli = await FindExistingAsync(node, ct);
        if (externalCli is not null)
        {
            // Reuse only executable code. All profile/config/session writes remain in our own DSH_HOME.
            await AtomicFile.WriteAsync(Path.Combine(release, "external-dsh.json"), JsonSerializer.Serialize(externalCli), ct);
            await File.WriteAllTextAsync(Path.Combine(release, "package.json"), "{\"name\":\"bigfish-pnpm-runtime\",\"private\":true}", ct);
            var install = await RegistryExecution.RunAsync(paths, node, release, runner,
                registry => [node.NpmCli, "install", "--prefix", release, "--save-exact", "--no-audit", "--no-fund", "--registry", registry, manifest.Pnpm.Name + "@" + manifest.Pnpm.Version], TimeSpan.FromMinutes(10), ct);
            install.EnsureSuccess("准备独立插件管理器");
            if (await UsableAsync(release, node, ct)) return id;
            // An unusable external runtime is never modified. Remove only our reference, then build privately.
            File.Delete(Path.Combine(release, "external-dsh.json"));
        }
        // A generation is published only through the atomic state pointer after execution checks.
        foreach (var resource in new[] { "package.json", "package-lock.json" })
        {
            using var s = typeof(DshInstaller).Assembly.GetManifestResourceStream("BigFish.Core.runtime-" + resource)!;
            using var reader = new StreamReader(s);
            await File.WriteAllTextAsync(Path.Combine(release, resource), await reader.ReadToEndAsync(ct), ct);
        }
        var originalLock = await File.ReadAllTextAsync(Path.Combine(release, "package-lock.json"), ct);
        var result = await RegistryExecution.RunAsync(paths, node, release, runner,
            registry => [node.NpmCli, "ci", "--prefix", release, "--omit=dev", "--no-audit", "--no-fund", "--registry", registry], TimeSpan.FromMinutes(20), ct,
            registry => AtomicFile.WriteAsync(Path.Combine(release, "package-lock.json"), DownloadSources.RewriteLock(originalLock, registry), ct));
        result.EnsureSuccess("安装 DeepSeek Harness");
        if (!await UsableAsync(release, node, ct)) throw new BigFishException("runtime-broken", "DSH 或 pnpm 执行验证失败，未切换运行环境。");
        return id;
    }
}

public sealed class PluginManager(AppPaths paths, CompatibilityManifest manifest, ICommandRunner runner,
    PackageVerifier verifier, ConfigurationManager config)
{
    public async Task<bool> InstalledAsync(PackagePin pin, NodeRuntime node, string release, CancellationToken ct)
    {
        // Dynamic import detects missing entrypoints/dependencies. HTTP checks later prove host activation.
        var script = """
            const {createRequire}=require('node:module'); const {pathToFileURL}=require('node:url');
            const req=createRequire(require('node:path').join(process.argv[1],'package.json'));
            (async()=>{const p=req(process.argv[2]+'/package.json'); if(p.version!==process.argv[3])process.exit(4);
              await import(pathToFileURL(req.resolve(process.argv[2])).href); console.log('BIGFISH_PLUGIN_OK');})().catch(()=>process.exit(5));
            """;
        var result = await runner.RunAsync(new(node.Executable, ["-e", script, paths.Profile, pin.Name, pin.Version], paths.Workspace,
            RuntimeEnvironment.Create(paths, node, release), TimeSpan.FromSeconds(40)), ct);
        return result.ExitCode == 0 && result.Output.Contains("BIGFISH_PLUGIN_OK", StringComparison.Ordinal);
    }
    public async Task EnsureAsync(PackagePin pin, NodeRuntime node, string release, CancellationToken ct)
    {
        if (pin != manifest.Im && pin != manifest.Bili) throw new BigFishException("untrusted-source", "插件不在固定版本清单中。");
        if (await InstalledAsync(pin, node, release, ct)) return;
        await verifier.VerifyRegistryAsync(pin, ct);
        await config.BackupProfileAsync(ct);
        // Upstream owns profile transactions. Do not force compatibility exemptions or overwrite profile YAML.
        var result = await RegistryExecution.RunAsync(paths, node, release, runner,
            registry => [DshInstaller.Cli(release), "plugin", "--profile", "web", "add", "-w", "--save-exact", "--registry", registry, pin.Name + "@" + pin.Version], TimeSpan.FromMinutes(20), ct);
        result.EnsureSuccess($"安装 {pin.Name}");
        if (!await InstalledAsync(pin, node, release, ct)) throw new BigFishException("plugin-load", $"{pin.Name} 无法导入。配置备份已保留。");
    }
}

public sealed class UpdateManager(HttpClient http, CompatibilityManifest manifest, DownloadSources? sources = null)
{
    public async Task<IReadOnlyList<string>> CheckAsync(CancellationToken ct)
    {
        var result = new List<string>();
        foreach (var pin in new[] { manifest.Dsh, manifest.Im, manifest.Bili })
        {
            var data = await RegistryMetadata.ReadAsync(http, sources ?? new(DownloadSources.BuildDefault), pin.Name, "latest", ct);
            result.Add($"{pin.Name}：审核版本 {pin.Version} / 上游 {data.GetProperty("version").GetString()}");
        }
        result.Add("上游版本仅供比较；安装器不会自动升级到未经兼容验证的组合。更新 BigFish 后执行修复安装。");
        return result;
    }
}
