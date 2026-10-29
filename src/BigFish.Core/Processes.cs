using System.Diagnostics;
using System.Text;

namespace BigFish.Core;

public sealed record Command(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null, TimeSpan? Timeout = null);
public sealed record CommandResult(int ExitCode, string Output)
{
    public void EnsureSuccess(string task)
    {
        if (ExitCode != 0) throw new BigFishException("process-failed", $"{task}失败（退出码 {ExitCode}）。\n{LogManager.Redact(Output)}");
    }
}
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(Command command, CancellationToken ct);
}
public sealed class ProcessRunner(LogManager log) : ICommandRunner
{
    public static ProcessStartInfo StartInfo(Command cmd, bool stdin = false)
    {
        var si = new ProcessStartInfo(cmd.FileName)
        {
            WorkingDirectory = cmd.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in cmd.Arguments) si.ArgumentList.Add(arg);
        // No inherited Node preload or npm configuration may inject execution into our isolated install.
        foreach (var key in si.Environment.Keys.Where(k => k.StartsWith("npm_", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("DSH_", StringComparison.OrdinalIgnoreCase) || k.Equals("NODE_OPTIONS", StringComparison.OrdinalIgnoreCase)
            || k.Equals("NODE_PATH", StringComparison.OrdinalIgnoreCase)).ToArray()) si.Environment.Remove(key);
        if (cmd.Environment is not null) foreach (var (k, v) in cmd.Environment) si.Environment[k] = v;
        return si;
    }
    public async Task<CommandResult> RunAsync(Command command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        log.Write($"执行 {Path.GetFileName(command.FileName)}（参数由 ArgumentList 传入）");
        using var process = new Process { StartInfo = StartInfo(command), EnableRaisingEvents = true };
        if (!process.Start()) throw new BigFishException("process-failed", "无法启动依赖进程。");
        using var job = WindowsJob.TryAssign(process);
        using var timeout = new CancellationTokenSource(command.Timeout ?? TimeSpan.FromMinutes(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var output = new StringBuilder(); var gate = new object();
        async Task Pump(StreamReader reader)
        {
            char[] buffer = new char[2048]; var pending = new StringBuilder(); var overlong = false;
            void Emit()
            {
                var s = overlong ? "[超长输出行已省略]" : LogManager.Redact(pending.ToString());
                pending.Clear(); overlong = false;
                lock (gate)
                {
                    output.AppendLine(s);
                    while (output.Length > 24000)
                    {
                        var end = output.ToString().IndexOf('\n'); if (end < 0) { output.Clear(); break; }
                        output.Remove(0, end + 1);
                    }
                }
                log.Write(s);
            }
            while (await reader.ReadAsync(buffer) is var n && n > 0)
            {
                for (var i = 0; i < n; i++)
                {
                    if (buffer[i] == '\n') { Emit(); continue; }
                    if (pending.Length < 16000) pending.Append(buffer[i]); else overlong = true;
                }
            }
            if (pending.Length > 0 || overlong) Emit();
        }
        var stdout = Pump(process.StandardOutput); var stderr = Pump(process.StandardError);
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            TryKill(process); await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(command.FileName)} 执行超时。");
        }
        await Task.WhenAll(stdout, stderr);
        var result = LogManager.Redact(output.ToString());
        return new(process.ExitCode, result);
    }
    internal static void TryKill(Process p) { try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { } }
}

// Kill descendants if BigFish crashes or exits: no orphan background bot after closing the window.
internal sealed class WindowsJob : IDisposable
{
    private IntPtr handle;
    private WindowsJob(IntPtr handle) => this.handle = handle;
    public static WindowsJob? TryAssign(Process p)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var h = Native.CreateJobObject(IntPtr.Zero, null);
        if (h == IntPtr.Zero) { var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error(); ProcessRunner.TryKill(p); throw new System.ComponentModel.Win32Exception(error); }
        var info = new Native.ExtendedLimitInformation(); info.BasicLimitInformation.LimitFlags = 0x2000;
        var size = System.Runtime.InteropServices.Marshal.SizeOf(info);
        var ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(info, ptr, false);
            if (!Native.SetInformationJobObject(h, 9, ptr, (uint)size) || !Native.AssignProcessToJobObject(h, p.Handle))
            { var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error(); Native.CloseHandle(h); ProcessRunner.TryKill(p); throw new System.ComponentModel.Win32Exception(error); }
            return new(h);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr); }
    }
    public void Dispose() { if (handle != IntPtr.Zero) Native.CloseHandle(handle); handle = IntPtr.Zero; }
    private static class Native
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation; public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(IntPtr job, int kind, IntPtr info, uint size);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}

public static class RuntimeEnvironment
{
    public static Dictionary<string, string> Create(AppPaths paths, NodeRuntime node, string release, string? registry = null)
    {
        registry ??= paths.Sources.Registry;
        var config = Path.Combine(paths.Root, paths.Sources.Region == DownloadRegion.China ? "npmrc-cn" : "npmrc-global");
        if (!File.Exists(config)) File.WriteAllText(config, $"registry={registry}\naudit=false\nfund=false\nupdate-notifier=false\nfetch-retries=2\nfetch-timeout=120000\n");
        var global = Path.Combine(paths.Root, "npm-globalrc");
        if (!File.Exists(global)) File.WriteAllText(global, "");
        return new()
        {
            ["DSH_HOME"] = paths.Home,
            ["DSH_AGENTS_HOME"] = Path.Combine(paths.Home, "agents"),
            ["DSH_TELEMETRY_DISABLED"] = "1",
            ["DSH_TELEMETRY_MODE"] = "DISABLED",
            ["npm_config_userconfig"] = config, ["npm_config_globalconfig"] = global,
            ["npm_config_cache"] = Path.Combine(paths.Root, "npm-cache"),
            ["npm_config_registry"] = registry,
            ["npm_config_update_notifier"] = "false", ["COREPACK_ENABLE_DOWNLOAD_PROMPT"] = "0",
            ["PATH"] = Path.GetDirectoryName(node.Executable) + Path.PathSeparator + Path.Combine(release, "node_modules", ".bin")
                + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
        };
    }
}
