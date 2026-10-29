using System.Runtime.InteropServices;
using System.Text.Json;

namespace BigFish.Core;

public sealed class SystemDetector(AppPaths paths, ICommandRunner runner)
{
    public async Task<PythonRuntime?> FindPythonAsync(CancellationToken ct)
    {
        var candidates = new List<string>();
        var exe = OperatingSystem.IsWindows() ? "python.exe" : "python3";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(dir) && !dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) candidates.Add(Path.Combine(dir.Trim('"'), exe));
        foreach (var v in new[] { "312", "311", "313", "310" })
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python" + v, "python.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python" + v, "python.exe"));
        }
        foreach (var file in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested(); if (!File.Exists(file)) continue;
            try
            {
                var result = await runner.RunAsync(new(file, ["-c", "import sys,struct; assert (3,10)<=sys.version_info<(3,14) and struct.calcsize('P')==8; print('.'.join(map(str,sys.version_info[:3])))"], paths.Root, Timeout: TimeSpan.FromSeconds(10)), ct);
                if (result.ExitCode == 0 && NodeVersion.TryParse(result.Output.Trim(), out _)) return new(Path.GetFullPath(file), result.Output.Trim());
            }
            catch (Exception e) when (e is not OperationCanceledException) { }
        }
        return null;
    }
    public static IEnumerable<string> NodeCandidates(AppPaths paths, bool privateOnly = false)
    {
        var exe = OperatingSystem.IsWindows() ? "node.exe" : "node";
        yield return Path.Combine(paths.PrivateNode, exe);
        if (privateOnly) yield break;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(dir)) yield return Path.Combine(dir.Trim('"'), exe);
        foreach (var dir in new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
            Environment.GetEnvironmentVariable("NVM_SYMLINK"), Environment.GetEnvironmentVariable("VOLTA_HOME") is { } volta ? Path.Combine(volta, "bin") : null
        }) if (!string.IsNullOrEmpty(dir)) yield return Path.Combine(dir, exe);
        var nvm = Environment.GetEnvironmentVariable("NVM_HOME");
        if (nvm is not null && Directory.Exists(nvm)) foreach (var dir in Directory.EnumerateDirectories(nvm, "v24.*")) yield return Path.Combine(dir, exe);
    }
    public async Task<NodeRuntime?> FindNodeAsync(CancellationToken ct, bool privateOnly = false)
    {
        foreach (var candidate in NodeCandidates(paths, privateOnly).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var runtime = await ProbeNodeAsync(candidate, ct);
            if (runtime is not null) return runtime;
        }
        return null;
    }
    public async Task<NodeRuntime?> ProbeNodeAsync(string candidate, CancellationToken ct)
    {
        if (!File.Exists(candidate)) return null;
        try
        {
            var r = await runner.RunAsync(new(candidate, ["-p", "JSON.stringify({version:process.version,arch:process.arch})"], paths.Root, Timeout: TimeSpan.FromSeconds(12)), ct);
            if (r.ExitCode != 0) return null;
            using var doc = JsonDocument.Parse(r.Output.Trim());
            var version = doc.RootElement.GetProperty("version").GetString()!;
            if (!NodeVersion.TryParse(version, out var v) || !v.Supported || doc.RootElement.GetProperty("arch").GetString() != "x64") return null;
            foreach (var npm in new[] { Path.Combine(Path.GetDirectoryName(candidate)!, "node_modules", "npm", "bin", "npm-cli.js"),
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(candidate)!, "..", "lib", "node_modules", "npm", "bin", "npm-cli.js")),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "npm", "bin", "npm-cli.js") })
            {
                if (!File.Exists(npm)) continue;
                var test = await runner.RunAsync(new(candidate, [npm, "--version"], paths.Root, Timeout: TimeSpan.FromSeconds(15)), ct);
                if (test.ExitCode == 0 && NodeVersion.TryParse(test.Output.Trim(), out _)) return new(Path.GetFullPath(candidate), npm, v.ToString());
            }
        }
        catch (Exception e) when (e is not OperationCanceledException && e is not UnauthorizedAccessException) { /* Broken candidates are never accepted. */ }
        return null;
    }
    public async Task<List<CheckResult>> ScanAsync(InstallState state, CancellationToken ct)
    {
        var node = await FindNodeAsync(ct);
        var checks = new List<CheckResult>
        {
            new("Windows", AppPaths.SupportedPlatform ? "✓ 已就绪" : "! 不兼容", $"{RuntimeInformation.OSDescription} / {RuntimeInformation.OSArchitecture}；需要 Windows 10 22H2 / 11 x64", AppPaths.SupportedPlatform),
            new("Node.js", node is null ? "○ 待安装或修复" : "✓ 已就绪", node?.Version ?? "将安装应用私有 Node.js 24", node is not null),
            new("npm", node is null ? "○ 待补齐" : "✓ 已执行验证", node?.NpmCli ?? "随私有 Node.js 提供；不依赖 PowerShell 执行策略", node is not null),
            new("工作区", "✓ 保留现有数据", paths.Workspace, true)
        };
        if (OperatingSystem.IsWindows())
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var admin = new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            checks.Add(new("用户权限", admin ? "管理员进程" : "✓ 标准用户权限", "正常安装不需要提升权限；已实际检查目录可写。", true));
        }
        if (node is not null && state.ActiveRelease is not null)
        {
            var release = paths.Release(state.ActiveRelease);
            var cli = DshInstaller.Cli(release);
            if (File.Exists(cli))
            {
                var result = await runner.RunAsync(new(node.Executable, [cli, "--version"], paths.Workspace, RuntimeEnvironment.Create(paths, node, release), TimeSpan.FromSeconds(30)), ct);
                checks.Add(new("DSH", result.ExitCode == 0 ? "✓ CLI 可执行" : "! 需要修复", result.Output.Trim(), result.ExitCode == 0));
            }
        }
        if (!checks.Any(c => c.Component == "DSH")) checks.Add(new("DSH", "○ 待安装", "独立运行环境，不覆盖全局安装", false));
        checks.Add(new("dsh-im", state.ImInstalled ? "○ 已安装，启动后检查加载" : "○ 待安装", "以真实 HTTP RPC 返回判断加载状态", false));
        var existing = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        if (Directory.Exists(existing)) checks.Add(new("已有 DSH 配置", "✓ 已发现并保留", existing + "；本版本不自动导入或修改", true));
        // Discover an existing global DSH from its adjacent JS entry, without executing cmd/ps1 strings.
        if (node is not null)
        {
            foreach (var cli in DshInstaller.ExistingCliCandidates(node))
            {
                if (!File.Exists(cli)) continue;
                var r = await runner.RunAsync(new(node.Executable, [cli, "--version"], paths.Workspace, Timeout: TimeSpan.FromSeconds(20)), ct);
                checks.Add(new("系统 DSH", r.ExitCode == 0 ? "✓ 发现可运行版本" : "! 发现损坏版本", r.Output.Trim() + "；精确匹配时复用程序，仍使用独立 Web profile", r.ExitCode == 0)); break;
            }
        }
        return checks;
    }
}
