using System.IO;
using System.Diagnostics;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using System.Text.Json;
using System.Windows;
using BigFish.Core;

namespace BigFish.App;

public partial class App : Application
{
    private string? selfTestReport;
    protected override async void OnStartup(StartupEventArgs e)
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("zh-CN");
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("zh-CN");
        if (e.Args.Length == 2 && e.Args[0] == "--self-test") selfTestReport = e.Args[1];
        DispatcherUnhandledException += (_, error) =>
        {
            error.Handled = true;
            var file = SaveCrash(error.Exception);
            if (selfTestReport is not null)
            {
                try { File.WriteAllText(selfTestReport, LogManager.Redact(error.Exception.ToString())); } catch (IOException) { }
                Shutdown(1); return;
            }
            MessageBox.Show("程序发生错误：" + LogManager.Redact(error.Exception.Message) + "\n诊断日志：" + file,
                "BigFish 运行错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        };
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--self-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var root = Path.Combine(Path.GetTempPath(), "BigFish-SelfTest-" + Guid.NewGuid().ToString("N"));
                var paths = new AppPaths(root, Path.Combine(root, "workspace")); paths.Prepare();
                var detector = new SystemDetector(paths, new ProcessRunner(new(paths.Logs)));
                var checks = await Task.Run(() => detector.ScanAsync(new(), CancellationToken.None));
                // Bind the actual view model and visit every page. The old empty-window test
                // never resolved ProgressBar.Value and missed the read-only TwoWay crash.
                var errors = new BindingErrors();
                PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                var model = new MainViewModel(paths, loadPreferences: false);
                var window = new MainWindow { DataContext = model }; window.Show();
                await model.InitializeAsync();
                for (var page = 0; page < window.Steps.Items.Count; page++)
                {
                    model.SelectedTab = page;
                    await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                }
                if (errors.Messages.Count > 0) throw new InvalidOperationException("WPF binding errors: " + string.Join(" | ", errors.Messages));
                if (BindingOperations.GetBinding(window.InstallProgressBar, RangeBase.ValueProperty)?.Mode != BindingMode.OneWay)
                    throw new InvalidOperationException("Progress must use OneWay binding.");
                await File.WriteAllTextAsync(e.Args[1], JsonSerializer.Serialize(new { osSupported = AppPaths.SupportedPlatform,
                    wpfLoaded = window.IsLoaded, viewModelBound = ReferenceEquals(window.DataContext, model), pagesVisited = window.Steps.Items.Count, bindingErrors = errors.Messages, downloadRegion = paths.Sources.Region.ToString(), framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    checks }, Json.Options));
                await model.ShutdownAsync(); window.Close(); Shutdown(0);
            }
            catch (Exception ex) { await File.WriteAllTextAsync(e.Args[1], LogManager.Redact(ex.ToString())); Shutdown(1); }
            return;
        }
        try
        {
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model }; MainWindow = window; window.Show();
            await model.InitializeAsync();
        }
        catch (Exception ex) { var file = SaveCrash(ex); MessageBox.Show(LogManager.Redact(ex.Message) + "\n诊断日志：" + file, "BigFish 启动失败", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
    private static string SaveCrash(Exception error)
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath() })
        {
            try { var log = new LogManager(Path.Combine(root, "BigFish", "logs")); log.Write(error.ToString()); return log.FilePath; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return "无法写入日志，请保留错误文字。";
    }
}
internal sealed class BindingErrors : TraceListener
{
    public List<string> Messages { get; } = [];
    public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(LogManager.Redact(message)); }
    public override void WriteLine(string? message) => Write(message);
}
