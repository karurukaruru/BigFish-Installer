using System.Text.Json;
using BigFish.Core;

var arguments = args.ToList();
string? Value(string flag) { var i = arguments.IndexOf(flag); return i >= 0 && i + 1 < arguments.Count ? arguments[i + 1] : null; }
var verb = arguments.FirstOrDefault() ?? "help";
if (verb == "help")
{
    Console.WriteLine("BigFish developer acceptance runner: diagnose|install|smoke --root <absolute> --workspace <absolute> [--region cn|global] [--private-node] [--bili] [--report <file>]");
    return 0;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    await using var controller = new BigFishController(new(Value("--root"), Value("--workspace"), Value("--region") switch { "cn" => DownloadRegion.China, "global" => DownloadRegion.Global, null => DownloadSources.BuildDefault, _ => throw new ArgumentException("--region 必须为 cn 或 global。") }));
    controller.Log.Line += Console.WriteLine;
    if (verb == "diagnose")
    {
        var report = JsonSerializer.Serialize(await controller.CheckAsync(cancellation.Token), Json.Options);
        if (Value("--report") is { } file) await File.WriteAllTextAsync(file, report);
        else Console.WriteLine(report);
        return 0;
    }
    if (verb is not ("install" or "smoke")) throw new ArgumentException("未知命令。");
    var options = new InstallOptions(!arguments.Contains("--no-im"), arguments.Contains("--bili"), arguments.Contains("--private-node"));
    var progress = new Progress<InstallProgress>(p => Console.WriteLine($"[{p.Completed}/{p.Total}] {p.Stage}: {p.Detail}"));
    var first = await controller.Engine.InstallAsync(options, progress, cancellation.Token);
    var configPath = Path.Combine(controller.Paths.Home, "acceptance-sentinel.txt");
    if (!File.Exists(configPath)) await File.WriteAllTextAsync(configPath, "BigFish acceptance sentinel — 配置保留");
    var before = await File.ReadAllBytesAsync(configPath);
    var reportObject = new Dictionary<string, object?> { ["platform"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        ["dshCliAndHttp"] = controller.Service.IsRunning, ["imRpc"] = first.ImInstalled, ["realAccountBinding"] = "not-tested", ["downloadRegion"] = controller.Paths.Sources.Region.ToString(), ["npmRegistry"] = controller.Paths.Sources.Registry };
    if (options.InstallIm)
    {
        reportObject["connections"] = await controller.Service.Health!.ConnectionStatusAsync(cancellation.Token);
        var language = await controller.Service.Health.RpcAsync("dsh-im-language", "settings.language.get", cancellation.Token);
        reportObject["interfaceLanguage"] = language.GetProperty("value").GetProperty("language").GetString();
    }
    if (verb == "smoke")
    {
        await controller.Service.StopAsync();
        // Simulates closing and reopening: new controller loads the persisted state.
        await using var reopened = new BigFishController(controller.Paths);
        var second = await reopened.Engine.InstallAsync(options, progress, cancellation.Token);
        if (first.ActiveRelease != second.ActiveRelease) throw new Exception("幂等检查失败：复用已验证运行环境失败。");
        var after = await File.ReadAllBytesAsync(configPath);
        if (!before.SequenceEqual(after)) throw new Exception("配置保护检查失败。");
        reportObject["idempotent"] = true; reportObject["sentinelPreserved"] = true;
        await reopened.Service.StopAsync(); reportObject["stopSucceeded"] = !reopened.Service.IsRunning;
    }
    if (Value("--report") is { } output) await File.WriteAllTextAsync(output, JsonSerializer.Serialize(reportObject, Json.Options));
    Console.WriteLine(JsonSerializer.Serialize(reportObject, Json.Options));
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"{ErrorClassifier.Code(e)}: {LogManager.Redact(e.Message)}");
    return e is OperationCanceledException ? 130 : 1;
}
