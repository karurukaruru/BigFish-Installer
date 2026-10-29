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
    private bool busy, shuttingDown, keyPresent;
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
    public bool CanConfigure => !busy && controller?.Service.IsRunning == true;
    public bool CanSaveKey => CanConfigure && keyPresent;
    public bool KeyPresent { set { keyPresent = value; Changed(nameof(CanSaveKey)); } }
    private int selectedTab;
    public int SelectedTab { get => selectedTab; set => Set(ref selectedTab, value); }
    private string status = "正在准备安装器……", stage = "等待安装", detail = "", steps = "", logs = "";
    private string guidance = "安装完成后，填入 API Key，再按需要绑定 QQ 或微信。";
    private string apiStatus = "尚未检查。已有密钥不会显示在这里；留空即可保留。";
    private double percent;
    private bool indeterminate;
    public string Status { get => status; private set => Set(ref status, value); }
    public string Stage { get => stage; private set => Set(ref stage, value); }
    public string Detail { get => detail; private set => Set(ref detail, value); }
    public string StepCount { get => steps; private set => Set(ref steps, value); }
    public string Logs { get => logs; private set => Set(ref logs, value); }
    public string Guidance { get => guidance; private set => Set(ref guidance, value); }
    public string ApiStatus { get => apiStatus; private set => Set(ref apiStatus, value); }
    public double ProgressPercent { get => percent; private set => Set(ref percent, value); }
    public bool Indeterminate { get => indeterminate; private set => Set(ref indeterminate, value); }
    public string ServiceStatus => controller?.Service.IsRunning == true ? "✓ 本机服务运行中" : "本机服务未运行";
    public string Versions => controller is { } c ? $"DSH {c.Manifest.Dsh.Version} · dsh-im {c.Manifest.Im.Version} · {c.Paths.Sources.DisplayName}" : "";
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

    public MainViewModel(AppPaths? initialPaths = null, bool loadPreferences = true)
    {
        var defaults = initialPaths ?? new AppPaths(); rootPath = defaults.Root; workspacePath = defaults.Workspace;
        PreferChinaMirror = defaults.Sources.Region == DownloadRegion.China;
        if (loadPreferences && File.Exists(PreferencesFile))
        {
            try
            {
                var p = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PreferencesFile));
                if (p is not null)
                {
                    rootPath = p.GetValueOrDefault("root", rootPath); workspacePath = p.GetValueOrDefault("workspace", workspacePath);
                    if (p.TryGetValue("region", out var region)) PreferChinaMirror = region != "global";
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            { status = "原目录设置无法读取，使用默认位置；原文件保留。"; }
        }
        InstallCommand = Action(async ct =>
        {
            if (!AppPaths.SupportedPlatform) throw new BigFishException("platform", "支持 Windows 10 22H2 / Windows 11 x64。");
            SelectedTab = 0;
            var progress = new Progress<InstallProgress>(p =>
            {
                Stage = p.Stage; Detail = p.Detail; StepCount = $"已验证：{p.Completed}/{p.Total} 项";
                Indeterminate = !p.Fraction.HasValue && p.Completed < p.Total;
                ProgressPercent = p.Fraction.HasValue ? p.Fraction.Value * 100 : (double)p.Completed / p.Total * 100;
            });
            await Task.Run(() => controller!.Engine.InstallAsync(new(InstallIm, InstallBili, ForcePrivateNode), progress, ct), ct);
            Status = "安装成功。下一步：填入 API Key，然后按需要扫码绑定。";
            SelectedTab = 1; await RefreshApiStatusAsync(ct);
        });
        CheckCommand = Action(async ct =>
        {
            Status = "正在检查环境和网络……"; Checks.Clear();
            foreach (var item in await Task.Run(() => controller!.CheckAsync(ct, InstallBili), ct)) Checks.Add(item);
            Status = "检查完成。点击一键安装可补齐依赖。";
        });
        StartCommand = Action(async ct => { await controller!.StartAsync(ct); await RefreshApiStatusAsync(ct); Status = "已启动，可继续配置或打开大肥鱼。"; });
        StopCommand = Action(async _ => { await controller!.Service.StopAsync(); Status = "已停止，配置和会话保留。"; });
        RestartCommand = Action(async ct => { await controller!.Service.StopAsync(); await controller.StartAsync(ct); Status = "已重启。"; });
        UpdateCommand = Action(async ct => { Guidance = string.Join("\n", await controller!.CheckUpdatesAsync(ct)); SelectedTab = 2; });
        TestCommand = Action(async ct =>
        {
            var health = RequireHealth();
            Guidance = string.Join("\n", await health.ConnectionStatusAsync(ct)) + "\n请在 DSH 发送一条消息测试模型，再在已绑定平台手动测试收发。";
            await RefreshApiStatusAsync(ct); SelectedTab = 1;
        });
        ApplyPathsCommand = Action(async ct =>
        {
            var paths = new AppPaths(RootPath, WorkspacePath, PreferChinaMirror ? DownloadRegion.China : DownloadRegion.Global);
            var next = await Task.Run(() => new BigFishController(paths), ct);
            try { if (controller is not null) await controller.DisposeAsync(); }
            catch { await next.DisposeAsync(); throw; }
            Attach(next);
            await AtomicFile.WriteAsync(PreferencesFile, JsonSerializer.Serialize(new { root = paths.Root, workspace = paths.Workspace, region = PreferChinaMirror ? "cn" : "global" }), ct);
            Status = "目录和下载源已应用；原目录数据保留。";
        }, requireController: false);
        OpenWebCommand = Simple(OpenWeb);
        ModelCommand = Simple(() => Guide("其他服务商或模型：在 DSH 设置 → 模型 中配置。DeepSeek 密钥可以直接在本页保存。"));
        QqCommand = Simple(() => Guide("在打开的 DSH 页面进入 设置 → IM机器人 → QQ，按官方机器人创建/扫码或已有凭据流程绑定。群聊能力取决于平台授权。"));
        WeChatCommand = Simple(() => Guide("在打开的 DSH 页面进入 设置 → IM机器人 → 微信，生成二维码后用获准账号扫码确认；没有相关权限时不能强行接入。"));
        WorkspaceCommand = Simple(() => ShellOpen(controller!.Paths.Workspace));
        LogFolderCommand = Simple(() => ShellOpen(controller?.Paths.Logs ?? Path.Combine(RootPath, "logs")));
        CancelCommand = Simple(() => { cancellation?.Cancel(); Status = "正在取消，请等待后台进程退出。"; }, () => busy);
        AdvancedCommand = Simple(() => SelectedTab = 3); LogsCommand = Simple(() => SelectedTab = 3);
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; timer.Tick += (_, _) => Refresh(); timer.Start();
    }
    private HealthChecker RequireHealth() => controller?.Service.Health ?? throw new BigFishException("not-running", "请先在安装页完成安装，或在管理页点击启动。");
    public Task SaveApiKeyAsync(string key) => Run(async ct =>
    {
        await new DshSetupClient(RequireHealth()).SaveApiKeyAsync(key, ct);
        ApiStatus = "✓ 密钥已由 DSH 保存。有效性和余额尚未验证，请打开 DSH 发送一条消息测试。";
        Status = "API Key 已保存；可以继续扫码绑定。";
    });
    private async Task RefreshApiStatusAsync(CancellationToken ct)
    {
        try { ApiStatus = await new DshSetupClient(RequireHealth()).HasApiKeyAsync(ct) ? "✓ 已有密钥，留空即可保留；尚未验证有效性或余额。" : "尚未配置，请在下方填入自己的 DeepSeek API Key。"; }
        catch (Exception e) when (e is not OperationCanceledException) { ApiStatus = "暂时无法读取密钥配置状态；可打开 DSH 模型设置检查。"; }
    }
    private void Attach(BigFishController c)
    {
        controller = c;
        c.Log.Line += line => Application.Current.Dispatcher.BeginInvoke(() => { Logs += line + "\n"; if (Logs.Length > 70000) Logs = Logs[^60000..]; });
        Changed(nameof(Versions)); Refresh();
    }
    public Task InitializeAsync() => Run(async ct =>
    {
        Attach(await Task.Run(() => new BigFishController(new AppPaths(RootPath, WorkspacePath, PreferChinaMirror ? DownloadRegion.China : DownloadRegion.Global)), ct));
        // Fast local scan only. External connectivity is checked on explicit request / installation.
        foreach (var row in await controller!.Detector.ScanAsync(await controller.Store.LoadAsync(ct), ct)) Checks.Add(row);
        Status = "准备就绪。点击一键安装；已有安装可到管理页启动。";
    });
    private ICommand Action(Func<CancellationToken, Task> action, bool requireController = true)
    {
        var command = new AsyncCommand(() => Run(action), () => !busy && !shuttingDown && (!requireController || controller is not null)); commands.Add(command); return command;
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
            try { controller?.Log.Write($"操作失败 [{ErrorClassifier.Code(e)}]：{e.Message}"); } catch (IOException) { }
            Stage = e is OperationCanceledException ? "已取消" : "操作未完成";
            // The concrete error is visible on every page, not hidden behind the progress tab.
            Status += " " + Detail;
        }
        finally { busy = false; Indeterminate = false; cancellation.Dispose(); cancellation = null; Refresh(); }
    }
    private void Refresh()
    {
        Changed(nameof(CanEdit)); Changed(nameof(ServiceStatus)); Changed(nameof(CanConfigure)); Changed(nameof(CanSaveKey));
        foreach (var command in commands) command.Refresh();
    }
    private void Guide(string text) { Guidance = text; SelectedTab = 1; OpenWeb(); }
    private void OpenWeb()
    {
        if (controller?.Service.IsRunning != true || controller.Service.LaunchUri is not { } uri) throw new BigFishException("not-running", "请先完成安装或启动大肥鱼。");
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
