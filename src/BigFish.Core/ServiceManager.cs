using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace BigFish.Core;

public interface IServiceManager : IAsyncDisposable
{
    bool IsRunning { get; }
    Uri? LaunchUri { get; }
    HealthChecker? Health { get; }
    Task StartAsync(NodeRuntime node, string release, bool requireIm, CancellationToken ct);
    Task StopAsync();
}
public sealed class ServiceManager(AppPaths paths, LogManager log, TimeSpan? startupTimeout = null) : IServiceManager
{
    private Process? process;
    private WindowsJob? job;
    private FileStream? serviceLock;
    private Task? stdout, stderr;
    public bool IsRunning => process is { HasExited: false };
    // Secret lives only in memory. It must never be serialized or displayed in the log UI.
    public Uri? LaunchUri { get; private set; }
    public HealthChecker? Health { get; private set; }
    public static int ChoosePort(int preferred)
    {
        try { var listener = new TcpListener(IPAddress.Loopback, preferred); listener.Start(); listener.Stop(); return preferred; }
        catch (SocketException) { return 0; } // OS selects atomically; no scan-and-race on a guessed alternative.
    }
    public async Task StartAsync(NodeRuntime node, string release, bool requireIm, CancellationToken ct)
    {
        if (IsRunning)
        {
            if (requireIm) await Health!.WaitForImAsync(TimeSpan.FromSeconds(45), ct);
            return;
        }
        await StopAsync();
        try { serviceLock = new FileStream(Path.Combine(paths.Root, ".service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new BigFishException("busy", "此安装目录已有运行中的 BigFish 服务，请在原窗口管理。", e); }
        try
        {
            var port = ChoosePort(3080);
            if (port == 0) log.Write("3080 已占用；不接管无法证明版本、目录及归属的服务，改由操作系统分配本机端口。");
            var wrapper = Path.Combine(paths.Root, "service-launcher.mjs");
            var policy = Path.Combine(paths.Root, "privacy.patch.yml");
            await AtomicFile.WriteAsync(policy, """
                - id: session-log-deepseek
                  config:
                    enabled: false
                """, ct);
            await AtomicFile.WriteAsync(wrapper, """
                import {pathToFileURL} from 'node:url';
                const [cli,...args] = process.argv.slice(2);
                process.argv = [process.execPath,cli,...args];
                process.stdin.resume();
                const shutdown = () => { if(process.listenerCount('SIGINT')) process.emit('SIGINT'); else process.exit(0); };
                process.stdin.on('end', shutdown);
                process.stdin.on('data', data => { if(data.toString().trim()==='BIGFISH_STOP') shutdown(); });
                const {runCli} = await import(pathToFileURL(cli).href);
                await runCli();
                """, ct);
            var command = new Command(node.Executable, [wrapper, DshInstaller.Cli(release), "--profile", "web", "--patch", policy, "--no-open", "--host", "127.0.0.1", "--port", port.ToString()], paths.Workspace,
                RuntimeEnvironment.Create(paths, node, release));
            process = new Process { StartInfo = ProcessRunner.StartInfo(command, true), EnableRaisingEvents = true };
            var ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, _) => ready.TrySetException(new BigFishException("startup-exited", "DSH 在就绪之前退出；查看脱敏日志。"));
            try { process.Start(); }
            catch { process.Dispose(); process = null; throw; }
            job = WindowsJob.TryAssign(process);
            async Task Pump(StreamReader reader)
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    var match = Regex.Match(line, @"dsh web: (http://[^\s]+)");
                    if (match.Success && Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var uri) && HealthChecker.IsLocalLaunchUri(uri)) ready.TrySetResult(uri);
                    log.Write(line);
                }
            }
            stdout = Pump(process.StandardOutput); stderr = Pump(process.StandardError);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(startupTimeout ?? TimeSpan.FromSeconds(120));
            try
            {
                LaunchUri = await ready.Task.WaitAsync(deadline.Token);
                Health = new HealthChecker(); await Health.AuthenticateAsync(LaunchUri, deadline.Token);
                if (requireIm) await Health.WaitForImAsync(TimeSpan.FromSeconds(60), deadline.Token);
                if (!IsRunning) throw new BigFishException("startup-exited", "DSH 在健康检查后退出。");
                log.Write("本机服务通过认证和 HTTP 就绪检查。" + (requireIm ? "dsh-im 实际 RPC 已响应。" : ""));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new BigFishException("startup-timeout", "DSH 启动超时。"); }
        }
        catch { await StopAsync(); throw; }
    }
    public async Task StopAsync()
    {
        try
        {
            if (process is { } p)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        try { await p.StandardInput.WriteLineAsync("BIGFISH_STOP"); await p.StandardInput.FlushAsync(); } catch (IOException) { }
                        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        try { await p.WaitForExitAsync(grace.Token); }
                        catch (OperationCanceledException) { log.Write("服务未在 8 秒内退出，终止 BigFish 所拥有的进程树。"); ProcessRunner.TryKill(p); await p.WaitForExitAsync(); }
                    }
                    if (stdout is not null && stderr is not null) await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    ProcessRunner.TryKill(p); job?.Dispose(); job = null;
                    p.Dispose(); process = null; stdout = null; stderr = null;
                }
            }
        }
        finally
        {
            Health?.Dispose(); Health = null; LaunchUri = null;
            serviceLock?.Dispose(); serviceLock = null;
        }
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
