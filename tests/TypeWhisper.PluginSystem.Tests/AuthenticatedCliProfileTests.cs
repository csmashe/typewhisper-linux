using System.Text.Json;
using Moq;
using TypeWhisper.Plugin.AuthenticatedCli;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

// CLI profiles: further sessions of the same CLIs, each its own LLM provider.
public sealed partial class AuthenticatedCliPluginTests
{
    private const string FirstProfileId = "11111111-1111-1111-1111-111111111111";
    private const string SecondProfileId = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task SavingAProfile_AddsAStableProviderAndKeepsTheBuiltInRoles()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        var host = CreateHost();
        await plugin.ActivateAsync(host.Object);
        var builtIns = plugin.AdditionalLlmProviders.ToList();
        var session = CreateSessionDirectory(fake, "work");

        var result = await plugin.SetItemsAsync(
            AuthenticatedCliPlugin.ProfilesCollectionKey,
            [ProfileItem(FirstProfileId, "Work", "codex", environment: $"CODEX_HOME={session}")]
        );

        Assert.True(result.IsSuccess, result.Message);
        host.Verify(service => service.SetSetting(
            AuthenticatedCliPlugin.ProfilesSettingName,
            It.Is<List<CliProfileSettings>>(profiles =>
                profiles.Count == 1
                && profiles[0].Id == FirstProfileId
                && profiles[0].Environment!["CODEX_HOME"] == session)), Times.Once);
        host.Verify(service => service.NotifyCapabilitiesChanged(), Times.AtLeastOnce);
        Assert.Equal(4, plugin.AdditionalLlmProviders.Count);
        Assert.Equal(builtIns, plugin.AdditionalLlmProviders.Take(3));
        var role = GetRole(plugin, ProfileSelectionId(FirstProfileId));
        Assert.Equal("Work", role.ProviderName);
        Assert.Equal("default", Assert.Single(role.SupportedModels).Id);

        var items = await plugin.GetItemsAsync(AuthenticatedCliPlugin.ProfilesCollectionKey);
        var item = Assert.Single(items).Values;
        Assert.Equal(FirstProfileId, item["__id"]);
        Assert.Equal("Work", item["name"]);
        Assert.Equal("codex", item["provider"]);
        Assert.Equal("", item["executable"]);
        Assert.Equal($"CODEX_HOME={session}", item["environment"]);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task TwoProfilesOfOneCli_EachRunWithTheirOwnSessionDirectory()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        var inherited = CreateSessionDirectory(fake, "inherited");
        var first = CreateSessionDirectory(fake, "first");
        var second = CreateSessionDirectory(fake, "second");
        Assert.True((await plugin.SetItemsAsync(
            AuthenticatedCliPlugin.ProfilesCollectionKey,
            [
                ProfileItem(FirstProfileId, "First", "codex", environment: $"CODEX_HOME={first}"),
                ProfileItem(SecondProfileId, "Second", "codex", environment: $"CODEX_HOME={second}"),
            ])).IsSuccess);

        using (new TemporaryEnvironment(("CODEX_HOME", inherited)))
        {
            Assert.Equal(first, await RunAndCaptureCodexHomeAsync(ProfileSelectionId(FirstProfileId)));
            Assert.Equal(second, await RunAndCaptureCodexHomeAsync(ProfileSelectionId(SecondProfileId)));
            Assert.Equal(inherited, await RunAndCaptureCodexHomeAsync("authenticated-cli-codex"));
        }

        await plugin.DeactivateAsync();
        return;

