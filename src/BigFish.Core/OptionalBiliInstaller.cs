using System.Text.Json.Nodes;

namespace BigFish.Core;

public sealed class OptionalBiliInstaller(AppPaths paths, ICommandRunner runner, IArtifactDownloader downloader)
{
    // Official PyPI uv Windows wheel; any mirror uses the same pinned SHA-256.
    public const string UvUrl = "https://files.pythonhosted.org/packages/7c/9d/714523188aea3054644d8e69ae8089aac70325a3cee2d3d05509d4f23398/uv-0.13.6-py3-none-win_amd64.whl";
    public const string UvSha256 = "7c9bc35ccb65932d8dc10a0669f9c805eaa3c9827ce18ecbecb1bd67944b2cdf";
    public async Task EnsureAsync(NodeRuntime node, string release, HealthChecker health, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new BigFishException("optional-platform", "B站依赖自动安装需要 Windows x64；基础服务已就绪。");
        var stateDir = Path.Combine(paths.Home, "bili-duty");
        var venv = Path.Combine(stateDir, ".venv"); var python = Path.Combine(venv, "Scripts", "python.exe");
        var env = RuntimeEnvironment.Create(paths, node, release);
        env["UV_PYTHON_INSTALL_DIR"] = Path.Combine(stateDir, "uv-python");
        env["UV_CACHE_DIR"] = Path.Combine(paths.Root, "uv-cache");
        async Task<bool> Probe()
        {
            if (!File.Exists(python)) return false;
            var r = await runner.RunAsync(new(python, ["-c", "import sys; assert sys.version_info >= (3,10); import bilibili_api,aiohttp,mcp,qrcode,PIL; print('READY')"], paths.Workspace, env, TimeSpan.FromSeconds(30)), ct);
            return r.ExitCode == 0 && r.Output.Contains("READY", StringComparison.Ordinal);
        }
        if (!await Probe())
        {
            var archive = Path.Combine(paths.Downloads, "uv-0.13.6-win_amd64.whl");
            await downloader.DownloadAsync(new(UvUrl), archive, UvSha256, null, ct);
            var root = Path.Combine(paths.Root, "runtime", "uv-0.13.6");
            var executable = Path.Combine(root, "uv.exe");
            if (!File.Exists(executable))
            {
                var staging = root + "." + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(staging);
                try
                {
                    await Task.Run(() => VerifiedDownloader.ExtractSafe(archive, staging, ct), ct);
                    var extracted = Directory.EnumerateFiles(staging, "uv.exe", SearchOption.AllDirectories).Single();
                    Directory.CreateDirectory(root);
                    foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Where(f =>
                        Path.GetFileName(f).StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase)))
                    {
                        var dest = Path.Combine(root, "notices", Path.GetRelativePath(staging, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(file, dest, true);
                    }
                    File.Move(extracted, executable, true);
                }
                finally { Directory.Delete(staging, true); }
            }
            (await runner.RunAsync(new(executable, ["--version"], paths.Workspace, env, TimeSpan.FromSeconds(15)), ct)).EnsureSuccess("检查 uv");
            if (!File.Exists(python))
            {
                var existingPython = await new SystemDetector(paths, runner).FindPythonAsync(ct);
                var args = existingPython is null ? new[] { "venv", "--python", "3.12", "--managed-python", venv }
                    : new[] { "venv", "--python", existingPython.Executable, venv };
                (await runner.RunAsync(new(executable, args, paths.Workspace, env, TimeSpan.FromMinutes(8)), ct)).EnsureSuccess("安装隔离 Python");
            }
            // Exact requirement file comes from the already verified pinned npm package.
            var requirements = Path.Combine(paths.Profile, "node_modules", "dsh-bili-scroll", "vendor", "bilibili-mcp", "requirements.txt");
            if (!File.Exists(requirements)) throw new BigFishException("optional-dependencies", "B站插件的依赖清单缺失，拒绝执行未知安装脚本。");
            var indexes = paths.Sources.PyPiIndexes.ToArray();
            for (var i = 0; i < indexes.Length; i++)
            {
                var result = await runner.RunAsync(new(executable, ["pip", "install", "--python", python, "--index-url", indexes[i], "-r", requirements], paths.Workspace, env, TimeSpan.FromMinutes(12)), ct);
                if (result.ExitCode == 0) break;
                if (i + 1 == indexes.Length || !DownloadSources.IsRegistryNetworkFailure(result.Output)) result.EnsureSuccess("安装 B站 Python 依赖");
            }
            if (!await Probe()) throw new BigFishException("optional-dependencies", "Python 实际导入检查失败。");
        }
        // This endpoint updates just pythonPath, preserving all other user configuration.
        var update = await health.SendJsonAsync("api/bili-duty/config", new { pythonPath = python }, ct);
        if (!update.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) throw new BigFishException("optional-dependencies", "B站 Python 路径配置未成功。");
        var status = await health.SendJsonAsync("api/bili-duty/status", null, ct);
        if (!status.TryGetProperty("venvReady", out var ready) || !ready.GetBoolean()) throw new BigFishException("optional-dependencies", "B站插件尚未确认 Python 依赖就绪。");
    }
}
