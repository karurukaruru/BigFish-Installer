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
    private string status = "", stage = "", detail = "", steps = "", logs = "";
    private string guidance = "", apiStatus = "";
    private string statusKey = "StatusPreparing", apiKey = "ApiDefault";
    private string? guidanceKey = "GuidanceDefault";
    private object[] statusArgs = [];
    private string? errorDetail;
    private string rawStage = "等待安装", rawDetail = "";
    private int stepCompleted, stepTotal;
    private bool haveSteps;
    private readonly List<CheckResult> rawChecks = [];
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
    public string ServiceStatus => controller?.Service.IsRunning == true ? Ui.Text["ServiceRunning"] : Ui.Text["ServiceStopped"];
    public string Versions => controller is { } c ? $"DSH {c.Manifest.Dsh.Version} · dsh-im {c.Manifest.Im.Version} · {Ui.Text.Map(c.Paths.Sources.DisplayName)}" : "";
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
                    if (p.TryGetValue("language", out var language)) Ui.Text.SetLanguage(language);
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            { statusKey = "PrefsUnreadable"; }
        }
        PublishLiveText();
        InstallCommand = Action(async ct =>
        {
            if (!AppPaths.SupportedPlatform) throw new BigFishException("platform", Ui.Text["Platform"]);
            await UseCurrentPathsAsync(ct);
            SelectedTab = 0;
            var progress = new Progress<InstallProgress>(p =>
            {
                rawStage = p.Stage; rawDetail = p.Detail; Stage = Ui.Text.Map(rawStage); Detail = Ui.Text.Map(rawDetail);
                stepCompleted = p.Completed; stepTotal = p.Total; haveSteps = true;
                StepCount = string.Format(Ui.Text["StepCount"], stepCompleted, stepTotal);
                Indeterminate = !p.Fraction.HasValue && p.Completed < p.Total;
                ProgressPercent = p.Fraction.HasValue ? p.Fraction.Value * 100 : (double)p.Completed / p.Total * 100;
            });
            await Task.Run(() => controller!.Engine.InstallAsync(new(InstallIm, InstallBili, ForcePrivateNode), progress, ct), ct);
            SetStatus("Installed", controller!.Paths.Root);
            SelectedTab = 1; await RefreshApiStatusAsync(ct);
        });
        CheckCommand = Action(async ct =>
        {
            await UseCurrentPathsAsync(ct);
            SetStatus("Checking"); rawChecks.Clear(); Checks.Clear();
            foreach (var item in await Task.Run(() => controller!.CheckAsync(ct, InstallBili), ct)) rawChecks.Add(item);
            ShowChecks(); SetStatus("Checked");
        });
        StartCommand = Action(async ct => { await UseCurrentPathsAsync(ct); await controller!.StartAsync(ct); await RefreshApiStatusAsync(ct); SetStatus("Started"); });
        StopCommand = Action(async _ => { await controller!.Service.StopAsync(); SetStatus("Stopped"); });
        RestartCommand = Action(async ct => { await UseCurrentPathsAsync(ct); await controller!.Service.StopAsync(); await controller.StartAsync(ct); SetStatus("Restarted"); });
        UpdateCommand = Action(async ct => { await UseCurrentPathsAsync(ct); guidanceKey = null; Guidance = string.Join("\n", await controller!.CheckUpdatesAsync(ct)); SelectedTab = 2; });
        TestCommand = Action(async ct =>
        {
            var health = RequireHealth();
            guidanceKey = null; Guidance = string.Join("\n", await health.ConnectionStatusAsync(ct)) + "\n" + Ui.Text["TestGuide"];
            await RefreshApiStatusAsync(ct); SelectedTab = 1;
        });
        ApplyPathsCommand = Action(async ct =>
        {
            await UseCurrentPathsAsync(ct);
            SetStatus("PathsApplied", controller!.Paths.Root, controller.Paths.Workspace);
        }, requireController: false);
        OpenWebCommand = Simple(OpenWeb);
        ModelCommand = Simple(() => Guide("GuideModel"));
        QqCommand = Simple(() => Guide("GuideQq"));
        WeChatCommand = Simple(() => Guide("GuideWeChat"));
        WorkspaceCommand = Simple(() => ShellOpen(controller!.Paths.Workspace));
        LogFolderCommand = Simple(() => ShellOpen(controller?.Paths.Logs ?? Path.Combine(RootPath, "logs")));
        CancelCommand = Simple(() => { cancellation?.Cancel(); SetStatus("Cancelling"); }, () => busy);
        AdvancedCommand = Simple(() => SelectedTab = 3); LogsCommand = Simple(() => SelectedTab = 3);
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; timer.Tick += (_, _) => Refresh(); timer.Start();
        Ui.Text.LanguageChanged += RefreshLanguage;
    }
    private void PublishLiveText()
    {
        status = string.Format(Ui.Text[statusKey], statusArgs);
        guidance = guidanceKey is null ? guidance : Ui.Text[guidanceKey];
        apiStatus = Ui.Text[apiKey];
        stage = Ui.Text.Map(rawStage); detail = Ui.Text.Map(rawDetail);
        if (haveSteps) steps = string.Format(Ui.Text["StepCount"], stepCompleted, stepTotal);
    }
    private void RefreshLanguage()
    {
        var savedError = errorDetail;
        Status = string.Format(Ui.Text[statusKey], statusArgs);
        if (savedError is not null)
        {
            errorDetail = savedError; Detail = Ui.Text.Map(savedError); rawDetail = savedError;
            Status = string.Format(Ui.Text[statusKey], statusArgs) + " " + Detail;
        }
        if (guidanceKey is not null) Guidance = Ui.Text[guidanceKey];
        ApiStatus = Ui.Text[apiKey];
        Stage = Ui.Text.Map(rawStage); if (savedError is null) Detail = Ui.Text.Map(rawDetail);
        if (haveSteps) StepCount = string.Format(Ui.Text["StepCount"], stepCompleted, stepTotal);
        ShowChecks(); Changed(nameof(ServiceStatus)); Changed(nameof(Versions));
        _ = SavePreferencesAsync();
    }
    private void SetStatus(string key, params object[] args)
    {
        statusKey = key; statusArgs = args; errorDetail = null;
        Status = string.Format(Ui.Text[key], args);
    }
    private void ShowChecks()
    {
        Checks.Clear();
        foreach (var row in rawChecks) Checks.Add(Ui.Text.MapCheck(row));
    }
    private async Task SavePreferencesAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencesFile)!);
            await AtomicFile.WriteAsync(PreferencesFile, JsonSerializer.Serialize(new { root = RootPath, workspace = WorkspacePath, region = PreferChinaMirror ? "cn" : "global", language = Ui.Text.Language }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    private async Task UseCurrentPathsAsync(CancellationToken ct)
    {
        // The text boxes used to update only this view. Install kept the controller created at startup, so a new folder still installed under LocalAppData on C:.
        AppPaths paths;
        try { paths = new AppPaths(RootPath, WorkspacePath, PreferChinaMirror ? DownloadRegion.China : DownloadRegion.Global); }
        catch (ArgumentException e)
        {
            throw new BigFishException("invalid-state", string.Format(Ui.Text["PathRejected"], Ui.Text.Map(e.Message)));
        }
        if (controller is not null
            && controller.Paths.Root.Equals(paths.Root, StringComparison.OrdinalIgnoreCase)
            && controller.Paths.Workspace.Equals(paths.Workspace, StringComparison.OrdinalIgnoreCase)
            && controller.Paths.Sources.Region == paths.Sources.Region) return;
        var next = await Task.Run(() => new BigFishController(paths), ct);
        try { if (controller is not null) await controller.DisposeAsync(); }
        catch { await next.DisposeAsync(); throw; }
        Attach(next);
        RootPath = paths.Root; WorkspacePath = paths.Workspace;
        await SavePreferencesAsync();
        SetStatus("PathChanged", paths.Root);
    }
    private HealthChecker RequireHealth() => controller?.Service.Health ?? throw new BigFishException("not-running", Ui.Text["NeedRunning"]);
    public Task SaveApiKeyAsync(string key) => Run(async ct =>
    {
        await new DshSetupClient(RequireHealth()).SaveApiKeyAsync(key, ct);
        apiKey = "KeySavedDetail"; ApiStatus = Ui.Text[apiKey];
        SetStatus("KeySaved");
    });
    private async Task RefreshApiStatusAsync(CancellationToken ct)
    {
        try { apiKey = await new DshSetupClient(RequireHealth()).HasApiKeyAsync(ct) ? "KeyPresent" : "KeyMissing"; ApiStatus = Ui.Text[apiKey]; }
        catch (Exception e) when (e is not OperationCanceledException) { apiKey = "KeyUnknown"; ApiStatus = Ui.Text[apiKey]; }
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
        rawChecks.Clear();
        foreach (var row in await controller!.Detector.ScanAsync(await controller.Store.LoadAsync(ct), ct)) rawChecks.Add(row);
        ShowChecks(); SetStatus("Ready");
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
            var code = ErrorClassifier.Code(e);
            statusKey = Ui.Text["Hint." + code].StartsWith("Hint.", StringComparison.Ordinal) ? "Hint.unexpected" : "Hint." + code;
            statusArgs = []; errorDetail = LogManager.Redact(e.Message);
            Detail = Ui.Text.Map(errorDetail); rawDetail = errorDetail;
            try { controller?.Log.Write($"操作失败 [{code}]：{e.Message}"); } catch (IOException) { }
            rawStage = e is OperationCanceledException ? "已取消" : "操作未完成"; Stage = Ui.Text.Map(rawStage);
            Status = Ui.Text[statusKey] + " " + Detail;
        }
        finally { busy = false; Indeterminate = false; cancellation.Dispose(); cancellation = null; Refresh(); }
    }
    private void Refresh()
    {
        Changed(nameof(CanEdit)); Changed(nameof(ServiceStatus)); Changed(nameof(CanConfigure)); Changed(nameof(CanSaveKey));
        foreach (var command in commands) command.Refresh();
    }
    private void Guide(string key) { guidanceKey = key; Guidance = Ui.Text[key]; SelectedTab = 1; OpenWeb(); }
    private void OpenWeb()
    {
        if (controller?.Service.IsRunning != true || controller.Service.LaunchUri is not { } uri) throw new BigFishException("not-running", Ui.Text["NeedInstallToOpen"]);
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