        // ReSharper disable AccessToDisposedClosure -- only called above, before plugin and fake dispose.
        async Task<string?> RunAndCaptureCodexHomeAsync(string selectionId)
        {
            var result = await GetRole(plugin, selectionId)
                .ProcessAsync("Instruction", "Input", "default", CancellationToken.None);
            Assert.Equal("processed", result);
            using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(fake.CapturePath));
            return capture.RootElement.GetProperty("environment").TryGetProperty("CODEX_HOME", out var value)
                ? value.GetString()
                : null;
        }
        // ReSharper restore AccessToDisposedClosure
    }

    [Fact]
    public async Task ClaudeProfile_KeepsTheBuiltInToolRestrictionsAndStructuredOutput()
    {
        using var fake = FakeCliInstallation.Create("success", "claude");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        var session = CreateSessionDirectory(fake, "claude-work");
        Assert.True((await plugin.SetItemsAsync(
            AuthenticatedCliPlugin.ProfilesCollectionKey,
            [ProfileItem(FirstProfileId, "Claude work", "claude", environment: $"CLAUDE_CONFIG_DIR={session}")]
        )).IsSuccess);

        Assert.Equal(
            "processed",
            await GetRole(plugin, "authenticated-cli-claude")
                .ProcessAsync("Instruction", "Input", "default", CancellationToken.None)
        );
        var builtInArguments = await ReadCapturedArgumentsAsync(fake);
        Assert.Equal(
            "processed",
            await GetRole(plugin, ProfileSelectionId(FirstProfileId))
                .ProcessAsync("Instruction", "Input", "default", CancellationToken.None)
        );

        Assert.Equal(builtInArguments, await ReadCapturedArgumentsAsync(fake));
        Assert.Contains("--json-schema", builtInArguments);
        var tools = builtInArguments.IndexOf("--tools");
        Assert.Equal("", builtInArguments[tools + 1]);
        var disallowed = builtInArguments.IndexOf("--disallowedTools");
        Assert.Equal("mcp__*", builtInArguments[disallowed + 1]);
        Assert.DoesNotContain("*", builtInArguments);
        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(fake.CapturePath));
        Assert.Equal(
            session,
            capture.RootElement.GetProperty("environment").GetProperty("CLAUDE_CONFIG_DIR").GetString()
        );
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task OpenCodeProfiles_KeepSeparateCatalogsAndNeverBorrowAnotherProfilesModels()
    {
        using var working = FakeCliInstallation.Create("success", "opencode");
        using var failing = FakeCliInstallation.Create("catalog-fail", "opencode");
        var path = $"{working.DirectoryPath}:{failing.DirectoryPath}";
        using var plugin = new AuthenticatedCliPlugin(
            new CliExecutableDiscovery(() => path),
            CreateRunner()
        ) { RuntimeDirectory = null };
        var host = CreateHost();
        await plugin.ActivateAsync(host.Object);
        Assert.True((await plugin.SetItemsAsync(
            AuthenticatedCliPlugin.ProfilesCollectionKey,
            [
                ProfileItem(FirstProfileId, "Working", "opencode", working.ExecutablePath("opencode")),
                ProfileItem(SecondProfileId, "Failing", "opencode", failing.ExecutablePath("opencode")),
            ])).IsSuccess);

        await plugin.RefreshFromSettingsAsync();

        var workingRole = GetRole(plugin, ProfileSelectionId(FirstProfileId));
        var failingRole = GetRole(plugin, ProfileSelectionId(SecondProfileId));
        Assert.True(workingRole.IsAvailable);
        Assert.Equal("opencode/muse-spark-1.3-contributor-free", Assert.Single(workingRole.SupportedModels).Id);
        Assert.False(failingRole.IsAvailable);
        Assert.Empty(failingRole.SupportedModels);
        Assert.Equal(
            CliAvailabilityState.ModelCatalogUnavailable,
            plugin.GetSnapshot(plugin.FindDescriptor(ProfileSelectionId(SecondProfileId))!).State
        );
        // Two installations on PATH make the built-in provider ambiguous, never a third catalog.
        Assert.False(GetRole(plugin, "authenticated-cli-opencode").IsAvailable);
        Assert.Empty(plugin.GetOpenCodeFreeModels());
        host.Verify(service => service.SetSetting(
            AuthenticatedCliPlugin.ProfilesSettingName,
            It.Is<List<CliProfileSettings>>(profiles =>
                profiles.Single(profile => profile.Id == FirstProfileId).OpenCodeCatalog != null
                && profiles.Single(profile => profile.Id == SecondProfileId).OpenCodeCatalog == null)),
            Times.AtLeastOnce);
        host.Verify(service => service.SetSetting(
            AuthenticatedCliPlugin.OpenCodeCatalogSettingName,
            It.IsAny<OpenCodeModelCatalogCache>()), Times.Never);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task ChangingAProfilesConnection_DropsItsCatalogAndDiscardsTheOldProbe()
    {
        using var working = FakeCliInstallation.Create("success", "opencode");
        using var failing = FakeCliInstallation.Create("catalog-fail", "opencode");
        var runner = new PausingRunner(CreateRunner());
        var path = $"{working.DirectoryPath}:{failing.DirectoryPath}";
        using var plugin = new AuthenticatedCliPlugin(
            new CliExecutableDiscovery(() => path),
            runner
        ) { RuntimeDirectory = null };
        await plugin.ActivateAsync(CreateHost().Object);
        Assert.True((await plugin.SetItemsAsync(
            AuthenticatedCliPlugin.ProfilesCollectionKey,
            [ProfileItem(FirstProfileId, "Zen", "opencode", working.ExecutablePath("opencode"))]
        )).IsSuccess);
        await plugin.WaitForProfileRefreshAsync();
        var role = GetRole(plugin, ProfileSelectionId(FirstProfileId));
        Assert.True(role.IsAvailable);
        var oldConnection = plugin.FindDescriptor(ProfileSelectionId(FirstProfileId))!;

        // The probe of the old connection is still running when the profile moves elsewhere.
        runner.PauseNextVersionProbe();
        var staleRefresh = plugin.RefreshFromSettingsAsync();
        await runner.Paused.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True((await plugin.SetItemsAsync(
            AuthenticatedCliPlugin.ProfilesCollectionKey,
            [ProfileItem(FirstProfileId, "Zen", "opencode", failing.ExecutablePath("opencode"))]
        )).IsSuccess);

        Assert.False(role.IsAvailable);
        Assert.Empty(role.SupportedModels);
        Assert.Equal(CliAvailabilityState.Checking, plugin.GetSnapshot(oldConnection).State);
        runner.Resume();
        await staleRefresh.WaitAsync(TimeSpan.FromSeconds(30));

        var current = plugin.FindDescriptor(ProfileSelectionId(FirstProfileId))!;
        Assert.NotEqual(CliAvailabilityState.Ready, plugin.GetSnapshot(current).State);
        Assert.Empty(role.SupportedModels);
        Assert.Same(role, GetRole(plugin, ProfileSelectionId(FirstProfileId)));
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task StalledProfileProbe_DoesNotHoldUpAnotherProvidersRequestOrSave()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        fake.Install("opencode");
        var runner = new PausingRunner(CreateRunner());
        var path = fake.DirectoryPath;
        using var plugin = new AuthenticatedCliPlugin(new CliExecutableDiscovery(() => path), runner)
        {
            RuntimeDirectory = null,
        };
        await plugin.ActivateAsync(CreateHost().Object);
        await plugin.RefreshFromSettingsAsync();
        runner.PauseNextVersionProbe(fake.ExecutablePath("codex"));
        Assert.True((await plugin.SetItemsAsync(
            AuthenticatedCliPlugin.ProfilesCollectionKey,
            [ProfileItem(FirstProfileId, "Slow", "codex")]
        )).IsSuccess);
        await runner.Paused.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            // OpenCode launches under its refresh gate, which a shared gate would have blocked.
            var result = await GetRole(plugin, "authenticated-cli-opencode")
                .ProcessAsync("Instruction", "Input", "opencode/muse-spark-1.3-contributor-free", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("processed", result);
            await plugin.SetSettingValueAsync("opencodeInstallation", fake.ExecutablePath("opencode"))
                .WaitAsync(TimeSpan.FromSeconds(30));
            await plugin.SetSettingValueAsync("opencodeInstallation", fake.ExecutablePath("opencode"))
                .WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            runner.Resume();
        }

        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task RenamingAProfile_KeepsItsRoleAndVerifiedStatus()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        Assert.True((await plugin.SetItemsAsync(
            AuthenticatedCliPlugin.ProfilesCollectionKey,
            [ProfileItem(FirstProfileId, "Before", "codex")]
        )).IsSuccess);
        await plugin.RefreshFromSettingsAsync();
        var role = GetRole(plugin, ProfileSelectionId(FirstProfileId));
        Assert.True(role.IsAvailable);

        Assert.True((await plugin.SetItemsAsync(
            AuthenticatedCliPlugin.ProfilesCollectionKey,
            [ProfileItem(FirstProfileId, "After", "codex")]
        )).IsSuccess);

        Assert.Same(role, GetRole(plugin, ProfileSelectionId(FirstProfileId)));
        Assert.Equal("After", role.ProviderName);
        Assert.True(role.IsAvailable);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task RemovedProfile_RefusesRequestsAndLeavesTheProviderList()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        Assert.True((await plugin.SetItemsAsync(
            AuthenticatedCliPlugin.ProfilesCollectionKey,
            [ProfileItem(FirstProfileId, "Gone", "codex")]
        )).IsSuccess);
        var role = GetRole(plugin, ProfileSelectionId(FirstProfileId));

        Assert.True((await plugin.SetItemsAsync(AuthenticatedCliPlugin.ProfilesCollectionKey, [])).IsSuccess);

        Assert.Equal(3, plugin.AdditionalLlmProviders.Count);
        Assert.False(role.IsAvailable);
        Assert.Empty(role.SupportedModels);
        var failure = await Assert.ThrowsAsync<PluginRequestException>(() =>
            role.ProcessAsync("Instruction", "Input", "default", CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.Configuration, failure.FailureKind);
        Assert.False(File.Exists(fake.CapturePath));
        await plugin.DeactivateAsync();
    }

    [Theory]
    [InlineData("codex", "", "CLAUDE_CONFIG_DIR={session}", "CLAUDE_CONFIG_DIR is not a session directory")]
    [InlineData("codex", "", "CODEX_HOME=relative/path", "must be an existing absolute local directory")]
    [InlineData("codex", "", "CODEX_HOME={session}/missing", "must be an existing absolute local directory")]
    [InlineData("codex", "", "CODEX_HOME={session}\nCODEX_HOME={session}", "enter each variable once")]
    [InlineData("codex", "", "PATH=/tmp", "PATH is not a session directory")]
    [InlineData("codex", "{claude}", "", "choose Automatic or an installation")]
    [InlineData("gemini", "", "", "choose Codex, Claude Code or OpenCode")]
    public async Task InvalidProfile_IsRejectedWithoutSaving(
        string provider,
        string executable,
        string environment,
        string expected
    )
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        fake.Install("claude");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        var host = CreateHost();
        await plugin.ActivateAsync(host.Object);
        var session = CreateSessionDirectory(fake, "session");

        var result = await plugin.SetItemsAsync(
            AuthenticatedCliPlugin.ProfilesCollectionKey,
            [
                ProfileItem(
                    FirstProfileId,
                    "Broken",
                    provider,
                    executable.Replace("{claude}", fake.ExecutablePath("claude")),
                    environment.Replace("{session}", session)
                ),
            ]
        );

        Assert.False(result.IsSuccess);
        Assert.Contains(expected, result.Message, StringComparison.OrdinalIgnoreCase);
        host.Verify(service => service.SetSetting(
            AuthenticatedCliPlugin.ProfilesSettingName,
            It.IsAny<List<CliProfileSettings>>()), Times.Never);
        Assert.Equal(3, plugin.AdditionalLlmProviders.Count);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task ProfileNamesAndCount_AreBounded()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);

        var unnamed = await plugin.SetItemsAsync(
            AuthenticatedCliPlugin.ProfilesCollectionKey,
            [ProfileItem(FirstProfileId, "  ", "codex")]
        );
        var tooMany = await plugin.SetItemsAsync(
            AuthenticatedCliPlugin.ProfilesCollectionKey,
            Enumerable.Range(0, AuthenticatedCliPlugin.MaximumProfiles + 1)
                .Select(index => ProfileItem(Guid.NewGuid().ToString("D"), $"P{index}", "codex"))
                .ToList()
        );

        Assert.False(unnamed.IsSuccess);
        Assert.False(tooMany.IsSuccess);
        Assert.Equal(3, plugin.AdditionalLlmProviders.Count);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task ProfileWhoseSessionDirectoryDisappeared_ReportsItAndDoesNotLaunch()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        var session = CreateSessionDirectory(fake, "removed-later");
        Assert.True((await plugin.SetItemsAsync(
            AuthenticatedCliPlugin.ProfilesCollectionKey,
            [ProfileItem(FirstProfileId, "Work", "codex", environment: $"CODEX_HOME={session}")]
        )).IsSuccess);
        Directory.Delete(session);

        await plugin.RefreshFromSettingsAsync();

        var role = GetRole(plugin, ProfileSelectionId(FirstProfileId));
        Assert.Equal(
            CliAvailabilityState.SessionDirectoryUnavailable,
            plugin.GetSnapshot(plugin.FindDescriptor(ProfileSelectionId(FirstProfileId))!).State
        );
        Assert.False(role.IsAvailable);
        await Assert.ThrowsAsync<PluginRequestException>(() =>
            role.ProcessAsync("Instruction", "Input", "default", CancellationToken.None));
        Assert.False(File.Exists(fake.CapturePath));
        Assert.True(GetRole(plugin, "authenticated-cli-codex").IsAvailable);

        // The offline profile must not block editing the rest of the list.
        var saved = await plugin.SetItemsAsync(
            AuthenticatedCliPlugin.ProfilesCollectionKey,
            [
                ProfileItem(FirstProfileId, "Work", "codex", environment: $"CODEX_HOME={session}"),
                ProfileItem(SecondProfileId, "Other", "codex"),
            ]
        );
        Assert.True(saved.IsSuccess, saved.Message);
        Assert.Equal(5, plugin.AdditionalLlmProviders.Count);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task FailedInstallationWrite_IsRetriedByTheNextSave()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        var host = CreateHost();
        var path = fake.ExecutablePath("codex");
        host.SetupSequence(service => service.SetSetting("codexInstallation", path))
            .Throws(new IOException("disk full"))
            .Pass();
        await plugin.ActivateAsync(host.Object);
        await plugin.RefreshFromSettingsAsync();

        await plugin.SetSettingValueAsync("codexInstallation", path);
        await plugin.SetSettingValueAsync("codexInstallation", path);

        host.Verify(service => service.SetSetting("codexInstallation", path), Times.Exactly(2));
        Assert.Equal(path, await plugin.GetSettingValueAsync("codexInstallation"));
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task SavedProfiles_AreRestoredWithTheirCatalogAndInvalidEntriesSkipped()
    {
        using var fake = FakeCliInstallation.Create("catalog-fail", "opencode");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        var host = CreateHost();
        host.Setup(service => service.GetSetting<List<CliProfileSettings>>(AuthenticatedCliPlugin.ProfilesSettingName))
            .Returns(
            [
                new CliProfileSettings(
                    FirstProfileId,
                    "Restored",
                    "opencode",
                    null,
                    [],
                    new OpenCodeModelCatalogCache(
                        1,
                        DateTimeOffset.UnixEpoch,
                        [new OpenCodeCachedModel("opencode/cached-free", "Cached Free", [])]
                    )
                ),
                new CliProfileSettings(SecondProfileId, "Unknown CLI", "gemini", null, []),
                new CliProfileSettings("not a valid id", "Bad id", "codex", null, []),
            ]);

        await plugin.ActivateAsync(host.Object);
        await plugin.RefreshFromSettingsAsync();

        Assert.Equal(4, plugin.AdditionalLlmProviders.Count);
        var role = GetRole(plugin, ProfileSelectionId(FirstProfileId));
        Assert.Equal("Restored", role.ProviderName);
        // The refresh failed, so the restored list is the last known good one.
        Assert.True(role.IsAvailable);
        Assert.Equal("opencode/cached-free", Assert.Single(role.SupportedModels).Id);
        Assert.Empty(plugin.GetOpenCodeFreeModels());
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task CancellingAProfileRequest_StopsTheCli()
    {
        using var fake = FakeCliInstallation.Create("timeout", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        Assert.True((await plugin.SetItemsAsync(
            AuthenticatedCliPlugin.ProfilesCollectionKey,
            [ProfileItem(FirstProfileId, "Work", "codex")]
        )).IsSuccess);
        await plugin.RefreshFromSettingsAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GetRole(plugin, ProfileSelectionId(FirstProfileId))
                .ProcessAsync("Instruction", "Input", "default", cancellation.Token));
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task ValidateAsync_ReportsEveryProfileByName()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        Assert.True((await plugin.SetItemsAsync(
            AuthenticatedCliPlugin.ProfilesCollectionKey,
            [ProfileItem(FirstProfileId, "Work account", "codex")]
        )).IsSuccess);

        var result = await plugin.ValidateAsync();

        Assert.NotNull(result);
        Assert.Contains("Work account: Ready", result.Message);
        Assert.Contains(
            "Work account: Ready",
            Assert.Single(plugin.GetCollectionDefinitions()).Description
        );
        await plugin.DeactivateAsync();
    }

    private static PluginCollectionItem ProfileItem(
        string id,
        string name,
        string provider,
        string executable = "",
        string environment = ""
    ) =>
        new(new Dictionary<string, string?>
        {
            ["__id"] = id,
            ["name"] = name,
            ["provider"] = provider,
            ["executable"] = executable,
            ["environment"] = environment,
        });

    private static string ProfileSelectionId(string profileId) => "authenticated-cli-profile-" + profileId;

    private static string CreateSessionDirectory(FakeCliInstallation fake, string name)
    {
        var path = Path.Join(fake.RootPath, "sessions", name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<List<string>> ReadCapturedArgumentsAsync(FakeCliInstallation fake)
    {
        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(fake.CapturePath));
        return capture.RootElement.GetProperty("arguments").EnumerateArray()
            .Select(argument => argument.GetString()!)
            .ToList();
    }

    /// <summary>Holds one version probe until released, to overlap a probe with a profile save.</summary>
    private sealed class PausingRunner(ICliProcessRunner inner) : ICliProcessRunner
    {
        private readonly SemaphoreSlim _release = new(0, 1);
        private int _pauseNext;
        private string? _executable;

        internal SemaphoreSlim Paused { get; } = new(0, 1);

        internal void PauseNextVersionProbe(string? executable = null)
        {
            Volatile.Write(ref _executable, executable);
            Volatile.Write(ref _pauseNext, 1);
        }

        internal void Resume() => _release.Release();

        public async Task<CliProcessResult> RunAsync(CliProcessRequest request, CancellationToken cancellationToken)
        {
            // ReSharper disable once InvertIf -- inverting would duplicate the delegated run below.
            if (request.Arguments.SequenceEqual(["--version"])
                && (Volatile.Read(ref _executable) is not { } executable
                    || string.Equals(executable, request.ExecutablePath, StringComparison.Ordinal))
                && Interlocked.Exchange(ref _pauseNext, 0) == 1)
            {
                Paused.Release();
                await _release.WaitAsync(cancellationToken);
            }

            return await inner.RunAsync(request, cancellationToken);
        }
    }
}
