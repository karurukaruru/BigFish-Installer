using System.IO;
using System.Text.Json;
using System.Windows;
using BigFish.Core;

namespace BigFish.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("zh-CN");
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("zh-CN");
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
                // Instantiate real WPF controls on the STA dispatcher. No installation or network calls.
                var window = new MainWindow(); window.Show(); window.UpdateLayout();
                await File.WriteAllTextAsync(e.Args[1], JsonSerializer.Serialize(new { osSupported = AppPaths.SupportedPlatform,
                    wpfLoaded = window.IsLoaded, downloadRegion = paths.Sources.Region.ToString(), framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    checks }, Json.Options));
                window.Close(); Shutdown(0);
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
        catch (Exception ex) { MessageBox.Show(LogManager.Redact(ex.Message), "BigFish 启动失败", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
}
