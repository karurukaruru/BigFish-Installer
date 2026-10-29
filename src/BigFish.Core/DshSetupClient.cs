using System.Text.Json;
using System.Text.RegularExpressions;

namespace BigFish.Core;

public interface IDshSetupRpc
{
    Task<JsonElement> CallAsync(string endpoint, object arguments, CancellationToken ct);
}

// Uses the pinned DSH settings/credentials APIs; never edits credential YAML itself.
public sealed class DshSetupClient(IDshSetupRpc rpc)
{
    private async Task<string> KeyReferenceAsync(CancellationToken ct)
    {
        var settings = await rpc.CallAsync("settings/describe", new { }, ct);
        foreach (var ns in settings.GetProperty("namespaces").EnumerateArray())
        {
            if (ns.GetProperty("ns").GetString() != "llm-deepseek") continue;
            var value = ns.GetProperty("value");
            var reference = value.TryGetProperty("apiKeyEnv", out var field) ? field.GetString() : "DEEPSEEK_API_KEY";
            if (reference is null || !Regex.IsMatch(reference, "^[A-Za-z_][A-Za-z0-9_]*$"))
                throw new BigFishException("configuration", "DSH 的 DeepSeek 凭据引用无效，请在 DSH 模型设置中检查。");
            return reference;
        }
        throw new BigFishException("configuration", "未找到受支持的 DeepSeek 模型配置，请先完成安装或修复。");
    }
    public async Task<bool> HasApiKeyAsync(CancellationToken ct)
    {
        var reference = await KeyReferenceAsync(ct);
        var state = await rpc.CallAsync("credentials/describe", new { refs = new[] { reference } }, ct);
        return state.GetProperty(reference).GetProperty("configured").GetBoolean();
    }
    public async Task SaveApiKeyAsync(string apiKey, CancellationToken ct)
    {
        var key = apiKey.Trim();
        if (key.Length is 0 or > 4096 || key.Any(char.IsWhiteSpace))
            throw new BigFishException("api-key-format", "请输入有效的 API Key，不能包含空格或换行。");
        var reference = await KeyReferenceAsync(ct);
        var state = await rpc.CallAsync("credentials/describe", new { refs = new[] { reference } }, ct);
        if (!state.GetProperty(reference).GetProperty("writable").GetBoolean())
            throw new BigFishException("configuration", "该凭据由外部环境管理，不能覆盖。请在 DSH 模型页处理。");
        await rpc.CallAsync("credentials/set", new { @ref = reference, value = key }, ct);
        var after = await rpc.CallAsync("credentials/describe", new { refs = new[] { reference } }, ct);
        if (!after.GetProperty(reference).GetProperty("configured").GetBoolean())
            throw new BigFishException("configuration", "DSH 没有确认密钥已保存，请重试。");
    }
    public async Task EnsureWorkspaceAsync(string path, CancellationToken ct)
    {
        var value = await rpc.CallAsync("workspace/create", new { request = new { path = AppPaths.Normalize(path) } }, ct);
        if (!value.TryGetProperty("workspace", out var workspace) || workspace.ValueKind != JsonValueKind.Object)
            throw new BigFishException("configuration", "DSH 未确认工作区已注册，已有目录内容未修改。");
    }
}
