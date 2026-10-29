using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BigFish.Core;

public sealed class HealthChecker : IDisposable, IDshSetupRpc
{
    private readonly HttpClient http;
    private readonly CookieContainer cookies;
    public Uri? BaseUri { get; private set; }
    public HealthChecker()
    {
        cookies = new();
        http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, CookieContainer = cookies }) { Timeout = TimeSpan.FromSeconds(8) };
    }
    public static bool IsLocalLaunchUri(Uri uri) => uri.Scheme == "http" && uri.Host == "127.0.0.1" &&
        string.IsNullOrEmpty(uri.UserInfo) && uri.Port > 0 && uri.AbsolutePath == "/";
    public async Task AuthenticateAsync(Uri uri, CancellationToken ct)
    {
        if (!IsLocalLaunchUri(uri)) throw new BigFishException("unsafe-bind", "拒绝非本机的管理服务地址。");
        using var response = await http.GetAsync(uri, ct);
        if (response.StatusCode != HttpStatusCode.SeeOther || cookies.GetCookies(uri).Count == 0)
            throw new BigFishException("health-check", "服务未返回 DSH 启动认证 Cookie，不能确认为就绪。");
        var origin = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
        var location = response.Headers.Location;
        if (location is null || new Uri(origin, location).GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority))
            throw new BigFishException("health-check", "启动认证返回了非本机跳转。");
        BaseUri = origin;
        using var root = await http.GetAsync(origin, ct); root.EnsureSuccessStatusCode();
        var html = await root.Content.ReadAsStringAsync(ct);
        if (!html.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase)) throw new BigFishException("health-check", "HTTP 返回的页面不是预期的 DSH Web UI。");
    }
    public async Task<JsonElement> RpcAsync(string channel, string method, CancellationToken ct)
    {
        if (channel is not ("qq" or "weixin" or "dsh-im-language")) throw new ArgumentException("不支持的只读状态通道。");
        if (method is not ("connection.status" or "settings.language.get")) throw new ArgumentException("不支持的只读状态方法。");
        var result = await SendJsonAsync("api/dsh-im/" + channel, new
        {
            type = "client-request", rpcId = "bigfish-" + Guid.NewGuid().ToString("N"), method = "dsh-im/" + channel,
            payload = new { method, payload = new { } }
        }, ct);
        if (!result.TryGetProperty("result", out var rpc) || !rpc.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new BigFishException("plugin-load", $"{channel} 状态接口未返回成功；查看 DSH 设置中的诊断。");
        return rpc.Clone();
    }
    public async Task WaitForImAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try { await RpcAsync("dsh-im-language", "settings.language.get", deadline.Token); return; }
                catch (HttpRequestException e) when (e.StatusCode is null or HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable) { }
                await Task.Delay(500, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new BigFishException("plugin-load", "dsh-im 未在期限内激活，不能标记安装完成。"); }
    }
    public async Task<JsonElement> SendJsonAsync(string relative, object? body, CancellationToken ct)
    {
        var origin = BaseUri ?? throw new BigFishException("not-running", "请先启动 DSH。");
        var uri = new Uri(origin, relative);
        if (uri.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority)) throw new ArgumentException("非本机请求。");
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, uri);
        request.Headers.Add("Origin", origin.GetLeftPart(UriPartial.Authority));
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); return doc.RootElement.Clone();
    }
    public async Task<JsonElement> CallAsync(string endpoint, object arguments, CancellationToken ct)
    {
        if (endpoint is not ("settings/describe" or "credentials/describe" or "credentials/set" or "workspace/create"))
            throw new ArgumentException("不支持的配置操作。");
        var id = "bigfish-" + Guid.NewGuid().ToString("N");
        var response = await SendJsonAsync("api/" + endpoint, new { type = "client-request", rpcId = id,
            method = endpoint, payload = new { args = arguments } }, ct);
        if (!response.TryGetProperty("rpcId", out var correlation) || correlation.GetString() != id)
            throw new BigFishException("configuration", "DSH 配置响应不匹配，操作未确认。");
        var result = response.GetProperty("result");
        if (!result.GetProperty("ok").GetBoolean())
            // Do not echo an upstream error body: a rejected secret may appear in validation details.
            throw new BigFishException("configuration", "DSH 拒绝此配置操作，请检查本机模型设置；未记录请求内容。");
        return result.TryGetProperty("value", out var value) ? value.Clone() : default;
    }
    public async Task<IReadOnlyList<string>> ConnectionStatusAsync(CancellationToken ct)
    {
        var rows = new List<string>();
        foreach (var (channel, label) in new[] { ("qq", "QQ"), ("weixin", "微信") })
        {
            try
            {
                var rpc = await RpcAsync(channel, "connection.status", ct);
                var value = rpc.GetProperty("value");
                // Report only counts, never account identifiers, credentials, or message content.
                var array = value.TryGetProperty("bots", out var bots) ? bots : value.TryGetProperty("accounts", out var accounts) ? accounts : default;
                if (array.ValueKind != JsonValueKind.Array) { rows.Add($"{label}：接口正常，状态结构有变化，请在 IM机器人 页面查看。"); continue; }
                var connected = array.EnumerateArray().Count(b => b.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True);
                rows.Add($"{label}：已配置 {array.GetArrayLength()} 个，连接 {connected} 个。" + (array.GetArrayLength() == 0 ? "请先扫码或绑定机器人凭据。" : "收发测试仍需在对应平台手动完成。"));
            }
            catch (Exception e) when (e is not OperationCanceledException) { rows.Add($"{label}：状态读取失败（{ErrorClassifier.Code(e)}），请查看插件页面。"); }
        }
        return rows;
    }
    public void Dispose() => http.Dispose();
}
