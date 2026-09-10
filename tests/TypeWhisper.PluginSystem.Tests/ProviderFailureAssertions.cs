using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Moq;
using TypeWhisper.Plugin.CloudflareAsr;
using TypeWhisper.Plugin.Qwen3Stt;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

internal static class ProviderFailureAssertions
{
    internal static async Task VerifyAsync<T>() where T : ITypeWhisperPlugin, new()
    {
        int[] statuses = [401, 429, 500, 0];
        foreach (var status in statuses)
        {
            using var client = new HttpClient(new FailureHandler(status));
            using var plugin = new T();
            var field = typeof(T).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((HttpClient)field.GetValue(plugin)!).Dispose();
            field.SetValue(plugin, client);
            var host = new Mock<IPluginHostServices>();
            host.Setup(h => h.LoadSecretAsync(It.IsAny<string>())).ReturnsAsync(status == 0 ? null : "test-secret-key");
            host.Setup(h => h.GetSetting<string>(It.IsAny<string>())).Returns((string key) => key switch
            {
                "baseUrl" or "baseURL" => status == 0 ? "" : "https://example.test",
                _ => null,
            });
            await plugin.ActivateAsync(host.Object);
            if (status == 0 && plugin is Qwen3SttPlugin)
                typeof(T).GetField("_baseUrl", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, "");
            var ex = await Assert.ThrowsAsync<PluginRequestException>(async () =>
            {
                switch (plugin)
                {
                    case ILlmProviderRole llm:
                        await llm.ProcessAsync("system", "user", llm.SupportedModels.Count > 0 ? llm.SupportedModels[0].Id : "model", CancellationToken.None);
                        break;
                    case ITranscriptionEngineRole transcription:
                        await transcription.TranscribeAsync(CreateWav(), plugin is CloudflareAsrPlugin ? null : "en", false, null, CancellationToken.None);
                        break;
                }
            });
            Assert.Equal(status switch
            {
                401 => PluginRequestFailureKind.Authentication,
                429 => PluginRequestFailureKind.RateLimit,
                500 => PluginRequestFailureKind.ServerError,
                _ => PluginRequestFailureKind.Configuration,
            }, ex.FailureKind);
            if (status != 0)
                Assert.Equal(status, ex.HttpStatusCode);
            if (status == 429)
                Assert.Equal(TimeSpan.FromSeconds(2), ex.RetryAfter);
            Assert.DoesNotContain("test-secret-key", ex.Message);
            if (plugin is not ILlmProviderRole streamingLlm)
                continue;
            var streamFailure = await Assert.ThrowsAsync<PluginRequestException>(async () =>
            {
                await foreach (var _ in streamingLlm.ProcessStreamingAsync(
                    "system", "user", streamingLlm.SupportedModels.Count > 0 ? streamingLlm.SupportedModels[0].Id : "model", CancellationToken.None))
                {
                }
            });
            Assert.Equal(ex.FailureKind, streamFailure.FailureKind);
            Assert.Equal(ex.HttpStatusCode, streamFailure.HttpStatusCode);
            Assert.Equal(ex.RetryAfter, streamFailure.RetryAfter);
        }
    }

    private static byte[] CreateWav()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(40);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(4);
        writer.Write(0);
        return stream.ToArray();
    }

    private sealed class FailureHandler(int status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.NotEqual(0, status);
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent("{\"error\":{\"message\":\"provider failed test-secret-key\"}}"),
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return Task.FromResult(response);
        }
    }
}
