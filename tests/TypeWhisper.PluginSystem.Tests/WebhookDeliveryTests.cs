using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Plugin.Webhook;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class WebhookDeliveryTests : IDisposable
{
    private const string CollectionKey = "webhooks";
    private const string SecretUrl = "https://hooks.example/private-token-in-path";

    private readonly string _tempDir = Path.Join(
        Path.GetTempPath(),
        "tw-webhook-delivery-" + Guid.NewGuid().ToString("N")
    );

    public WebhookDeliveryTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
            // Best-effort cleanup in tests.
        }
    }

    [Fact]
    public async Task SlowDestinations_ShareOneBudgetAndAreSentInParallel()
    {
        var host = new FakeHost(_tempDir);
        var handler = new ScriptedHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var service = CreateService(host, handler, TimeSpan.FromMilliseconds(300));
        service.ReplaceAll([Config("Slow A"), Config("Slow B"), Config("Slow C")]);

        var stopwatch = Stopwatch.StartNew();
        await service.SendWebhooksAsync(Event());
        stopwatch.Stop();

        Assert.Equal(3, handler.CallCount);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed}");
        var log = service.SnapshotDeliveryLog();
        Assert.Equal(3, log.Count);
        Assert.All(log, entry => Assert.Equal(DeliveryOutcome.TimedOut, entry.Outcome));
    }

    [Fact]
    public async Task Dispose_CancelsInFlightDeliveryWithoutLoggingOrRetrying()
    {
        var host = new FakeHost(_tempDir);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var service = CreateService(host, handler, TimeSpan.FromMinutes(1));
        service.ReplaceAll([Config("Hook")]);

        var delivery = service.SendWebhooksAsync(Event());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Dispose();
        await delivery.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, handler.CallCount);
        Assert.Empty(service.SnapshotDeliveryLog());
        Assert.Empty(host.Logs);
    }

    [Fact]
    public async Task Dispose_DoesNotReportAFailureFromTheCancelledSend()
    {
        var host = new FakeHost(_tempDir);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler(async (_, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                throw new ObjectDisposedException("client");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var service = CreateService(host, handler, TimeSpan.FromMinutes(1));
        service.ReplaceAll([Config("Hook")]);

        var delivery = service.SendWebhooksAsync(Event());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Dispose();
        await delivery.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(service.SnapshotDeliveryLog());
        Assert.Empty(host.Logs);
    }

    [Fact]
    public async Task CallerCancellation_StopsDeliveryWithoutLogging()
    {
        var host = new FakeHost(_tempDir);
        var handler = new ScriptedHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var service = CreateService(host, handler, TimeSpan.FromMinutes(1));
        service.ReplaceAll([Config("Hook")]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // ReSharper disable once MethodSupportsCancellation -- the timeout only guards against a hang; cancellation is under test.
        await service.SendWebhooksAsync(Event(), cts.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(service.SnapshotDeliveryLog());
    }

    [Fact]
    public async Task ConnectionFailure_IsRetriedOnce()
    {
        var host = new FakeHost(_tempDir);
        var handler = new ScriptedHandler((_, _) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "refused")
        );
        using var service = CreateService(host, handler);
        service.ReplaceAll([Config("Hook")]);

        await service.SendWebhooksAsync(Event());

        Assert.Equal(2, handler.CallCount);
        Assert.Equal(DeliveryOutcome.ConnectionFailed, service.SnapshotDeliveryLog().Single().Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, 2)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 2)]
    [InlineData(HttpStatusCode.InternalServerError, 1)]
    [InlineData(HttpStatusCode.BadGateway, 1)]
    [InlineData(HttpStatusCode.Unauthorized, 1)]
    public async Task HttpFailure_IsRetriedOnlyWhenTheServerRefused(
        HttpStatusCode status,
        int expectedCalls
    )
    {
        var host = new FakeHost(_tempDir);
        var handler = new ScriptedHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(status))
        );
        using var service = CreateService(host, handler);
        service.ReplaceAll([Config("Hook")]);

        await service.SendWebhooksAsync(Event());

        Assert.Equal(expectedCalls, handler.CallCount);
        var entry = service.SnapshotDeliveryLog().Single();
        Assert.Equal(DeliveryOutcome.HttpError, entry.Outcome);
        Assert.Equal((int)status, entry.StatusCode);
    }

    [Fact]
    public async Task RetryThenSuccess_LogsOneDeliveredEntry()
    {
        var host = new FakeHost(_tempDir);
        var handler = new ScriptedHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
        );
        handler.Then((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)));
        using var service = CreateService(host, handler);
        service.ReplaceAll([Config("Hook")]);

        await service.SendWebhooksAsync(Event());

        Assert.Equal(2, handler.CallCount);
        var entry = service.SnapshotDeliveryLog().Single();
        Assert.Equal(DeliveryOutcome.Delivered, entry.Outcome);
        Assert.Equal(202, entry.StatusCode);
        Assert.Empty(host.Logs);
    }

    [Fact]
    public async Task Timeout_IsNotRetried()
    {
        var host = new FakeHost(_tempDir);
        var handler = new ScriptedHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var service = CreateService(host, handler, TimeSpan.FromMilliseconds(200));
        service.ReplaceAll([Config("Hook")]);

        await service.SendWebhooksAsync(Event());

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(DeliveryOutcome.TimedOut, service.SnapshotDeliveryLog().Single().Outcome);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("disable")]
    [InlineData("redirect")]
    public async Task Retry_IsSkippedWhenTheWebhookChangedMeanwhile(string change)
    {
        var host = new FakeHost(_tempDir);
        WebhookService? service = null;
        var original = Config("Hook");
        var handler = new ScriptedHandler((_, _) =>
        {
            switch (change)
            {
                case "remove":
                    // ReSharper disable once AccessToModifiedClosure -- service is assigned before the handler first runs.
                    service!.RemoveWebhook(original.Id);
                    break;
                case "disable":
                    // ReSharper disable once AccessToModifiedClosure -- service is assigned before the handler first runs.
                    service!.UpdateWebhook(original with { IsEnabled = false });
                    break;
                default:
                    // ReSharper disable once AccessToModifiedClosure -- service is assigned before the handler first runs.
                    service!.UpdateWebhook(original with { Url = "https://elsewhere.example/hook" });
                    break;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        service = CreateService(host, handler);
        try
        {
            service.ReplaceAll([original]);

            await service.SendWebhooksAsync(Event());

            Assert.Equal(1, handler.CallCount);
            Assert.Empty(service.SnapshotDeliveryLog());
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public async Task Retry_StillHappensAfterAnUnrelatedSettingsSave()
    {
        var host = new FakeHost(_tempDir);
        WebhookService? service = null;
        var original = Config("Hook");
        var handler = new ScriptedHandler((_, _) =>
        {
            // Saving settings replaces every record even when nothing changed.
            // ReSharper disable once AccessToModifiedClosure -- service is assigned before the handler first runs.
            service!.ReplaceAll([original with { Name = "Renamed" }, Config("Other", enabled: false)]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        handler.Then((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        service = CreateService(host, handler);
        try
        {
            service.ReplaceAll([original]);

            await service.SendWebhooksAsync(Event());

            Assert.Equal(2, handler.CallCount);
            Assert.Equal(DeliveryOutcome.Delivered, service.SnapshotDeliveryLog().Single().Outcome);
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public async Task Failure_LogsNeitherUrlNorHeaderNorExceptionText()
    {
        var host = new FakeHost(_tempDir) { Secrets = { ["ref"] = "Bearer super-secret" } };
        var handler = new ScriptedHandler((request, _) =>
            throw new InvalidOperationException(
                $"boom at {request.RequestUri} with {request.Headers.Authorization}"
            )
        );
        var plugin = await ActivatePluginAsync(host, handler);
        plugin.Service!.ReplaceAll(
            [
                Config("Hook", url: SecretUrl) with
                {
                    HeaderSecretReferences = new Dictionary<string, string>
                    {
                        ["Authorization"] = "ref",
                    },
                },
            ]
        );

        await plugin.Service.SendWebhooksAsync(Event());
        var log = await plugin.ExecuteSettingsActionAsync("recent-deliveries", CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(DeliveryOutcome.Failed, plugin.Service.SnapshotDeliveryLog().Single().Outcome);
        foreach (var text in host.Logs.Append(log.Message))
        {
            Assert.DoesNotContain("private-token", text);
            Assert.DoesNotContain("hooks.example", text);
            Assert.DoesNotContain("super-secret", text);
            Assert.DoesNotContain("boom", text);
        }

        Assert.Contains("Hook", log.Message);
        Assert.Single(host.Logs);
    }

    [Fact]
    public async Task MissingHeaderSecret_IsReportedWithoutSending()
    {
        var host = new FakeHost(_tempDir);
        var handler = new ScriptedHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
        );
        using var service = CreateService(host, handler);
        service.ReplaceAll(
            [
                Config("Hook") with
                {
                    HeaderSecretReferences = new Dictionary<string, string>
                    {
                        ["Authorization"] = "missing",
                    },
                },
            ]
        );

        await service.SendWebhooksAsync(Event());

        Assert.Equal(0, handler.CallCount);
        Assert.Equal(
            DeliveryOutcome.HeaderSecretUnavailable,
            service.SnapshotDeliveryLog().Single().Outcome
        );
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "Work", true)]
    [InlineData(false, null, false)]
    [InlineData(false, "Work", true)]
    [InlineData(false, "Personal", false)]
    public async Task ProfileMode_SelectsDeliveries(bool allProfiles, string? profile, bool delivered)
    {
        var host = new FakeHost(_tempDir);
        var handler = new ScriptedHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
        );
        using var service = CreateService(host, handler);
        service.ReplaceAll(
            [Config("Hook") with { AllProfiles = allProfiles, ProfileFilter = allProfiles ? [] : ["Work"] }]
        );

        await service.SendWebhooksAsync(Event(profile));

        Assert.Equal(delivered ? 1 : 0, handler.CallCount);
    }

    [Fact]
    public async Task TestAction_SendsToDisabledWebhookWithoutPersistingAnything()
    {
        var host = new FakeHost(_tempDir) { Secrets = { ["ref"] = "Bearer saved" } };
        string? body = null;
        string? authorization = null;
        var handler = new ScriptedHandler(async (request, ct) =>
        {
            body = await request.Content!.ReadAsStringAsync(ct);
            authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var plugin = await ActivatePluginAsync(host, handler);
        var webhook = Config("Disabled", enabled: false) with
        {
            AllProfiles = false,
            ProfileFilter = ["Work"],
            HeaderSecretReferences = new Dictionary<string, string> { ["Authorization"] = "ref" },
        };
        plugin.Service!.ReplaceAll([webhook]);
        var savedJson = await File.ReadAllTextAsync(Path.Join(_tempDir, "webhooks.json"));
        var storesBefore = host.StoreCount;

        var actions = plugin.GetSettingsActions();
        var test = Assert.Single(actions, action => action.Id.StartsWith("test:", StringComparison.Ordinal));
        var result = await plugin.ExecuteSettingsActionAsync(test.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains("200", result.Message);
        Assert.Equal("Bearer saved", authorization);
        Assert.Contains("TypeWhisper test message", body);
        Assert.Equal(savedJson, await File.ReadAllTextAsync(Path.Join(_tempDir, "webhooks.json")));
        Assert.Equal(storesBefore, host.StoreCount);
        Assert.True(plugin.Service.SnapshotDeliveryLog().Single().IsTest);
        Assert.Empty(host.Logs);
    }

    [Fact]
    public async Task TestAction_ForARemovedWebhook_FailsWithoutSending()
    {
        var host = new FakeHost(_tempDir);
        var handler = new ScriptedHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
        );
        var plugin = await ActivatePluginAsync(host, handler);
        var webhook = Config("Hook");
        plugin.Service!.ReplaceAll([webhook]);
        var testId = plugin.GetSettingsActions()[0].Id;
        plugin.Service.RemoveWebhook(webhook.Id);

        var result = await plugin.ExecuteSettingsActionAsync(testId, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task TestAction_HonorsCancellation()
    {
        var host = new FakeHost(_tempDir);
        var handler = new ScriptedHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var plugin = await ActivatePluginAsync(host, handler);
        plugin.Service!.ReplaceAll([Config("Hook")]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            plugin.ExecuteSettingsActionAsync(plugin.GetSettingsActions()[0].Id, cts.Token)
        );
        Assert.Empty(plugin.Service.SnapshotDeliveryLog());
    }

    [Fact]
    public async Task RecentDeliveries_WithoutDeliveries_SaysSo()
    {
        var plugin = await ActivatePluginAsync(
            new FakeHost(_tempDir),
            new ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))
        );

        var result = await plugin.ExecuteSettingsActionAsync("recent-deliveries", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Actions.NoDeliveries", result.Message);
    }

    [Fact]
    public void Actions_AreHiddenUntilActivated()
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        Assert.Empty(plugin.GetSettingsActions());
    }

    [Fact]
    public async Task Redirect_IsNotFollowed()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var requests = new List<string>();
        // ReSharper disable once FunctionNeverReturns -- the loop ends when server.Stop() makes AcceptTcpClientAsync throw.
        var serverTask = Task.Run(async () =>
        {
            while (true)
            {
                // ReSharper disable once AccessToDisposedClosure -- disposing the listener is what ends this loop.
                using var client = await server.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var read = await stream.ReadAsync(buffer);
                var requestLine = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n")[0];
                lock (requests)
                    requests.Add(requestLine);
                var response = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                );
                await stream.WriteAsync(response);
            }
        });

        var host = new FakeHost(_tempDir);
        using var service = new WebhookService(host, _tempDir);
        service.ReplaceAll([Config("Hook", url: $"http://127.0.0.1:{port}/hook")]);

        await service.SendWebhooksAsync(Event());
        server.Stop();
        _ = serverTask.ContinueWith(_ => { }, TaskScheduler.Default);

        lock (requests)
            Assert.Equal(["POST /hook HTTP/1.1"], requests);
        var entry = service.SnapshotDeliveryLog().Single();
        Assert.Equal(DeliveryOutcome.HttpError, entry.Outcome);
        Assert.Equal(302, entry.StatusCode);
    }

    [Fact]
    public async Task SetItems_ListedModeWithoutProfiles_IsRejected()
    {
        var plugin = await ActivatePluginAsync(new FakeHost(_tempDir, ["Work"]));

        var result = await plugin.SetItemsAsync(
            CollectionKey,
            [Item("Hook", profileMode: "listed", profiles: "")]
        );

        Assert.False(result.IsSuccess);
        Assert.Contains("Settings.ProfilesRequired", result.Message);
    }

    [Fact]
    public async Task SetItems_AllModeWithProfiles_IsRejected()
    {
        var plugin = await ActivatePluginAsync(new FakeHost(_tempDir, ["Work"]));

        var result = await plugin.SetItemsAsync(
            CollectionKey,
            [Item("Hook", profileMode: "all", profiles: "Work")]
        );

        Assert.False(result.IsSuccess);
        Assert.Contains("Settings.ProfilesWithAllMode", result.Message);
    }

    [Fact]
    public async Task SetItems_UnknownNewProfile_IsRejectedButSavedStaleNamesAreKept()
    {
        var host = new FakeHost(_tempDir, ["Work"]);
        var plugin = await ActivatePluginAsync(host);
        var id = Guid.NewGuid().ToString("D");

        var typo = await plugin.SetItemsAsync(
            CollectionKey,
            [Item("Hook", profileMode: "listed", profiles: "Wrok", id: id)]
        );
        Assert.False(typo.IsSuccess);
        Assert.Contains("Settings.ProfileUnknown", typo.Message);
        Assert.Contains("Work", typo.Message);

        Assert.True(
            (await plugin.SetItemsAsync(
                CollectionKey,
                [Item("Hook", profileMode: "listed", profiles: "Work", id: id)]
            )).IsSuccess
        );

        // The profile is later renamed; an unrelated edit must still save.
        host.Profiles = ["Office"];
        var rename = await plugin.SetItemsAsync(
            CollectionKey,
            [Item("Renamed hook", profileMode: "listed", profiles: "Work", id: id)]
        );

        Assert.True(rename.IsSuccess);
        Assert.Equal(["Work"], plugin.Service!.SnapshotWebhooks().Single().ProfileFilter);
    }

    [Fact]
    public async Task SetItems_UrlWithCredentials_IsRejected()
    {
        var plugin = await ActivatePluginAsync(new FakeHost(_tempDir));

        var result = await plugin.SetItemsAsync(
            CollectionKey,
            [Item("Hook", url: "https://user:pass@example.com/hook")]
        );

        Assert.False(result.IsSuccess);
        Assert.Contains("Settings.UrlCredentials", result.Message);
    }

    [Fact]
    public async Task GetItems_ReportsProfileMode_AndLegacyFilesKeepTheirMeaning()
    {
        await File.WriteAllTextAsync(
            Path.Join(_tempDir, "webhooks.json"),
            $$"""
            [
              { "id": "{{Guid.NewGuid()}}", "name": "Filtered", "url": "https://example.com/a", "profileFilter": ["Work"] },
              { "id": "{{Guid.NewGuid()}}", "name": "Everything", "url": "https://example.com/b", "profileFilter": [] }
            ]
            """
        );
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        var items = await plugin.GetItemsAsync(CollectionKey);

        Assert.Equal("listed", items[0].Values["profileMode"]);
        Assert.Equal("all", items[1].Values["profileMode"]);
    }

    [Fact]
    public async Task SetItems_WithoutProfileMode_DerivesItFromTheList()
    {
        var plugin = await ActivatePluginAsync(new FakeHost(_tempDir, ["Work"]));

        var result = await plugin.SetItemsAsync(
            CollectionKey,
            [Item("Filtered", profiles: "Work"), Item("Everything")]
        );

        Assert.True(result.IsSuccess);
        var saved = plugin.Service!.SnapshotWebhooks();
        Assert.False(saved[0].AllProfiles);
        Assert.True(saved[1].AllProfiles);
    }

    private WebhookService CreateService(
        FakeHost host,
        HttpMessageHandler handler,
        TimeSpan? budget = null
    ) => new(host, _tempDir, handler, budget ?? TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(10));

    private async Task<WebhookPlugin> ActivatePluginAsync(
        FakeHost host,
        HttpMessageHandler? handler = null
    )
    {
        var plugin = new WebhookPlugin((h, dir) =>
            new WebhookService(
                h,
                dir,
                handler ?? new ScriptedHandler((_, _) =>
                    Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
                ),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(10)
            )
        );
        plugin.SetDataDirectory(_tempDir);
        await plugin.ActivateAsync(host);
        return plugin;
    }

    private static WebhookConfig Config(
        string name,
        string url = "https://example.com/hook",
        bool enabled = true
    ) => new() { Name = name, Url = url, IsEnabled = enabled };

    private static TranscriptionCompletedEvent Event(string? profile = null) =>
        new() { Text = "hello", ProfileName = profile };

    private static PluginCollectionItem Item(
        string name,
        string url = "https://example.com/hook",
        string? profileMode = null,
        string profiles = "",
        string? id = null
    )
    {
        var values = new Dictionary<string, string?>
        {
            ["name"] = name,
            ["url"] = url,
            ["method"] = "POST",
            ["headers"] = "",
            ["profiles"] = profiles,
            ["enabled"] = "true",
        };
        if (profileMode is not null)
            values["profileMode"] = profileMode;
        if (id is not null)
            values["__id"] = id;

        return new PluginCollectionItem(values);
    }

    private sealed class ScriptedHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder
    ) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _next = new();
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public void Then(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> next)
        {
            lock (_next)
                _next.Enqueue(next);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var call = Interlocked.Increment(ref _callCount);
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> current;
            lock (_next)
                current = call > 1 && _next.Count > 0 ? _next.Dequeue() : responder;
            return current(request, cancellationToken);
        }
    }

    private sealed class FakeHost(string dataDirectory, IReadOnlyList<string>? profiles = null)
        : IPluginHostServices
    {
        private readonly List<string> _logs = [];
        private int _storeCount;

        public Dictionary<string, string> Secrets { get; } = [];
        public IReadOnlyList<string> Profiles { get; set; } = profiles ?? [];
        public int StoreCount => _storeCount;

        public IReadOnlyList<string> Logs
        {
            get
            {
                lock (_logs)
                    return _logs.ToList();
            }
        }

        public Task StoreSecretAsync(string key, string value)
        {
            Interlocked.Increment(ref _storeCount);
            lock (Secrets)
                Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key)
        {
            lock (Secrets)
                return Task.FromResult(Secrets.GetValueOrDefault(key));
        }

        public Task DeleteSecretAsync(string key)
        {
            lock (Secrets)
                Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key) => default;
        public void SetSetting<T>(string key, T value) { }
        public string PluginDataDirectory => dataDirectory;
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new PluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => Profiles;
        public IPluginLocalization Localization { get; } = new KeyLocalization();

        public void Log(PluginLogLevel level, string message)
        {
            lock (_logs)
                _logs.Add(message);
        }

        public void NotifyCapabilitiesChanged() { }
    }

    private sealed class KeyLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;

        public string GetString(string key, params object[] args) =>
            args.Length == 0 ? key : $"{key}: {string.Join(", ", args)}";
    }
}
