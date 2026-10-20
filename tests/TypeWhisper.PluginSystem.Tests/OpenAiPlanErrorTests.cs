using System.Net;
using System.Text.Json;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Plugin.OpenAi;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public partial class OpenAiPluginTests
{
    public static TheoryData<string, int, string, PluginRequestFailureKind, bool> PlanErrors => new()
    {
        { "subscription_sharing_usage_limit_exceeded", 429, "Settings.ChatGptUsageLimit", PluginRequestFailureKind.Permission, false },
        { "subscription_sharing_user_not_eligible", 403, "Settings.ChatGptNotEligible", PluginRequestFailureKind.Permission, false },
        { "subscription_sharing_usage_unavailable", 503, "Settings.ChatGptUsageUnavailable", PluginRequestFailureKind.ServerError, true },
        { "subscription_sharing_user_unavailable", 503, "Settings.ChatGptUserUnavailable", PluginRequestFailureKind.ServerError, true },
        { "subscription_sharing_unsupported_capability", 400, "Settings.ChatGptUnsupportedCapability", PluginRequestFailureKind.InvalidRequest, false },
        { "chatpass_v2_scope_not_authorized", 403, "Settings.ChatGptPlanNotAuthorized", PluginRequestFailureKind.Authentication, false },
    };

    [Theory]
    [MemberData(nameof(PlanErrors))]
    public async Task PlanErrors_HttpAndSseUseLocalizedRecoveryAndRetryClassification(
        string code, int status, string key, PluginRequestFailureKind kind, bool transient)
    {
        foreach (var language in new[] { "en", "de", "es", "ru" })
        foreach (var sse in new[] { false, true })
        {
            var localization = new PluginLocalization(Path.GetFullPath(Path.Join(
                "..", "..", "..", "..", "..", "plugins", "TypeWhisper.Plugin.OpenAi"), AppContext.BaseDirectory), language);
            var error = new { code, param = "tools", message = "untrusted-provider-message" };
            var body = sse
                ? "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n"
                  + "data: " + JsonSerializer.Serialize(new { type = "response.failed", response = new { status = "failed", error } }) + "\n\n"
                : JsonSerializer.Serialize(new { error });
            using var client = new HttpClient(new CapturingHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(sse ? HttpStatusCode.OK : (HttpStatusCode)status) { Content = new StringContent(body) })));
            var sut = new OpenAiChatGptClient(client, "fixture", localization);
            var ex = await Assert.ThrowsAsync<PluginRequestException>(() => sut.ProcessAsync("system", "input", "gpt-fixture", null, CancellationToken.None));
            Assert.Equal(kind, ex.FailureKind);
            Assert.Equal(transient, ex.IsTransient);
            Assert.Equal(sse ? null : (int?)status, ex.HttpStatusCode);
            var expected = code == "subscription_sharing_unsupported_capability"
                ? localization.GetString(key, "tools") : localization.GetString(key);
            Assert.NotEqual(key, expected);
            Assert.Equal(expected, ex.Message);
            Assert.DoesNotContain("partial", ex.Message);
            Assert.DoesNotContain("untrusted-provider-message", ex.Message);
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ru")]
    public async Task MissingPlanPermission_IsLocalized(string language)
    {
        var localization = new PluginLocalization(Path.GetFullPath(Path.Join(
            "..", "..", "..", "..", "..", "plugins", "TypeWhisper.Plugin.OpenAi"), AppContext.BaseDirectory), language);
        var host = new TestPluginHostServices { Localization = localization };
        host.SetSetting("authMode", "chatgpt");
        host.SetSetting("oauthClientId", ChatGptIssuedClientId);
        host.Secrets["oauth-access-token"] = "fixture";
        using var sut = new OpenAiPlugin();
        await sut.ActivateAsync(host);
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() => sut.ProcessAsync("system", "input", "gpt-fixture", CancellationToken.None));
        Assert.Equal(localization.GetString("Settings.ChatGptPlanNotAuthorized"), ex.Message);
        Assert.NotEqual("Settings.ChatGptPlanNotAuthorized", ex.Message);
    }
}
