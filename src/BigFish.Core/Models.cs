using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BigFish.Core;

public readonly record struct NodeVersion(int Major, int Minor, int Patch)
{
    public static bool TryParse(string text, out NodeVersion version)
    {
        var m = Regex.Match(text.Trim(), @"^v?(\d+)\.(\d+)\.(\d+)$");
        version = default;
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var a) ||
            !int.TryParse(m.Groups[2].Value, out var b) || !int.TryParse(m.Groups[3].Value, out var c)) return false;
        version = new(a, b, c); return true;
    }
    public bool Supported => Major == 24 && Minor >= 9;
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public sealed record PackagePin(string Name, string Version, string Integrity, string Repository);
public sealed record CompatibilityManifest(string CheckedOn, string NodeVersion, string NodeSha256,
    PackagePin Dsh, PackagePin Im, PackagePin Pnpm, PackagePin Bili)
{
    public static CompatibilityManifest Load()
    {
        using var s = typeof(CompatibilityManifest).Assembly.GetManifestResourceStream("BigFish.Core.versions.json")!;
        return JsonSerializer.Deserialize<CompatibilityManifest>(s, Json.Options)!;
    }
}
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
public sealed record NodeRuntime(string Executable, string NpmCli, string Version);
public sealed record PythonRuntime(string Executable, string Version);
public sealed record CheckResult(string Component, string Status, string Detail, bool Ready);
public sealed record InstallProgress(string Stage, int Completed, int Total, string Detail, double? Fraction = null);
public sealed record InstallOptions(bool InstallIm = true, bool InstallBili = false, bool ForcePrivateNode = false);
public sealed record InstallState
{
    public int Schema { get; init; } = 1;
    public string? ActiveRelease { get; init; }
    public string? NodePath { get; init; }
    public string? NpmPath { get; init; }
    public string? LastStage { get; init; }
    public string? FailureCode { get; init; }
    public bool ImInstalled { get; init; }
    public bool BiliInstalled { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
public enum DependencyAction { Reuse, InstallPrivate, RepairPrivate }
public static class DependencyResolver
{
    public static DependencyAction Node(NodeRuntime? runtime, bool privateExists, bool forcePrivate = false) =>
        runtime is not null && NodeVersion.TryParse(runtime.Version, out var v) && v.Supported && !forcePrivate
            ? DependencyAction.Reuse : privateExists ? DependencyAction.RepairPrivate : DependencyAction.InstallPrivate;
    public static bool Compatible(string dsh, string im, CompatibilityManifest manifest) =>
        dsh == manifest.Dsh.Version && im == manifest.Im.Version;
}
public sealed class BigFishException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}
public static class ErrorClassifier
{
    public static string Code(Exception e) => e switch
    {
        OperationCanceledException => "cancelled",
        BigFishException b => b.Code,
        UnauthorizedAccessException => "access-denied",
        HttpRequestException => "network",
        TimeoutException => "timeout",
        JsonException => "invalid-state",
        IOException => "io",
        _ => "unexpected"
    };
    public static string Hint(Exception e) => Code(e) switch
    {
        "cancelled" => "已取消。已验证组件会保留，可重新点击一键安装。",
        "access-denied" => "目录没有写入权限，请在高级选项中选择当前用户可写目录。",
        "network" => "网络请求失败。检查网络或代理后重试；不会自动切换未知下载源。",
        "integrity" => "下载校验不匹配，文件未执行。请重试或从官方来源检查版本。",
        "startup-timeout" => "服务未在限时内就绪。查看脱敏日志，检查端口及插件兼容性。",
        "incompatible" => "版本不在已审核组合内；请更新 BigFish 后重试。",
        "busy" => "另一个 BigFish 操作正在使用此安装目录。",
        _ => "操作未完成，已有工作区与配置保留。请查看日志并尝试修复安装。"
    };
}

public sealed class AppPaths
{
    public string Root { get; }
    public string Workspace { get; }
    public DownloadSources Sources { get; }
    public string Home => Path.Combine(Root, "dsh-home");
    public string Profile => Path.Combine(Home, "profiles", "web");
    public string Releases => Path.Combine(Root, "releases");
    public string Downloads => Path.Combine(Root, "downloads");
    public string Logs => Path.Combine(Root, "logs");
    public string StateFile => Path.Combine(Root, "state.json");
    public string PrivateNode => Path.Combine(Root, "runtime", "node-v" + CompatibilityManifest.Load().NodeVersion + "-win-x64");
    public AppPaths(string? root = null, string? workspace = null, DownloadRegion? region = null)
    {
        Sources = new(region ?? DownloadSources.BuildDefault);
        Root = Normalize(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BigFish"));
        Workspace = Normalize(workspace ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ai-fish"));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Root.Equals(Workspace, comparison) || new[] { Home, Releases, Downloads, Logs, Path.Combine(Root, "runtime") }
            .Any(p => p.Equals(Workspace, comparison) || IsWithin(p, Workspace))) throw new ArgumentException("工作区不能覆盖运行环境目录。");
    }
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) throw new ArgumentException("请选择绝对路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }
    public static bool IsWithin(string parent, string child) => Path.GetFullPath(child).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public string Release(string id)
    {
        if (!Regex.IsMatch(id, "^[a-zA-Z0-9-]+$")) throw new BigFishException("invalid-state", "无效的运行环境目录标识。");
        return Path.Combine(Releases, id);
    }
    public void Prepare()
    {
        foreach (var p in new[] { Root, Workspace, Home, Releases, Downloads, Logs })
        {
            Directory.CreateDirectory(p);
            var probe = Path.Combine(p, ".write-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        }
    }
    public static bool SupportedPlatform => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19045) &&
        RuntimeInformation.OSArchitecture == Architecture.X64;
}
