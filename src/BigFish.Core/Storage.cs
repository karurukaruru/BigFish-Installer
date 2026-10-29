using System.Text.Json;
using System.Text.RegularExpressions;

namespace BigFish.Core;

public static class AtomicFile
{
    public static async Task WriteAsync(string path, string content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content, ct);
            ct.ThrowIfCancellationRequested();
            // Same volume rename; a failure leaves the previous file intact.
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
public sealed class InstallationStateStore(string path)
{
    public async Task<InstallState> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path)) return new();
        try
        {
            var state = JsonSerializer.Deserialize<InstallState>(await File.ReadAllTextAsync(path, ct), Json.Options)
                ?? throw new JsonException("状态为空");
            if (state.Schema != 1) throw new BigFishException("invalid-state", "状态文件来自其他版本，未覆盖。请备份后检查。");
            return state;
        }
        catch (JsonException e) { throw new BigFishException("invalid-state", "安装状态损坏。原文件已保留，不会自动覆盖。", e); }
    }
    public Task SaveAsync(InstallState state, CancellationToken ct = default) =>
        AtomicFile.WriteAsync(path, JsonSerializer.Serialize(state with { UpdatedAt = DateTimeOffset.UtcNow }, Json.Options), ct);
}

public sealed class ConfigurationManager(AppPaths paths)
{
    public async Task EnsureChineseDefaultsAsync(CancellationToken ct)
    {
        var file = Path.Combine(paths.Home, "settings.yaml");
        if (File.Exists(file))
        {
            var existing = await File.ReadAllTextAsync(file, ct);
            if (existing.Replace("\r\n", "\n").Trim() != "locale:\n  preference: zh-CN") return;
            // Migrate only our exact 0.1.1 template; never change an existing user's locale/config.
            File.Copy(file, file + ".pre-0.1.2-" + Guid.NewGuid().ToString("N"));
        }
        await AtomicFile.WriteAsync(file, "locale:\n  preference: zh\n", ct);
    }
    public async Task<string> BackupProfileAsync(CancellationToken ct)
    {
        var dest = Path.Combine(paths.Root, "backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dest);
        // Only configuration and lockfiles: never copy enormous node_modules or touch sessions.
        foreach (var name in new[] { "package.json", "pnpm-lock.yaml", "pnpm-workspace.yaml", "cordis.patch.yml" })
        {
            ct.ThrowIfCancellationRequested();
            var source = Path.Combine(paths.Profile, name);
            if (File.Exists(source)) await File.WriteAllBytesAsync(Path.Combine(dest, name), await File.ReadAllBytesAsync(source, ct), ct);
        }
        return dest;
    }
    public Task EnsureBiliSafeDefaultsAsync(CancellationToken ct)
    {
        var p = Path.Combine(paths.Home, "bili-duty", "config.json");
        // Only create first-run defaults. Existing user choices are never overwritten.
        if (File.Exists(p)) return Task.CompletedTask;
        return AtomicFile.WriteAsync(p, """
            { "peakValley": false, "autoComment": false, "replyToComments": false,
              "dmMode": "off", "dmUid": 0, "transcribe": false, "commentPerRound": 0 }
            """, ct);
    }
}

public sealed class LogManager
{
    private readonly object gate = new();
    private readonly string directory;
    public event Action<string>? Line;
    public string FilePath { get; }
    public LogManager(string directory)
    {
        this.directory = directory;
        FilePath = Path.Combine(directory, $"bigfish-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
    }
    public static string Redact(string value)
    {
        value = Regex.Replace(value, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        value = Regex.Replace(value, @"(?im)^(\s*(?:Cookie|Set-Cookie|Authorization)\s*:)[^\r\n]*", "$1 [REDACTED]");
        value = Regex.Replace(value, @"(?i)([?&](?:token|key|secret|code|password|access_token)=)[^\s&#]+", "$1[REDACTED]");
        value = Regex.Replace(value, @"(?i)(?:Bearer\s+)[^\s,;]+", "Bearer [REDACTED]");
        value = Regex.Replace(value, @"\bsk-[A-Za-z0-9_-]+", "[REDACTED]");
        value = Regex.Replace(value, "(?i)([\\\"']?(?:api[_-]?key|token|password|secret|cookie|authorization|clientSecret|appSecret|accessToken)[\\\"']?\\s*[:=]\\s*)(?:[\\\"'][^\\\"']*[\\\"']|[^\\s,;}]+)", "$1[REDACTED]");
        value = Regex.Replace(value, @"(https?://)[^/@\s]+:[^/@\s]+@", "$1[REDACTED]@");
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (userHome.Length > 3) value = value.Replace(userHome, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        return value;
    }
    public void Write(string text)
    {
        var line = $"{DateTimeOffset.Now:O} {Redact(text)}";
        lock (gate)
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(FilePath, line + Environment.NewLine);
        }
        Line?.Invoke(line);
    }
}

public sealed class OperationLock : IDisposable
{
    private readonly FileStream stream;
    public OperationLock(AppPaths paths)
    {
        Directory.CreateDirectory(paths.Root);
        try { stream = new FileStream(Path.Combine(paths.Root, ".operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new BigFishException("busy", "此安装目录正在被其他 BigFish 操作使用。", e); }
    }
    public void Dispose() => stream.Dispose();
}
