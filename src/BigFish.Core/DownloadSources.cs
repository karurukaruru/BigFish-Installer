using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BigFish.Core;

public enum DownloadRegion { China, Global }
public sealed record DownloadSources(DownloadRegion Region)
{
    public const string OfficialRegistry = "https://registry.npmjs.org/";
    public const string ChinaRegistry = "https://registry.npmmirror.com/";
    public static DownloadRegion BuildDefault => typeof(DownloadSources).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "BigFishDownloadRegion")?.Value == "global" ? DownloadRegion.Global : DownloadRegion.China;
    public string DisplayName => Region == DownloadRegion.China ? "中国版（国内镜像优先）" : "国际版（官方源）";
    public string Registry => Region == DownloadRegion.China ? ChinaRegistry : OfficialRegistry;
    public string PyPiIndex => Region == DownloadRegion.China ? "https://pypi.tuna.tsinghua.edu.cn/simple" : "https://pypi.org/simple";
    public IEnumerable<string> Registries => Region == DownloadRegion.China ? [ChinaRegistry, OfficialRegistry] : [OfficialRegistry];
    public IEnumerable<string> PyPiIndexes => Region == DownloadRegion.China ? [PyPiIndex, "https://pypi.org/simple"] : [PyPiIndex];
    public IEnumerable<Uri> ArtifactCandidates(Uri official)
    {
        if (Region == DownloadRegion.China)
        {
            if (official.Host == "nodejs.org" && official.AbsolutePath.StartsWith("/dist/", StringComparison.Ordinal))
                yield return new("https://npmmirror.com/mirrors/node/" + official.AbsolutePath[6..]);
            else if (official.Host == "files.pythonhosted.org" && official.AbsolutePath.StartsWith("/packages/", StringComparison.Ordinal))
                yield return new("https://pypi.tuna.tsinghua.edu.cn" + official.AbsolutePath);
        }
        yield return official;
    }
    public static string RewriteLock(string original, string registry)
    {
        if (registry is not (OfficialRegistry or ChinaRegistry)) throw new BigFishException("untrusted-source", "不支持的依赖源。");
        var root = JsonNode.Parse(original) ?? throw new JsonException("运行时锁文件为空。");
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var (key, value) in obj.ToArray())
                {
                    if (key == "resolved" && value is JsonValue v && v.TryGetValue<string>(out var text) && text.StartsWith(OfficialRegistry, StringComparison.Ordinal))
                        obj[key] = registry + text[OfficialRegistry.Length..];
                    else Visit(value);
                }
            else if (node is JsonArray array) foreach (var child in array) Visit(child);
        }
        Visit(root); return root.ToJsonString(Json.Options);
    }
    public static bool IsRegistryNetworkFailure(string output) => Regex.IsMatch(output,
        @"(?i)\b(ETIMEDOUT|ECONNRESET|ECONNREFUSED|EAI_AGAIN|ENOTFOUND|E403|E404|E502|E503|E504|ERR_PNPM_FETCH_\d+|ERR_SOCKET_TIMEOUT)\b|\b(403 Forbidden|404 Not Found|502 Bad Gateway|503 Service Unavailable)\b")
        && !Regex.IsMatch(output, @"(?i)EINTEGRITY|ERR_PNPM_TARBALL_INTEGRITY|integrity checksum failed");
}
public sealed class MirrorArtifactDownloader(IArtifactDownloader inner, DownloadSources sources, LogManager log) : IArtifactDownloader
{
    public async Task DownloadAsync(Uri uri, string destination, string sha256, IProgress<double>? progress, CancellationToken ct)
    {
        var candidates = sources.ArtifactCandidates(uri).ToArray();
        for (var i = 0; i < candidates.Length; i++)
        {
            try { await inner.DownloadAsync(candidates[i], destination, sha256, progress, ct); return; }
            catch (Exception e) when (i + 1 < candidates.Length && !ct.IsCancellationRequested && e is HttpRequestException or TimeoutException)
            { log.Write($"镜像 {candidates[i].Host} 暂不可用，切换到已审核官方源；继续完整性校验。"); }
        }
    }
}
public static class RegistryExecution
{
    public static async Task<CommandResult> RunAsync(AppPaths paths, NodeRuntime node, string release, ICommandRunner runner,
        Func<string, IReadOnlyList<string>> arguments, TimeSpan timeout, CancellationToken ct, Func<string, Task>? prepare = null)
    {
        var registries = paths.Sources.Registries.ToArray();
        for (var i = 0; i < registries.Length; i++)
        {
            if (prepare is not null) await prepare(registries[i]);
            var result = await runner.RunAsync(new(node.Executable, arguments(registries[i]), paths.Workspace,
                RuntimeEnvironment.Create(paths, node, release, registries[i]), timeout), ct);
            if (result.ExitCode == 0 || i + 1 == registries.Length || !DownloadSources.IsRegistryNetworkFailure(result.Output)) return result;
        }
        throw new InvalidOperationException("未配置依赖源。");
    }
}
