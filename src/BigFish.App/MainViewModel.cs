using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BigFish.Core;

namespace BigFish.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private BigFishController? controller;
    private CancellationTokenSource? cancellation;
    private Task? activeTask;
    private bool busy, shuttingDown;
    private readonly DispatcherTimer timer;
    private readonly List<AsyncCommand> commands = [];
    private static string PreferencesFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BigFish", "launcher-settings.json");
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { field = value; Changed(name); }
    private string rootPath, workspacePath;
    public string RootPath { get => rootPath; set => Set(ref rootPath, value); }
    public string WorkspacePath { get => workspacePath; set => Set(ref workspacePath, value); }
    public bool InstallIm { get; set; } = true;
    public bool InstallBili { get; set; }
    public bool PreferChinaMirror { get; set; } = DownloadSources.BuildDefault == DownloadRegion.China;
    public bool ForcePrivateNode { get; set; }
    public bool CanEdit => !busy;
    private int selectedTab;
    public int SelectedTab { get => selectedTab; set => Set(ref selectedTab, value); }
    private string status = "等待检查环境", stage = "准备安装", detail = "", steps = "", logs = "", guidance = "安装成功后：设置 → 模型配置 API Key；设置 → IM机器人 绑定 QQ 或微信。";
    private double percent;
    private bool indeterminate;
    public string Status { get => status; private set => Set(ref status, value); }
    public string Stage { get => stage; private set => Set(ref stage, value); }
    public string Detail { get => detail; private set => Set(ref detail, value); }
    public string StepCount { get => steps; private set => Set(ref steps, value); }
    public string Logs { get => logs; private set => Set(ref logs, value); }
    public string Guidance { get => guidance; private set => Set(ref guidance, value); }
    public double ProgressPercent { get => percent; private set => Set(ref percent, value); }
    public bool Indeterminate { get => indeterminate; private set => Set(ref indeterminate, value); }
    public string ServiceStatus => controller?.Service.IsRunning == true ? "✓ 本机服务运行中" : "本机服务未运行";
    public string Versions => controller is { } c ? $"审核组合：Node {c.Manifest.NodeVersion} · DSH {c.Manifest.Dsh.Version} · dsh-im {c.Manifest.Im.Version} · {c.Paths.Sources.DisplayName}" : "";
    public ObservableCollection<CheckResult> Checks { get; } = [];
    public ICommand InstallCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand ApplyPathsCommand { get; }
    public ICommand OpenWebCommand { get; }
    public ICommand ModelCommand { get; }
    public ICommand QqCommand { get; }
    public ICommand WeChatCommand { get; }
    public ICommand WorkspaceCommand { get; }
    public ICommand LogFolderCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand AdvancedCommand { get; }
    public ICommand LogsCommand { get; }

    public MainViewModel()
    {
        var defaults = new AppPaths(); rootPath = defaults.Root; workspacePath = defaults.Workspace;
        if (File.Exists(PreferencesFile))
        {
            try
            {
                var preferences = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PreferencesFile));
                if (preferences is not null) { rootPath = preferences.GetValueOrDefault("root", rootPath); workspacePath = preferences.GetValueOrDefault("workspace", workspacePath); if (preferences.TryGetValue("region", out var region)) PreferChinaMirror = region != "global"; }
            }
            catch (Exception e) when (e is JsonException or IOException) { status = "目录设置无法读取，已使用默认位置；原文件保留。"; }
        }
        InstallCommand = Action(async ct =>
        {
            if (!AppPaths.SupportedPlatform) throw new BigFishException("platform", "支持 Windows 10 22H2 / Windows 11 x64。");
            SelectedTab = 2;
            var progress = new Progress<InstallProgress>(p =>
            {
                Stage = p.Stage; Detail = p.Detail; StepCount = $"已验证完成：{p.Completed}/{p.Total} 项";
                Indeterminate = !p.Fraction.HasValue && p.Completed < p.Total;
                ProgressPercent = p.Fraction.HasValue ? p.Fraction.Value * 100 : (double)p.Completed / p.Total * 100;
            });
            var options = new InstallOptions(InstallIm, InstallBili, ForcePrivateNode);
            await Task.Run(() => controller!.Engine.InstallAsync(options, progress, ct), ct);
            Status = "安装与本机验证通过；模型配置和扫码授权尚未完成。"; SelectedTab = 3; OpenWeb();
        });
        CheckCommand = Action(CheckAsync);
        StartCommand = Action(async ct => { await controller!.StartAsync(ct); Status = "本机服务启动成功。"; OpenWeb(); });
        StopCommand = Action(async _ => { await controller!.Service.StopAsync(); Status = "服务已停止，配置及会话保留。"; });
        RestartCommand = Action(async ct => { await controller!.Service.StopAsync(); await controller.StartAsync(ct); Status = "服务已重启。"; });
        UpdateCommand = Action(async ct => { Guidance = string.Join("\n", await controller!.CheckUpdatesAsync(ct)); SelectedTab = 3; });
        TestCommand = Action(async ct =>
        {
            var health = controller!.Service.Health ?? throw new BigFishException("not-running", "请先启动 DSH。");
            Guidance = string.Join("\n", await health.ConnectionStatusAsync(ct)) + "\n发送一条测试消息并观察回复；安装器不会自动发消息或消耗模型余额。";
        });
        ApplyPathsCommand = Action(async ct =>
        {
            var paths = new AppPaths(RootPath, WorkspacePath, PreferChinaMirror ? DownloadRegion.China : DownloadRegion.Global); await Task.Run(paths.Prepare, ct);
            if (controller is not null) await controller.DisposeAsync();
            Attach(new BigFishController(paths));
            await AtomicFile.WriteAsync(PreferencesFile, JsonSerializer.Serialize(new { root = paths.Root, workspace = paths.Workspace, region = PreferChinaMirror ? "cn" : "global" }), ct);
            Status = "目录和下载源已应用。已有数据未移动，请重新检查环境。";
        });
        OpenWebCommand = Simple(OpenWeb);
        ModelCommand = Simple(() => Guide("在 DSH 中打开「设置 → 模型」，输入 DeepSeek API Key 并保存。密钥只交给本机 DSH，BigFish 不读取或记录它。若提示 401 检查密钥；余额不足请在服务商账户中确认。"));
        QqCommand = Simple(() => Guide("打开「设置 → IM机器人 → QQ」，按插件引导创建官方机器人并扫码，或填写已有机器人的凭据。群聊能力由 QQ 平台权限决定，普通个人账号不会因此自动获得机器人权限。"));
        WeChatCommand = Simple(() => Guide("打开「设置 → IM机器人 → 微信」，生成二维码并用获准的账号扫码。二维码失效可重新生成；账号无相关权限时需按平台规则申请，安装器无法绕过权限。"));
        WorkspaceCommand = Simple(() => ShellOpen(controller!.Paths.Workspace));
        LogFolderCommand = Simple(() => ShellOpen(controller!.Paths.Logs));
        CancelCommand = Simple(() => { cancellation?.Cancel(); Status = "正在安全取消，请等待后台进程退出。"; }, () => busy);
        AdvancedCommand = Simple(() => SelectedTab = 4); LogsCommand = Simple(() => SelectedTab = 5);
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; timer.Tick += (_, _) => Changed(nameof(ServiceStatus)); timer.Start();
    }
    private void Attach(BigFishController c)
    {
        controller = c;
        c.Log.Line += line => Application.Current.Dispatcher.BeginInvoke(() =>
        { Logs = (Logs + line + "\n"); if (Logs.Length > 70000) Logs = Logs[^60000..]; });
        Changed(nameof(Versions)); Changed(nameof(ServiceStatus));
    }
    public Task InitializeAsync() => Run(async ct =>
    {
        Attach(await Task.Run(() => new BigFishController(new AppPaths(RootPath, WorkspacePath, PreferChinaMirror ? DownloadRegion.China : DownloadRegion.Global)), ct));
        await CheckAsync(ct);
    });
    private async Task CheckAsync(CancellationToken ct)
    {
        SelectedTab = 1; Checks.Clear(); Status = "正在执行环境检查……";
        foreach (var item in await Task.Run(() => controller!.CheckAsync(ct, InstallBili), ct)) Checks.Add(item);
        Status = "检查完成；可点击一键安装自动补齐依赖。";
    }
    private ICommand Action(Func<CancellationToken, Task> action)
    {
        var command = new AsyncCommand(() => Run(action), () => !busy && !shuttingDown); commands.Add(command); return command;
    }
    private ICommand Simple(Action action, Func<bool>? can = null)
    {
        var command = new AsyncCommand(() => { try { action(); } catch (Exception e) { Status = LogManager.Redact(e.Message); } return Task.CompletedTask; }, can ?? (() => !busy && !shuttingDown)); commands.Add(command); return command;
    }
    private Task Run(Func<CancellationToken, Task> action)
    {
        if (busy || shuttingDown) return Task.CompletedTask;
        activeTask = ExecuteAsync(action); return activeTask;
    }
    private async Task ExecuteAsync(Func<CancellationToken, Task> action)
    {
        busy = true; cancellation = new(); Refresh();
        try { await action(cancellation.Token); }
        catch (Exception e)
        {
            Status = ErrorClassifier.Hint(e); Detail = LogManager.Redact(e.Message);
            controller?.Log.Write($"操作失败 [{ErrorClassifier.Code(e)}]：{e.Message}");
            Stage = e is OperationCanceledException ? "已取消" : "需要处理";
        }
        finally { busy = false; Indeterminate = false; cancellation.Dispose(); cancellation = null; Refresh(); }
    }
    private void Refresh() { Changed(nameof(CanEdit)); Changed(nameof(ServiceStatus)); foreach (var command in commands) command.Refresh(); }
    private void Guide(string text) { Guidance = text; SelectedTab = 3; OpenWeb(); }
    private void OpenWeb()
    {
        if (controller?.Service.IsRunning != true || controller.Service.LaunchUri is not { } uri) throw new BigFishException("not-running", "请先启动 DSH。");
        ShellOpen(uri.AbsoluteUri);
    }
    private static void ShellOpen(string value) => Process.Start(new ProcessStartInfo(value) { UseShellExecute = true });
    public async Task ShutdownAsync()
    {
        shuttingDown = true; timer.Stop(); cancellation?.Cancel();
        if (activeTask is not null) await activeTask;
        if (controller is not null) await controller.DisposeAsync();
    }
}

internal sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute();
    public async void Execute(object? parameter) { if (CanExecute(parameter)) await execute(); }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
