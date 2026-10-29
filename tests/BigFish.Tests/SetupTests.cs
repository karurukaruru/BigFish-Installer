using System.Text.Json;
using BigFish.Core;
using Xunit;

namespace BigFish.Tests;

public class SetupTests
{
    private sealed class SetupFixture(bool writable = true, string reference = "DEEPSEEK_API_KEY")
    {
        public bool Configured;
        public string? Saved, Workspace;
        public int Sets;
        public string Reply(string path, string body)
        {
            using var doc = JsonDocument.Parse(body); var root = doc.RootElement;
            var args = root.GetProperty("payload").GetProperty("args");
            object? value = null;
            switch (path)
            {
                case "/api/settings/describe": value = new { namespaces = new[] { new { ns = "llm-deepseek", value = new { apiKeyEnv = reference } } } }; break;
                case "/api/credentials/describe":
                    Assert.Equal(reference, args.GetProperty("refs")[0].GetString());
                    value = new Dictionary<string, object> { [reference] = new { configured = Configured, writable } }; break;
                case "/api/credentials/set":
                    Assert.Equal(reference, args.GetProperty("ref").GetString());
                    Saved = args.GetProperty("value").GetString(); Configured = true; Sets++; break;
                case "/api/workspace/create":
                    Workspace = args.GetProperty("request").GetProperty("path").GetString();
                    value = new { workspace = new { id = "fixture", path = Workspace }, created = true }; break;
                default: throw new InvalidOperationException("Unexpected fixture path: " + path);
            }
            return JsonSerializer.Serialize(new { rpcId = root.GetProperty("rpcId").GetString(), result = new { ok = true, value } });
        }
    }
    [Fact] public async Task SaveKeyUsesAuthenticatedDshApiAndConfirmsWithoutReadingSecret()
    {
        var fixture = new SetupFixture(); await using var server = new LocalDshFixture(apiResponse: fixture.Reply);
        using var health = new HealthChecker(); await health.AuthenticateAsync(new(server.Origin, "?token=fixture"), default);
        var setup = new DshSetupClient(health); Assert.False(await setup.HasApiKeyAsync(default));
        await setup.SaveApiKeyAsync("  fixture-secret-value  ", default);
        Assert.True(await setup.HasApiKeyAsync(default)); Assert.Equal("fixture-secret-value", fixture.Saved);
        Assert.True(server.SawCookie); Assert.Equal(1, fixture.Sets);
    }
    [Fact] public async Task ExternalCredentialReferenceIsPreserved()
    {
        var fixture = new SetupFixture(reference: "CUSTOM_DEEPSEEK_KEY"); await using var server = new LocalDshFixture(apiResponse: fixture.Reply);
        using var health = new HealthChecker(); await health.AuthenticateAsync(new(server.Origin, "?token=fixture"), default);
        await new DshSetupClient(health).SaveApiKeyAsync("fixture-custom", default); Assert.Equal(1, fixture.Sets);
    }
    [Fact] public async Task ManagedCredentialCannotBeOverwritten()
    {
        var fixture = new SetupFixture(false); await using var server = new LocalDshFixture(apiResponse: fixture.Reply);
        using var health = new HealthChecker(); await health.AuthenticateAsync(new(server.Origin, "?token=fixture"), default);
        await Assert.ThrowsAsync<BigFishException>(() => new DshSetupClient(health).SaveApiKeyAsync("fixture-key", default)); Assert.Equal(0, fixture.Sets);
    }
    [Theory] [InlineData("")] [InlineData("   ")] [InlineData("key with-space")] [InlineData("key\r\nheader")]
    public async Task InvalidKeyNeverReachesDsh(string key)
    {
        var fixture = new SetupFixture(); await using var server = new LocalDshFixture(apiResponse: fixture.Reply);
        using var health = new HealthChecker();
        await Assert.ThrowsAsync<BigFishException>(() => new DshSetupClient(health).SaveApiKeyAsync(key, default)); Assert.Null(fixture.Saved);
    }
    [Fact] public async Task WorkspaceRegistrationKeepsUnicodePathAndContents()
    {
        using var temp = new TempDirectory(); var file = Path.Combine(temp.Paths.Workspace, "history.txt");
        Directory.CreateDirectory(temp.Paths.Workspace); await File.WriteAllTextAsync(file, "existing history");
        var fixture = new SetupFixture(); await using var server = new LocalDshFixture(apiResponse: fixture.Reply);
        using var health = new HealthChecker(); await health.AuthenticateAsync(new(server.Origin, "?token=fixture"), default);
        await new DshSetupClient(health).EnsureWorkspaceAsync(temp.Paths.Workspace, default);
        Assert.Equal(temp.Paths.Workspace, fixture.Workspace); Assert.Equal("existing history", await File.ReadAllTextAsync(file));
    }
    [Fact] public async Task CorrelationMismatchIsNotReportedAsSaved()
    {
        await using var server = new LocalDshFixture(apiResponse: (_, _) => "{\"rpcId\":\"wrong\",\"result\":{\"ok\":true,\"value\":{}}}");
        using var health = new HealthChecker(); await health.AuthenticateAsync(new(server.Origin, "?token=fixture"), default);
        await Assert.ThrowsAsync<BigFishException>(() => health.CallAsync("settings/describe", new { }, default));
    }
    [Fact] public async Task RejectedSecretIsNeverEchoedInException()
    {
        await using var server = new LocalDshFixture(apiResponse: (_, body) =>
        {
            using var doc = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(new { rpcId = doc.RootElement.GetProperty("rpcId").GetString(), result = new { ok = false, error = new { message = "secret-must-never-appear" } } });
        });
        using var health = new HealthChecker(); await health.AuthenticateAsync(new(server.Origin, "?token=fixture"), default);
        var error = await Assert.ThrowsAsync<BigFishException>(() => health.CallAsync("credentials/set", new { @ref = "DEEPSEEK_API_KEY", value = "secret-must-never-appear" }, default));
        Assert.DoesNotContain("secret-must-never-appear", error.ToString());
        await Assert.ThrowsAsync<ArgumentException>(() => health.CallAsync("session/send", new { }, default));
    }
    [Fact] public async Task CancelledSaveDoesNotWriteCredential()
    {
        var fixture = new SetupFixture(); await using var server = new LocalDshFixture(apiResponse: fixture.Reply);
        using var health = new HealthChecker(); await health.AuthenticateAsync(new(server.Origin, "?token=fixture"), default);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DshSetupClient(health).SaveApiKeyAsync("fixture-key", stop.Token)); Assert.Equal(0, fixture.Sets);
    }
    [Fact] public async Task OldInstallerLocaleTemplateIsBackedUpAndMigratedOnlyExactly()
    {
        using var temp = new TempDirectory(); temp.Paths.Prepare(); var path = Path.Combine(temp.Paths.Home, "settings.yaml");
        await File.WriteAllTextAsync(path, "locale:\r\n  preference: zh-CN\r\n");
        await new ConfigurationManager(temp.Paths).EnsureChineseDefaultsAsync(default);
        Assert.Equal("locale:\n  preference: zh\n", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(temp.Paths.Home, "settings.yaml.pre-0.1.2-*"));
        await File.WriteAllTextAsync(path, "locale:\n  preference: en\ncustom: keep\n");
        await new ConfigurationManager(temp.Paths).EnsureChineseDefaultsAsync(default); Assert.Contains("custom: keep", await File.ReadAllTextAsync(path));
    }
}
