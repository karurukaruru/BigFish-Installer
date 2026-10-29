using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace BigFish.Core;

public interface IArtifactDownloader
{
    Task DownloadAsync(Uri uri, string destination, string sha256, IProgress<double>? progress, CancellationToken ct);
}
public sealed class VerifiedDownloader(HttpClient http, LogManager log) : IArtifactDownloader
{
    public static void ValidateSource(Uri uri)
    {
        if (uri.Scheme != "https" || !new[] { "nodejs.org", "files.pythonhosted.org", "registry.npmjs.org", "npmmirror.com", "cdn.npmmirror.com", "registry.npmmirror.com", "pypi.tuna.tsinghua.edu.cn", "mirrors.tuna.tsinghua.edu.cn" }.Contains(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)) throw new BigFishException("untrusted-source", "下载来源未在白名单中。");
    }
    public async Task DownloadAsync(Uri uri, string destination, string sha256, IProgress<double>? progress, CancellationToken ct)
    {
        ValidateSource(uri);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination) && await MatchesAsync(destination, sha256, ct)) return;
        var partial = destination + ".partial";
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                // Interrupted transfers restart safely; only fully verified cache entries are reused.
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                ValidateSource(response.RequestMessage!.RequestUri!);
                response.EnsureSuccessStatusCode();
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
                {
                    byte[] buffer = new byte[81920]; long received = 0;
                    while (await input.ReadAsync(buffer, timeout.Token) is var n && n > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, n), timeout.Token); received += n;
                        if (response.Content.Headers.ContentLength is > 0) progress?.Report((double)received / response.Content.Headers.ContentLength.Value);
                    }
                }
                if (!await MatchesAsync(partial, sha256, ct)) throw new BigFishException("integrity", "SHA-256 完整性校验失败。");
                File.Move(partial, destination, true); return;
            }
            catch (Exception e) when (attempt < 3 && !ct.IsCancellationRequested && IsTransient(e))
            { log.Write($"下载重试 {attempt}/2：{e.GetType().Name}"); await Task.Delay(TimeSpan.FromSeconds(attempt), ct); }
            catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
            { throw new TimeoutException("下载在有界重试后仍然超时。", e); }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
    }
    private static bool IsTransient(Exception e) => e is IOException or OperationCanceledException ||
        e is HttpRequestException h && (h.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)h.StatusCode >= 500);
    public static async Task<bool> MatchesAsync(string path, string expected, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
    public static void ExtractSafe(string zip, string destination, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('\\', '/')));
            if (!AppPaths.IsWithin(destination, target)) throw new BigFishException("invalid-archive", "压缩包包含不安全路径。");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new BigFishException("invalid-archive", "压缩包包含符号链接。");
            if (entry.Name.Length == 0) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target);
        }
    }
}
