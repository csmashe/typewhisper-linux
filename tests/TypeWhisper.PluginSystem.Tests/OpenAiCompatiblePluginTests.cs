using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.OpenAiCompatible;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class OpenAiCompatiblePluginTests
{
    [Fact]
    public async Task ProcessStreamingAsync_StreamsDeltas_AgainstOpenAiCompatibleServer()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}",
            "",
            "data: [DONE]",
            "");
        var handler = new CapturingHandler((request, body) =>
        {
            capturedRequest = request;
            capturedBody = body;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        });

        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedLlmModel", "llama3");
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (var chunk in sut.ProcessStreamingAsync("sys", "user", "llama3", CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal(["Hel", "lo"], chunks);
        Assert.Equal("http://localhost:11434/v1/chat/completions", capturedRequest?.RequestUri?.ToString());
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("llama3", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ProcessStreamingAsync_ToggleOff_YieldsSingleBulkChunk()
    {
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"choices":[{"message":{"content":"bulk"}}]}""",
                Encoding.UTF8, "application/json"),
        });

        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedLlmModel", "llama3");
        host.SetSetting("streamResponses", false);
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (var chunk in sut.ProcessStreamingAsync("sys", "user", "llama3", CancellationToken.None))
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.Equal("bulk", chunks[0]);
    }

    private static HttpClient ModelsClient() =>
        new(new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"data":[{"id":"m1"},{"id":"m2"}]}""",
                Encoding.UTF8, "application/json"),
        }));

    private static PluginCollectionItem ProfileItem(
        string name, string baseUrl, string? apiKey = null,
        string? model = null, string? llmModel = null, string? id = "") =>
        new(new Dictionary<string, string?>
        {
            ["name"] = name,
            ["baseUrl"] = baseUrl,
            ["api-key"] = apiKey,
            ["selectedModel"] = model,
            ["selectedLlmModel"] = llmModel,
            ["__id"] = id,
        });

    [Fact]
    public async Task SetBaseUrl_EndpointChange_InvalidatesStateThenRefreshSelectsFirstModel()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedModel", "old-model");
        host.SetSetting("selectedLlmModel", "old-model");
        host.SetSetting(
            "fetchedModels",
            JsonSerializer.Serialize(new List<FetchedModel> { new("old-model", null) })
        );
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"data":[{"id":"new-model"},{"id":"new-model-2"}]}""",
                Encoding.UTF8,
                "application/json"
            ),
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.SetSettingValueAsync("baseUrl", "http://localhost:9999");

        Assert.Empty(sut.FetchedModels);
        Assert.Null(sut.SelectedTranscriptionModelId);
        Assert.Null(sut.SelectedLlmModelId);
        Assert.Empty(
            JsonSerializer.Deserialize<List<FetchedModel>>(
                host.GetSetting<string>("fetchedModels")!
            )!
        );
        Assert.Null(host.GetSetting<string>("selectedModel"));
        Assert.Null(host.GetSetting<string>("selectedLlmModel"));

        await sut.RefreshModelCatalogAsync();

        Assert.Equal(["new-model", "new-model-2"], sut.FetchedModels.Select(m => m.Id));
        Assert.Equal("new-model", sut.SelectedTranscriptionModelId);
        Assert.Equal("new-model", sut.SelectedLlmModelId);
        Assert.Equal("new-model", host.GetSetting<string>("selectedModel"));
        Assert.Equal("new-model", host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task SetApiKeyAsync_CredentialChange_InvalidatesDefaultCatalogAndSelections()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedModel", "old-model");
        host.SetSetting("selectedLlmModel", "old-model");
        host.SetSetting(
            "fetchedModels",
            JsonSerializer.Serialize(new List<FetchedModel> { new("old-model", null) })
        );
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.SetApiKeyAsync("new-key");

        Assert.Empty(sut.FetchedModels);
        Assert.Null(sut.SelectedTranscriptionModelId);
        Assert.Null(sut.SelectedLlmModelId);
        Assert.Null(host.GetSetting<string>("selectedModel"));
        Assert.Null(host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task FullFormSave_BaseUrlChange_DoesNotRestoreStaleSelectionsFromLaterFields()
    {
        // Reproduces the host's full-form save (TrySaveFlatSettingsAsync), which applies
        // every field in definition order. A base-URL change clears the catalog and both
        // selections, but the form still carries the old selectedModel/selectedLlmModel in
        // the later fields; those setters must not re-pair the new endpoint with stale IDs.
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedModel", "old-model");
        host.SetSetting("selectedLlmModel", "old-model");
        host.SetSetting(
            "fetchedModels",
            JsonSerializer.Serialize(new List<FetchedModel> { new("old-model", null) })
        );
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        // Host save order: baseUrl, api-key, selectedModel, selectedLlmModel.
        await sut.SetSettingValueAsync("baseUrl", "http://localhost:9999");
        await sut.SetSettingValueAsync("api-key", "");
        await sut.SetSettingValueAsync("selectedModel", "old-model");
        await sut.SetSettingValueAsync("selectedLlmModel", "old-model");

        Assert.Empty(sut.FetchedModels);
        Assert.Null(sut.SelectedTranscriptionModelId);
        Assert.Null(sut.SelectedLlmModelId);
        Assert.Null(host.GetSetting<string>("selectedModel"));
        Assert.Null(host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task SetItemsAsync_AddsProfile_ExposesRoleWithProfileSelectionId()
    {
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("Local Ollama", "http://localhost:11434", apiKey: "secret123", llmModel: "m1")]);

        Assert.True(result.IsSuccess);

        var llm = Assert.Single(sut.AdditionalLlmProviders);
        Assert.Equal("Local Ollama", llm.ProviderName);
        Assert.True(llm.IsAvailable);

        var selectionId = llm.GetLlmSelectionId();
        Assert.StartsWith("openai-compatible-", selectionId);
        Assert.DoesNotContain(":", selectionId); // must round-trip in plugin:{id}:{model}

        var engine = Assert.Single(sut.AdditionalTranscriptionEngines);
        Assert.Equal(selectionId, engine.GetTranscriptionSelectionId());
        Assert.Equal(sut.PluginId, engine.PluginId); // role keeps the owner's plugin id
    }

    [Fact]
    public async Task AdditionalProfileRole_IsStableAcrossRepeatedGettersAndCapabilityRefresh()
    {
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("Local Ollama", "http://localhost:11434", llmModel: "m1")]
        );

        var firstLlmRole = Assert.Single(sut.AdditionalLlmProviders);
        var firstTranscriptionRole = Assert.Single(sut.AdditionalTranscriptionEngines);
        Assert.Same(firstLlmRole, firstTranscriptionRole);
        Assert.Same(firstLlmRole, Assert.Single(sut.AdditionalLlmProviders));

        var refreshCountBefore = host.CapabilitiesChangedCount;
        var unchangedItems = await sut.GetItemsAsync("profiles");
        var result = await sut.SetItemsAsync("profiles", unchangedItems);

        Assert.True(result.IsSuccess);
        Assert.True(host.CapabilitiesChangedCount > refreshCountBefore);
        Assert.Same(firstLlmRole, Assert.Single(sut.AdditionalLlmProviders));
        Assert.Same(
            firstTranscriptionRole,
            Assert.Single(sut.AdditionalTranscriptionEngines)
        );
    }

    [Fact]
    public async Task AdditionalProfileRole_ChangedOrRemovedProfileInvalidatesCacheEntry()
    {
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("Original", "http://localhost:11434", llmModel: "m1")]
        );

        var originalRole = Assert.Single(sut.AdditionalLlmProviders);
        var profileId = Assert.Single(await sut.GetItemsAsync("profiles")).Values["__id"];

        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("Changed", "http://localhost:11434", llmModel: "m1", id: profileId)]
        );
        var changedRole = Assert.Single(sut.AdditionalLlmProviders);
        Assert.NotSame(originalRole, changedRole);

        await sut.SetItemsAsync("profiles", []);
        Assert.Empty(sut.AdditionalLlmProviders);

        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("Changed", "http://localhost:11434", llmModel: "m1", id: profileId)]
        );
        Assert.NotSame(changedRole, Assert.Single(sut.AdditionalLlmProviders));
    }

    [Fact]
    public async Task GetItemsAsync_DoesNotEchoApiKey()
    {
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("Local Ollama", "http://localhost:11434", apiKey: "secret123")]);

        var item = Assert.Single(await sut.GetItemsAsync("profiles"));

        Assert.Null(item.Values["api-key"]);
        Assert.Equal("Local Ollama", item.Values["name"]);
        Assert.Equal("http://localhost:11434", item.Values["baseUrl"]);
    }

    [Fact]
    public async Task SetItemsAsync_NullApiKey_KeepsStoredKeyAcrossUnrelatedSave()
    {
        // Pre-fix host behavior never delivered this null sentinel: it submitted
        // "" for both untouched and cleared fields, which the plugin kept. The
        // plugin's null behavior itself already kept the key; this pins the now
        // reachable untouched-secret contract.
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("Original", "http://localhost:11434", apiKey: "stored-key")]
        );
        var id = Assert.Single(await sut.GetItemsAsync("profiles")).Values["__id"];
        var secretReference = $"api-key.{id}";

        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("Renamed", "http://localhost:11434", apiKey: null, id: id)]
        );

        Assert.Equal("stored-key", host.Secrets[secretReference]);
        Assert.Single(host.StoredSecrets);
        Assert.Empty(host.DeletedSecretKeys);
    }

    [Fact]
    public async Task SetItemsAsync_BlankApiKeyWithStoredKey_DeletesSecretAndDropsCatalog()
    {
        // Pre-fix, NullIfWhiteSpace mapped "" to the keep sentinel, so no delete
        // occurred and the credential-bound catalog survived unchanged.
        var requestedApiKeys = new List<string?>();
        var handler = new CapturingHandler((request, _) =>
        {
            var apiKey = request.Headers.Authorization?.Parameter;
            requestedApiKeys.Add(apiKey);
            var model = apiKey is null ? "public-model" : "secured-model";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"data":[{"id":"{{model}}"}]}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        var host = new TestPluginHostServices();
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P", "http://localhost:11434", apiKey: "stored-key")]
        );
        var id = Assert.Single(await sut.GetItemsAsync("profiles")).Values["__id"];
        var secretReference = $"api-key.{id}";
        Assert.Contains(
            Assert.Single(sut.AdditionalLlmProviders).SupportedModels,
            model => model.Id == "secured-model"
        );

        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P", "http://localhost:11434", apiKey: "", id: id)]
        );

        Assert.DoesNotContain(secretReference, host.Secrets.Keys);
        Assert.Contains(secretReference, host.DeletedSecretKeys);
        Assert.Equal(["stored-key", null], requestedApiKeys);
        var models = Assert.Single(sut.AdditionalLlmProviders).SupportedModels;
        Assert.Contains(models, model => model.Id == "public-model");
        Assert.DoesNotContain(models, model => model.Id == "secured-model");
    }

    [Fact]
    public async Task SetItemsAsync_BlankApiKeyWithoutStoredKey_IsNoOp()
    {
        // Pre-fix behavior was also a no-op for this keyless case; this is the
        // compatibility guard that ensures the new clear signal does not emit a
        // needless delete or discard an unrelated catalog.
        var modelRequests = 0;
        var handler = new CapturingHandler((_, _) =>
        {
            modelRequests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"m1"}]}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        var host = new TestPluginHostServices();
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P", "http://localhost:11434")]
        );
        var id = Assert.Single(await sut.GetItemsAsync("profiles")).Values["__id"];

        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P", "http://localhost:11434", apiKey: "", id: id)]
        );

        Assert.Empty(host.Secrets);
        Assert.Empty(host.StoredSecrets);
        Assert.Empty(host.DeletedSecretKeys);
        Assert.Equal(1, modelRequests);
        Assert.Contains(
            Assert.Single(sut.AdditionalLlmProviders).SupportedModels,
            model => model.Id == "m1"
        );
    }

    [Fact]
    public async Task SetItemsAsync_NonBlankApiKey_ReplacesStoredKey()
    {
        // Pre-fix replacement already worked. This regression guard proves the
        // new null/blank split leaves the existing non-blank path intact.
        var handler = new CapturingHandler((request, _) =>
        {
            var model = request.Headers.Authorization?.Parameter == "new-key"
                ? "new-key-model"
                : "old-key-model";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"data":[{"id":"{{model}}"}]}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        var host = new TestPluginHostServices();
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P", "http://localhost:11434", apiKey: "old-key")]
        );
        var id = Assert.Single(await sut.GetItemsAsync("profiles")).Values["__id"];
        var secretReference = $"api-key.{id}";

        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P", "http://localhost:11434", apiKey: "new-key", id: id)]
        );

        Assert.Equal("new-key", host.Secrets[secretReference]);
        Assert.Equal(
            [(secretReference, "old-key"), (secretReference, "new-key")],
            host.StoredSecrets
        );
        Assert.Empty(host.DeletedSecretKeys);
        var models = Assert.Single(sut.AdditionalLlmProviders).SupportedModels;
        Assert.Contains(models, model => model.Id == "new-key-model");
        Assert.DoesNotContain(models, model => model.Id == "old-key-model");
    }

    [Fact]
    public async Task AdditionalProfiles_PersistAndReloadWithSecret()
    {
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P1", "http://localhost:11434", apiKey: "k", llmModel: "m1")]);

        // A fresh instance over the same host (same settings + secrets) reloads them.
        var reloaded = new OpenAiCompatiblePlugin(httpClient);
        await reloaded.ActivateAsync(host);

        var llm = Assert.Single(reloaded.AdditionalLlmProviders);
        Assert.Equal("P1", llm.ProviderName);
        Assert.True(llm.IsAvailable);
    }

    [Fact]
    public async Task SetItemsAsync_RejectsInvalidBaseUrl()
    {
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.SetItemsAsync("profiles", [ProfileItem("Bad", "not-a-url")]);

        Assert.False(result.IsSuccess);
        Assert.Empty(sut.AdditionalLlmProviders);
    }

    [Fact]
    public async Task SetItemsAsync_EndpointChange_RefetchesCatalog()
    {
        // /v1/models returns different models depending on the server port, so we can
        // tell whether the catalog was refetched after the base URL changed.
        var handler = new CapturingHandler((request, _) =>
        {
            var models = request.RequestUri!.Port == 11434
                ? """{"data":[{"id":"m1"},{"id":"m2"}]}"""
                : """{"data":[{"id":"x1"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(models, Encoding.UTF8, "application/json"),
            };
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(new TestPluginHostServices());

        await sut.SetItemsAsync("profiles", [ProfileItem("P", "http://localhost:11434")]);
        var id = Assert.Single(await sut.GetItemsAsync("profiles")).Values["__id"];
        Assert.Contains(sut.AdditionalLlmProviders[0].SupportedModels, m => m.Id == "m1");

        // Re-save the SAME profile (same __id) pointing at a different server.
        await sut.SetItemsAsync("profiles", [ProfileItem("P", "http://localhost:9999", id: id)]);

        var models = sut.AdditionalLlmProviders[0].SupportedModels.Select(m => m.Id).ToList();
        Assert.Contains("x1", models);
        Assert.DoesNotContain("m1", models); // stale catalog must not survive the endpoint change
    }

    [Fact]
    public async Task SetItemsAsync_EndpointChange_NormalizesSelectionsAndInvalidatesRole()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            var models = request.RequestUri!.Port == 11434
                ? """{"data":[{"id":"old-model"}]}"""
                : """{"data":[{"id":"new-model"},{"id":"new-model-2"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(models, Encoding.UTF8, "application/json"),
            };
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(new TestPluginHostServices());
        await sut.SetItemsAsync(
            "profiles",
            [
                ProfileItem(
                    "P",
                    "http://localhost:11434",
                    model: "old-model",
                    llmModel: "old-model"
                ),
            ]
        );
        var originalRole = Assert.Single(sut.AdditionalLlmProviders);
        var id = Assert.Single(await sut.GetItemsAsync("profiles")).Values["__id"];

        await sut.SetItemsAsync(
            "profiles",
            [
                ProfileItem(
                    "P",
                    "http://localhost:9999",
                    model: "old-model",
                    llmModel: "old-model",
                    id: id
                ),
            ]
        );

        var item = Assert.Single(await sut.GetItemsAsync("profiles"));
        Assert.Equal("new-model", item.Values["selectedModel"]);
        Assert.Equal("new-model", item.Values["selectedLlmModel"]);
        Assert.NotSame(originalRole, Assert.Single(sut.AdditionalLlmProviders));
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_UpdatesProfileCatalog()
    {
        var modelsJson = """{"data":[{"id":"m1"}]}""";
        // Responder reads the current modelsJson each call, so we can simulate the
        // server's model list changing after the profile was first saved.
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // Reading the reassigned-below modelsJson is the point (see comment above):
            // each call returns the server's current model list.
            // ReSharper disable once AccessToModifiedClosure
            Content = new StringContent(modelsJson, Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(new TestPluginHostServices());
        await sut.SetItemsAsync("profiles", [ProfileItem("P", "http://localhost:11434")]);

        Assert.Contains(sut.AdditionalLlmProviders[0].SupportedModels, m => m.Id == "m1");
        Assert.DoesNotContain(sut.AdditionalLlmProviders[0].SupportedModels, m => m.Id == "m2");

        // Server gains a model; the dropdown-open refresh path should pick it up.
        modelsJson = """{"data":[{"id":"m1"},{"id":"m2"}]}""";
        await sut.RefreshModelCatalogAsync();

        Assert.Contains(sut.AdditionalLlmProviders[0].SupportedModels, m => m.Id == "m2");
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_RemovedDefaultModel_NormalizesBothSelections()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedModel", "m2");
        host.SetSetting("selectedLlmModel", "m2");
        host.SetSetting(
            "fetchedModels",
            JsonSerializer.Serialize(
                new List<FetchedModel> { new("m1", null), new("m2", null) }
            )
        );
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"data":[{"id":"m1"}]}""",
                Encoding.UTF8,
                "application/json"
            ),
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.RefreshModelCatalogAsync();

        Assert.Equal("m1", sut.SelectedTranscriptionModelId);
        Assert.Equal("m1", sut.SelectedLlmModelId);
        Assert.Equal("m1", host.GetSetting<string>("selectedModel"));
        Assert.Equal("m1", host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_RemovedProfileModel_NormalizesBothSelections()
    {
        var modelsJson = """{"data":[{"id":"m1"},{"id":"m2"}]}""";
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // ReSharper disable once AccessToModifiedClosure -- modelsJson is reassigned below before the refresh call, to simulate the server dropping a model.
            Content = new StringContent(modelsJson, Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(new TestPluginHostServices());
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P", "http://localhost:11434", model: "m2", llmModel: "m2")]
        );
        var originalRole = Assert.Single(sut.AdditionalLlmProviders);

        modelsJson = """{"data":[{"id":"m1"}]}""";
        await sut.RefreshModelCatalogAsync();

        var item = Assert.Single(await sut.GetItemsAsync("profiles"));
        Assert.Equal("m1", item.Values["selectedModel"]);
        Assert.Equal("m1", item.Values["selectedLlmModel"]);
        Assert.NotSame(originalRole, Assert.Single(sut.AdditionalLlmProviders));
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_SuccessfulEmptyCatalog_ClearsBothSelections()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedModel", "m1");
        host.SetSetting("selectedLlmModel", "m1");
        host.SetSetting(
            "fetchedModels",
            JsonSerializer.Serialize(new List<FetchedModel> { new("m1", null) })
        );
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"data":[]}""",
                Encoding.UTF8,
                "application/json"
            ),
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.RefreshModelCatalogAsync();

        Assert.Empty(sut.FetchedModels);
        Assert.Null(sut.SelectedTranscriptionModelId);
        Assert.Null(sut.SelectedLlmModelId);
        Assert.Null(host.GetSetting<string>("selectedModel"));
        Assert.Null(host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_TransientFailure_LeavesStateUntouched()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedModel", "default-m2");
        host.SetSetting("selectedLlmModel", "default-m1");
        host.SetSetting(
            "fetchedModels",
            JsonSerializer.Serialize(
                new List<FetchedModel>
                {
                    new("default-m1", null),
                    new("default-m2", null),
                }
            )
        );
        var failModelRequests = false;
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- asserting every request targets /v1/models is the intended verification.
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.EndsWith("/v1/models", request.RequestUri!.AbsolutePath);
            // ReSharper disable once AccessToModifiedClosure -- failModelRequests is flipped below, after the initial SetItemsAsync call, to simulate a transient failure on the subsequent refresh.
            if (failModelRequests)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"profile-m1"},{"id":"profile-m2"}]}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [
                ProfileItem(
                    "P",
                    "http://localhost:9999",
                    model: "profile-m2",
                    llmModel: "profile-m1"
                ),
            ]
        );

        failModelRequests = true;
        await sut.RefreshModelCatalogAsync();

        Assert.Equal(["default-m1", "default-m2"], sut.FetchedModels.Select(m => m.Id));
        Assert.Equal("default-m2", sut.SelectedTranscriptionModelId);
        Assert.Equal("default-m1", sut.SelectedLlmModelId);
        Assert.Equal("default-m2", host.GetSetting<string>("selectedModel"));
        Assert.Equal("default-m1", host.GetSetting<string>("selectedLlmModel"));

        var profile = Assert.Single(await sut.GetItemsAsync("profiles"));
        Assert.Equal("profile-m2", profile.Values["selectedModel"]);
        Assert.Equal("profile-m1", profile.Values["selectedLlmModel"]);
        Assert.Equal(
            ["profile-m1", "profile-m2"],
            Assert.Single(sut.AdditionalLlmProviders).SupportedModels.Select(m => m.Id)
        );
    }

    [Fact]
    public async Task ValidateAsync_TransientCatalogFailure_LeavesPriorStateUntouched()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedModel", "m1");
        host.SetSetting("selectedLlmModel", "m1");
        host.SetSetting(
            "fetchedModels",
            JsonSerializer.Serialize(new List<FetchedModel> { new("m1", null) })
        );
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"not-data":[]}""",
                Encoding.UTF8,
                "application/json"
            ),
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ValidateAsync();

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal(["m1"], sut.FetchedModels.Select(m => m.Id));
        Assert.Equal("m1", sut.SelectedTranscriptionModelId);
        Assert.Equal("m1", sut.SelectedLlmModelId);
        Assert.Equal("m1", host.GetSetting<string>("selectedModel"));
        Assert.Equal("m1", host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task ProcessStreamingAsync_ThroughProfile_StreamsDeltas()
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}",
            "",
            "data: [DONE]",
            "");
        var handler = new CapturingHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path.EndsWith("/chat/completions", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":[{"id":"m1"}]}""", Encoding.UTF8, "application/json"),
                };
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(new TestPluginHostServices()); // streamResponses defaults true
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P", "http://localhost:11434", llmModel: "m1")]);

        var role = Assert.Single(sut.AdditionalLlmProviders);
        var chunks = new List<string>();
        await foreach (var chunk in role.ProcessStreamingAsync("sys", "user", "m1", CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal(["Hel", "lo"], chunks);
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, body);
        }
    }

    private sealed class TestPluginHostServices : IPluginHostServices
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly Dictionary<string, JsonElement> _settings = [];
        public Dictionary<string, string?> Secrets { get; } = [];
        public List<(string Key, string Value)> StoredSecrets { get; } = [];
        public List<string> DeletedSecretKeys { get; } = [];
        public int CapabilitiesChangedCount { get; private set; }

        public Task StoreSecretAsync(string key, string value)
        {
            StoredSecrets.Add((key, value));
            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key) =>
            Task.FromResult(Secrets.GetValueOrDefault(key));

        public Task DeleteSecretAsync(string key)
        {
            DeletedSecretKeys.Add(key);
            Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value) ? value.Deserialize<T>(s_jsonOptions) : default;

        public void SetSetting<T>(string key, T value) =>
            _settings[key] = JsonSerializer.SerializeToElement(value, s_jsonOptions);

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged()
        {
            CapabilitiesChangedCount++;
        }
        public IPluginLocalization Localization { get; } = new TestPluginLocalization();
    }

    private sealed class TestPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
    }

    private sealed class TestPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }
        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent =>
            new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
