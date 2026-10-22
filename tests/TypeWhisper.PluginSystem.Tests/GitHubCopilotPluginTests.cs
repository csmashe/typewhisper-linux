using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Plugin.GitHubCopilot;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

// The plugin only runs on Linux: it owns Unix file modes for the runtime it downloads.
[SupportedOSPlatform("linux")]
public sealed partial class GitHubCopilotPluginTests : IDisposable
{
    private static readonly CopilotAccount s_personal = new("https://github.com", "personal");
    private static readonly CopilotAccount s_work = new("https://github.com", "work");
    private readonly string _root = Path.Join(Path.GetTempPath(), "copilot-plugin-" + Guid.NewGuid().ToString("N"));

    public GitHubCopilotPluginTests()
    {
        Directory.CreateDirectory(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task EachProviderSendsItsTextThroughItsOwnAccount()
    {
        var (plugin, transport, _) = await ActivateAsync();
        await plugin.SetSettingValueAsync(GitHubCopilotPlugin.AccountSettingKey, s_personal.Key);
        await SaveProfilesAsync(plugin, ("Work", s_work));
        await plugin.RefreshAsync(CancellationToken.None);

        var defaultRole = Role(plugin, GitHubCopilotPlugin.DefaultSelectionId);
        var workRole = plugin.AdditionalLlmProviders.Single(role => !ReferenceEquals(role, defaultRole));
        Assert.Equal("GitHub Copilot", defaultRole.ProviderName);
        Assert.Equal("GitHub Copilot · Work", workRole.ProviderName);
        Assert.True(defaultRole.IsAvailable);
        Assert.True(workRole.IsAvailable);

        Assert.Equal("personal:model-a:hello", await defaultRole.ProcessAsync("s", "hello", "model-a", CancellationToken.None));
        Assert.Equal("work:model-b:hello", await workRole.ProcessAsync("s", "hello", "model-b", CancellationToken.None));
        Assert.Equal(["personal", "work"], transport.Processed.Select(request => request.Login));
    }

    [Fact]
    public async Task ASignedOutAccountFailsWithoutFallingBackToAnotherAccount()
    {
        var (plugin, transport, host) = await ActivateAsync();
        await plugin.SetSettingValueAsync(GitHubCopilotPlugin.AccountSettingKey, s_personal.Key);
        await SaveProfilesAsync(plugin, ("Work", s_work));
        await plugin.RefreshAsync(CancellationToken.None);
        transport.SignedIn.Remove("personal");

        var defaultRole = Role(plugin, GitHubCopilotPlugin.DefaultSelectionId);
        var failure = await Assert.ThrowsAsync<PluginRequestException>(() =>
            defaultRole.ProcessAsync("s", "private", "model-a", CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.Authentication, failure.FailureKind);
        Assert.False(defaultRole.IsAvailable);
        Assert.DoesNotContain(transport.Processed, request => request.Login == "work");

        var workRole = plugin.AdditionalLlmProviders.Single(role => !ReferenceEquals(role, defaultRole));
        Assert.Equal("work:model-a:x", await workRole.ProcessAsync("s", "x", "model-a", CancellationToken.None));
        // The binding is kept, so signing back in restores the same provider.
        Assert.Equal(s_personal.Key, await plugin.GetSettingValueAsync(GitHubCopilotPlugin.AccountSettingKey));
        Assert.True(host.Notifications > 0);
    }

    [Fact]
    public async Task AnAccountThatCannotLoadModelsWhileSignedOutBecomesUnavailable()
    {
        var (plugin, transport, host) = await ActivateAsync();
        await plugin.SetSettingValueAsync(GitHubCopilotPlugin.AccountSettingKey, s_personal.Key);
        await SaveProfilesAsync(plugin, ("Work", s_work));
        await plugin.RefreshAsync(CancellationToken.None);
        var defaultRole = Role(plugin, GitHubCopilotPlugin.DefaultSelectionId);
        Assert.True(defaultRole.IsAvailable);

        transport.UnbindableLogins.Add("personal");
        var notifications = host.Notifications;
        await plugin.RefreshAsync(CancellationToken.None);

        Assert.False(defaultRole.IsAvailable);
        Assert.True(plugin.AdditionalLlmProviders.Single(role => !ReferenceEquals(role, defaultRole)).IsAvailable);
        Assert.True(host.Notifications > notifications);

        transport.UnbindableLogins.Clear();
        await plugin.RefreshAsync(CancellationToken.None);
        Assert.True(defaultRole.IsAvailable);
    }

    [Fact]
    public async Task ProfileRolesKeepTheirInstanceAndSelectionIdWhenRenamed()
    {
        var (plugin, _, _) = await ActivateAsync();
        await plugin.RefreshAsync(CancellationToken.None);
        await SaveProfilesAsync(plugin, ("Work", s_work));
        var role = Assert.Single(plugin.AdditionalLlmProviders);
        var selectionId = Assert.IsType<ILlmProviderSelectionIdentity>(role, exactMatch: false).LlmSelectionId;
        Assert.StartsWith(GitHubCopilotPlugin.ProfileSelectionIdPrefix, selectionId);

        var items = await plugin.GetItemsAsync(GitHubCopilotPlugin.ProfilesCollectionKey);
        var renamed = new Dictionary<string, string?>(items[0].Values) { [GitHubCopilotPlugin.ProfileNameField] = "Office" };
        Assert.True((await plugin.SetItemsAsync(GitHubCopilotPlugin.ProfilesCollectionKey, [new PluginCollectionItem(renamed)])).IsSuccess);
        Assert.Same(role, Assert.Single(plugin.AdditionalLlmProviders));
        Assert.Equal("GitHub Copilot · Office", role.ProviderName);
    }

    [Fact]
    public async Task ProfilesOnlyAcceptSignedInAccountsAndValidNames()
    {
        var (plugin, _, _) = await ActivateAsync();
        await plugin.RefreshAsync(CancellationToken.None);
        var stranger = new CopilotAccount("https://github.com", "stranger");

        Assert.False((await SaveProfilesAsync(plugin, ("Stranger", stranger))).IsSuccess);
        Assert.False((await SaveProfilesAsync(plugin, ("  ", s_work))).IsSuccess);
        Assert.False((await SaveProfilesAsync(plugin, (new string('x', 101), s_work))).IsSuccess);
        Assert.False((await SaveProfilesAsync(
            plugin,
            Enumerable.Range(0, GitHubCopilotPlugin.MaximumProfiles + 1).Select(i => ($"P{i}", s_work)).ToArray())).IsSuccess);
        Assert.Empty(plugin.AdditionalLlmProviders);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            plugin.SetSettingValueAsync(GitHubCopilotPlugin.AccountSettingKey, stranger.Key));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            plugin.SetSettingValueAsync(GitHubCopilotPlugin.CacheSettingKey, "15"));
    }

    [Fact]
    public async Task AFailedSettingsWriteKeepsThePublishedConfiguration()
    {
        var (plugin, _, host) = await ActivateAsync();
        await plugin.RefreshAsync(CancellationToken.None);
        await SaveProfilesAsync(plugin, ("Work", s_work));
        host.FailSettingName = GitHubCopilotPlugin.ConfigurationSettingName;

        await Assert.ThrowsAsync<IOException>(() => SaveProfilesAsync(plugin, ("Other", s_personal)));
        await Assert.ThrowsAsync<IOException>(() =>
            plugin.SetSettingValueAsync(GitHubCopilotPlugin.AccountSettingKey, s_personal.Key));
        var role = Assert.Single(plugin.AdditionalLlmProviders);
        Assert.Equal("GitHub Copilot · Work", role.ProviderName);
        Assert.Equal("", await plugin.GetSettingValueAsync(GitHubCopilotPlugin.AccountSettingKey));
    }

    [Fact]
    public async Task ProfilesAndModelListsSurviveARestart()
    {
        var (plugin, _, host) = await ActivateAsync();
        await plugin.SetSettingValueAsync(GitHubCopilotPlugin.AccountSettingKey, s_personal.Key);
        await SaveProfilesAsync(plugin, ("Work", s_work));
        await plugin.SetSettingValueAsync(GitHubCopilotPlugin.CacheSettingKey, "60");
        await plugin.RefreshAsync(CancellationToken.None);
        await plugin.DeactivateAsync();

        // A fresh plugin with an offline runtime still restores both providers and their models.
        var offline = new FakeTransport { FailAccounts = true };
        var restarted = new GitHubCopilotPlugin(_ => offline, root => new FakeInstaller(root));
        await restarted.ActivateAsync(host);
        Assert.Equal(2, restarted.AdditionalLlmProviders.Count);
        Assert.All(restarted.AdditionalLlmProviders, role => Assert.Equal(["model-a", "model-b"], role.SupportedModels.Select(m => m.Id)));
        Assert.Equal("60", await restarted.GetSettingValueAsync(GitHubCopilotPlugin.CacheSettingKey));
        Assert.Equal(TimeSpan.FromHours(1), offline.CatalogLifetime);
        await restarted.DeactivateAsync();
        Assert.DoesNotContain("token", host.RawSettings(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFailedModelRefreshKeepsThePreviousList()
    {
        var (plugin, transport, _) = await ActivateAsync();
        await plugin.SetSettingValueAsync(GitHubCopilotPlugin.AccountSettingKey, s_personal.Key);
        await plugin.RefreshAsync(CancellationToken.None);
        transport.FailModels = true;
        await plugin.RefreshAsync(CancellationToken.None);
        Assert.Equal(["model-a", "model-b"], Role(plugin, GitHubCopilotPlugin.DefaultSelectionId).SupportedModels.Select(m => m.Id));
    }

    [Fact]
    public async Task ARefreshCutShortLoadsANewProfileFirstAndKeepsWhatItLoaded()
    {
        var (plugin, transport, host) = await ActivateAsync();
        await plugin.SetSettingValueAsync(GitHubCopilotPlugin.AccountSettingKey, s_personal.Key);
        await plugin.RefreshAsync(CancellationToken.None);
        await plugin.BackgroundRefresh;
        transport.FailModels = true;
        await SaveProfilesAsync(plugin, ("Work", s_work));
        await plugin.BackgroundRefresh;
        transport.FailModels = false;

        using var cancel = new CancellationTokenSource();
        transport.ModelsLoaded = login =>
        {
            transport.ModelOrder.Enqueue(login);
            // ReSharper disable once AccessToDisposedClosure -- only runs inside the awaited RefreshAsync below.
            cancel.Cancel();
        };
        var notifications = host.Notifications;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.RefreshAsync(cancel.Token));
        Assert.Equal("work", Assert.Single(transport.ModelOrder));
        Assert.True(host.Notifications > notifications);
        var work = plugin.AdditionalLlmProviders.Single(role => role.GetLlmSelectionId() != GitHubCopilotPlugin.DefaultSelectionId);
        Assert.NotEmpty(work.SupportedModels);
    }

    [Fact]
    public async Task SdkErrorsAreNeverShownOrLogged()
    {
        var (plugin, transport, host) = await ActivateAsync();
        await plugin.SetSettingValueAsync(GitHubCopilotPlugin.AccountSettingKey, s_personal.Key);
        await plugin.RefreshAsync(CancellationToken.None);
        transport.ProcessFailure = new InvalidOperationException("ghu_fixturetoken private dictation");

        var failure = await Assert.ThrowsAsync<PluginRequestException>(() =>
            Role(plugin, GitHubCopilotPlugin.DefaultSelectionId).ProcessAsync("s", "private dictation", "model-a", CancellationToken.None));
        Assert.DoesNotContain("ghu_", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private", failure.Message, StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
        Assert.All(host.Logs, line => Assert.DoesNotContain("ghu_", line, StringComparison.Ordinal));
        Assert.Contains(host.Logs, line => line.Contains("type=InvalidOperationException", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("timeout", PluginRequestFailureKind.Timeout)]
    [InlineData("model", PluginRequestFailureKind.Configuration)]
    [InlineData("empty", PluginRequestFailureKind.EmptyResponse)]
    public async Task FailuresMapToTheirRequestKind(string failure, PluginRequestFailureKind kind)
    {
        var (plugin, transport, _) = await ActivateAsync();
        await plugin.SetSettingValueAsync(GitHubCopilotPlugin.AccountSettingKey, s_personal.Key);
        await plugin.RefreshAsync(CancellationToken.None);
        transport.ProcessFailure = failure switch
        {
            "timeout" => new TimeoutException(),
            "model" => new CopilotModelUnavailableException(),
            _ => null,
        };
        transport.EmptyResult = failure == "empty";

        var role = Role(plugin, GitHubCopilotPlugin.DefaultSelectionId);
        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            role.ProcessAsync("s", "u", "model-a", CancellationToken.None));
        Assert.Equal(kind, error.FailureKind);
        if (failure == "model")
        {
            Assert.Equal(["model-b"], role.SupportedModels.Select(m => m.Id));
        }
    }

    [Fact]
    public async Task AQueuedRequestIsNotSentAfterItsProviderMovesToAnotherAccount()
    {
        var (plugin, transport, _) = await ActivateAsync();
        await plugin.SetSettingValueAsync(GitHubCopilotPlugin.AccountSettingKey, s_personal.Key);
        await plugin.RefreshAsync(CancellationToken.None);
        await plugin.BackgroundRefresh;
        var role = Role(plugin, GitHubCopilotPlugin.DefaultSelectionId);
        transport.Hold = true;
        using var cancel = new CancellationTokenSource();
        var first = role.ProcessAsync("s", "first", "model-a", cancel.Token);
        // ReSharper disable once MethodSupportsCancellation -- the timeout is the hang guard.
        await transport.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var queued = role.ProcessAsync("s", "second", "model-a", CancellationToken.None);

        await plugin.SetSettingValueAsync(GitHubCopilotPlugin.AccountSettingKey, s_work.Key, CancellationToken.None);
        transport.Hold = false;
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var failure = await Assert.ThrowsAsync<PluginRequestException>(() => queued);
        Assert.Equal(PluginRequestFailureKind.Configuration, failure.FailureKind);
        Assert.Empty(transport.Processed);
    }

    [Fact]
    public async Task OpeningAModelListReusesFreshListsInsteadOfStartingTheRuntime()
    {
        var (plugin, transport, _) = await ActivateAsync();
        await plugin.SetSettingValueAsync(GitHubCopilotPlugin.AccountSettingKey, s_personal.Key);
        await plugin.RefreshAsync(CancellationToken.None);
        await plugin.BackgroundRefresh;
        var calls = transport.AccountCalls;
        await plugin.RefreshModelCatalogAsync();
        Assert.Equal(calls, transport.AccountCalls);

        await SaveProfilesAsync(plugin, ("Work", s_work));
        await plugin.RefreshModelCatalogAsync();
        Assert.True(transport.AccountCalls > calls);
    }

    [Fact]
    public async Task SessionLogsAreClearedAtStartupAndWhenTheRuntimeIsRemoved()
    {
        var (plugin, transport, _) = await ActivateAsync();
        Assert.Equal(1, transport.SessionCleanups);
        await plugin.ExecuteSettingsActionAsync(GitHubCopilotPlugin.RemoveActionId, CancellationToken.None);
        Assert.Equal(2, transport.SessionCleanups);
    }

    [Fact]
    public async Task AFailedModelListRefreshNeverSurfacesTheSdkMessage()
    {
        var (plugin, transport, host) = await ActivateAsync();
        await plugin.SetSettingValueAsync(GitHubCopilotPlugin.AccountSettingKey, s_personal.Key);
        // Failing before the save keeps the new profile without a list, so the refresh is not skipped.
        transport.AccountFailure = new InvalidOperationException("ghu_fixturetoken");
        await SaveProfilesAsync(plugin, ("Work", s_work));
        await plugin.RefreshModelCatalogAsync();
        Assert.All(host.Logs, line => Assert.DoesNotContain("ghu_", line, StringComparison.Ordinal));
        Assert.Contains(host.Logs, line => line.Contains(
            "event=catalog-refresh-failed type=InvalidOperationException",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallerCancellationIsRethrown()
    {
        var (plugin, transport, _) = await ActivateAsync();
        await plugin.SetSettingValueAsync(GitHubCopilotPlugin.AccountSettingKey, s_personal.Key);
        await plugin.RefreshAsync(CancellationToken.None);
        transport.Hold = true;
        using var cancel = new CancellationTokenSource();
        var request = Role(plugin, GitHubCopilotPlugin.DefaultSelectionId).ProcessAsync("s", "u", "model-a", cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task WithoutTheRuntimeOnlyTheDownloadActionIsOffered()
    {
        var (plugin, transport, _) = await ActivateAsync(installed: false);
        var actions = plugin.GetSettingsActions().ToDictionary(action => action.Id);
        Assert.True(actions[GitHubCopilotPlugin.DownloadActionId].IsEnabled);
        Assert.False(actions[GitHubCopilotPlugin.RefreshActionId].IsEnabled);
        Assert.False(actions[GitHubCopilotPlugin.RemoveActionId].IsEnabled);
        Assert.NotNull(actions[GitHubCopilotPlugin.RemoveActionId].ConfirmationMessage);

        Assert.Empty(plugin.AdditionalLlmProviders);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            plugin.SetSettingValueAsync(GitHubCopilotPlugin.AccountSettingKey, s_personal.Key));

        var result = await plugin.ExecuteSettingsActionAsync(GitHubCopilotPlugin.DownloadActionId, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Message);
        await plugin.SetSettingValueAsync(GitHubCopilotPlugin.AccountSettingKey, s_personal.Key);
        await plugin.RefreshAsync(CancellationToken.None);
        var role = Role(plugin, GitHubCopilotPlugin.DefaultSelectionId);
        Assert.True(role.IsAvailable);
        Assert.Equal("personal:model-a:u", await role.ProcessAsync("s", "u", "model-a", CancellationToken.None));

        await plugin.ExecuteSettingsActionAsync(GitHubCopilotPlugin.RemoveActionId, CancellationToken.None);
        Assert.False(role.IsAvailable);
        var failure = await Assert.ThrowsAsync<PluginRequestException>(() =>
            role.ProcessAsync("s", "u", "model-a", CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.Configuration, failure.FailureKind);
        Assert.Single(transport.Processed);
        Assert.Null(plugin.SettingsProgress);
    }

    [Fact]
    public async Task ARefreshFailureAfterASuccessfulDownloadIsNotReportedAsADownloadFailure()
    {
        var (plugin, transport, _) = await ActivateAsync(installed: false);
        transport.AccountFailure = new TimeoutException();

        var result = await plugin.ExecuteSettingsActionAsync(GitHubCopilotPlugin.DownloadActionId, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(new PluginLocalization(PluginDirectory(), "en").GetString("Error.SettingsTimeout"), result.Message);
    }

    [Fact]
    public void Localization_ShipsEveryKeyInEveryLocaleWithRealTranslations()
    {
        var englishKeys = JsonDocument
            .Parse(File.ReadAllText(Path.Join(PluginDirectory(), "Localization", "en.json")))
            .RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToList();
        foreach (var key in SourceKeys())
        {
            Assert.Contains(key, englishKeys);
        }

        var en = new PluginLocalization(PluginDirectory(), "en");
        foreach (var locale in new[] { "de", "es", "ru" })
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Join(PluginDirectory(), "Localization", locale + ".json")));
            Assert.Equal(englishKeys.Count, document.RootElement.EnumerateObject().Count());
            var translated = new PluginLocalization(PluginDirectory(), locale);
            foreach (var key in englishKeys)
            {
                Assert.True(document.RootElement.TryGetProperty(key, out var value), $"{locale}:{key}");
                Assert.False(string.IsNullOrWhiteSpace(value.GetString()), $"{locale}:{key}");
            }

            Assert.NotEqual(en.GetString("Action.Download"), translated.GetString("Action.Download"));
            Assert.NotEqual(en.GetString("Manifest.Description"), translated.GetString("Manifest.Description"));
        }
    }

    [Fact]
    public async Task RuntimeInstallVerifiesExtractsOwnerOnlyAndRemoves()
    {
        var archive = BuildArchive(
            ("package/prebuilds/linux-x64/copilot-runtime", "#!/bin/sh\n", UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.OtherExecute | UnixFileMode.OtherWrite),
            ("package/prebuilds/linux-x64/runtime.node", "native", UnixFileMode.UserRead | UnixFileMode.OtherRead),
            ("package/index.js", "js", UnixFileMode.UserRead),
            ("package/sdk/deep/nested/file.js", "js", UnixFileMode.UserRead));
        var installer = CreateInstaller(archive, out var handler);

        Assert.False(installer.IsInstalled);
        await installer.InstallAsync(null, CancellationToken.None);
        Assert.True(installer.IsInstalled);
        Assert.Equal(1, handler.Requests);
        var mode = File.GetUnixFileMode(installer.ExecutablePath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Join(Path.GetDirectoryName(installer.ExecutablePath)!, "runtime.node")));
        Assert.Empty(Directory.EnumerateFiles(Path.Join(_root, "runtime", ".download")));
        foreach (var directory in Directory.EnumerateDirectories(Path.Join(_root, "runtime"), "*", SearchOption.AllDirectories))
        {
            Assert.True(PrivateDirectory.IsPrivate(directory), directory);
        }

        await installer.InstallAsync(null, CancellationToken.None);
        Assert.Equal(1, handler.Requests);
        await installer.RemoveAsync(CancellationToken.None);
        Assert.False(installer.IsInstalled);
        Assert.False(File.Exists(installer.ExecutablePath));
    }

    [Fact]
    public async Task ARuntimeThatDoesNotMatchItsPinnedHashIsNeverInstalled()
    {
        var archive = BuildArchive(
            ("package/prebuilds/linux-x64/copilot-runtime", "#!/bin/sh\n", UnixFileMode.UserRead | UnixFileMode.UserExecute),
            ("package/prebuilds/linux-x64/runtime.node", "native", UnixFileMode.UserRead));
        var asset = new CopilotRuntimeAsset("linux-x64", new string('0', 64), archive.Length);
        var installer = new CopilotRuntimeInstaller(
            Path.Join(_root, "runtime"),
            new HttpClient(new ArchiveHandler(archive)),
            asset);

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(null, CancellationToken.None));
        Assert.False(installer.IsInstalled);
        Assert.False(File.Exists(installer.ExecutablePath));
    }

    [Theory]
    [InlineData("package/../escape")]
    [InlineData("/etc/escape")]
    [InlineData("outside/file")]
    [InlineData("package/./file")]
    public async Task UnsafeArchiveEntriesAreRejected(string name)
    {
        var archive = BuildArchive((name, "x", UnixFileMode.UserRead));
        var destination = Path.Join(_root, "extract");
        Directory.CreateDirectory(destination);
        var path = Path.Join(_root, "archive.tgz");
        await File.WriteAllBytesAsync(path, archive);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CopilotRuntimeInstaller.ExtractAsync(path, destination, CancellationToken.None));
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    public async Task LinksInTheArchiveAreRejected(TarEntryType type)
    {
        using var tar = new MemoryStream();
        await using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            await writer.WriteEntryAsync(new PaxTarEntry(type, "package/prebuilds/linux-x64/copilot-runtime")
            {
                LinkName = "/bin/sh",
            });
        }

        var path = Path.Join(_root, "links.tgz");
        await File.WriteAllBytesAsync(path, Gzip(tar.ToArray()));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CopilotRuntimeInstaller.ExtractAsync(path, _root, CancellationToken.None));
    }

    [Fact]
    public void OnlyAProcessRunningOnMuslIsTreatedAsMusl()
    {
        Assert.False(CopilotRuntimeInstaller.IsMusl(["7f00-7f01 r-xp 00000000 08:01 1 /usr/lib/x86_64-linux-gnu/libc.so.6"]));
        Assert.True(CopilotRuntimeInstaller.IsMusl(["7f00-7f01 r-xp 00000000 08:01 1 /lib/ld-musl-x86_64.so.1"]));
        // This test host runs on glibc even if a musl loader is installed.
        Assert.False(CopilotRuntimeInstaller.IsMusl(File.ReadLines("/proc/self/maps")));
    }

    [Fact]
    public async Task ADownloadServerThatNeverAnswersTimesOut()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptTcpClientAsync();
        // ReSharper disable once ShortLivedHttpClient -- one request against a local listener.
        using var client = new HttpClient(new ResponseHeadersTimeoutHandler(TimeSpan.FromMilliseconds(500)));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await Assert.ThrowsAsync<TimeoutException>(() => client.GetAsync(
            $"http://127.0.0.1:{port}/runtime.tgz",
            HttpCompletionOption.ResponseHeadersRead));
        (await accept).Dispose();
    }

    private CopilotRuntimeInstaller CreateInstaller(byte[] archive, out ArchiveHandler handler)
    {
        handler = new ArchiveHandler(archive);
        var asset = new CopilotRuntimeAsset("linux-x64", Convert.ToHexStringLower(SHA256.HashData(archive)), archive.Length);
        return new CopilotRuntimeInstaller(Path.Join(_root, "runtime"), new HttpClient(handler), asset);
    }

    private static byte[] BuildArchive(params (string Name, string Content, UnixFileMode Mode)[] entries)
    {
        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content, mode) in entries)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    Mode = mode,
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                });
            }
        }

        return Gzip(tar.ToArray());
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    private async Task<(GitHubCopilotPlugin Plugin, FakeTransport Transport, TestHost Host)> ActivateAsync(
        bool installed = true
    )
    {
        var transport = new FakeTransport();
        var host = new TestHost(Path.Join(_root, "data"));
        var plugin = new GitHubCopilotPlugin(_ => transport, root => new FakeInstaller(root) { Installed = installed });
        await plugin.ActivateAsync(host);
        if (!installed)
        {
            return (plugin, transport, host);
        }

        // Accounts can only be chosen once discovered.
        await plugin.BackgroundRefresh;
        await plugin.RefreshAsync(CancellationToken.None);
        return (plugin, transport, host);
    }

    private static Task<PluginSettingsValidationResult> SaveProfilesAsync(
        GitHubCopilotPlugin plugin,
        params (string Name, CopilotAccount Account)[] profiles
    ) =>
        plugin.SetItemsAsync(
            GitHubCopilotPlugin.ProfilesCollectionKey,
            profiles.Select(profile => new PluginCollectionItem(new Dictionary<string, string?>
                {
                    [GitHubCopilotPlugin.ProfileNameField] = profile.Name,
                    [GitHubCopilotPlugin.ProfileAccountField] = profile.Account.Key,
                }))
                .ToList());

    private static ILlmProviderRole Role(GitHubCopilotPlugin plugin, string selectionId) =>
        plugin.AdditionalLlmProviders.Single(role => role.GetLlmSelectionId() == selectionId);

    [GeneratedRegex("Loc\\.L\\(\"([A-Za-z.]+)\"")]
    private static partial Regex LocKeyPattern();

    private static IEnumerable<string> SourceKeys() =>
        Directory.EnumerateFiles(PluginDirectory(), "*.cs")
            .SelectMany(file => LocKeyPattern()
                .Matches(File.ReadAllText(file))
                .Select(match => match.Groups[1].Value))
            .Distinct();

    private static string PluginDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Join(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins", "TypeWhisper.Plugin.GitHubCopilot"));

    private sealed class FakeInstaller(string root)
        : CopilotRuntimeInstaller(root, new HttpClient(), new CopilotRuntimeAsset("linux-x64", "", 0))
    {
        internal bool Installed { get; set; } = true;
        internal override bool IsInstalled => Installed;

        internal override Task InstallAsync(IProgress<double>? progress, CancellationToken ct)
        {
            progress?.Report(0.5);
            Installed = true;
            return Task.CompletedTask;
        }

        internal override Task RemoveAsync(CancellationToken ct)
        {
            Installed = false;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTransport : ICopilotTransport
    {
        internal HashSet<string> SignedIn { get; } = ["personal", "work"];
        internal ConcurrentQueue<(string Login, string Model)> Processed { get; } = new();
        internal bool FailAccounts { get; init; }
        internal Exception? AccountFailure { get; set; }
        internal bool FailModels { get; set; }
        internal HashSet<string> UnbindableLogins { get; } = [];
        internal bool EmptyResult { get; set; }
        internal bool Hold { get; set; }
        internal TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? ProcessFailure { get; set; }
        internal TimeSpan CatalogLifetime { get; private set; }

        private int _accountCalls;
        internal int AccountCalls => Volatile.Read(ref _accountCalls);

        public Task<IReadOnlyList<CopilotAccount>> GetAccountsAsync(CancellationToken ct) =>
            Interlocked.Increment(ref _accountCalls) > 0 && (FailAccounts || AccountFailure is not null)
                ? throw AccountFailure ?? new IOException("offline")
                : Task.FromResult<IReadOnlyList<CopilotAccount>>(
                    SignedIn.Order().Select(login => new CopilotAccount("https://github.com", login)).ToList());

        internal ConcurrentQueue<string> ModelOrder { get; } = new();
        internal Action<string>? ModelsLoaded { get; set; }

        public Task<IReadOnlyList<PluginModelInfo>> GetModelsAsync(CopilotAccount account, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailModels)
            {
                throw new IOException("offline");
            }

            if (UnbindableLogins.Contains(account.Login))
            {
                throw new CopilotSignInRequiredException();
            }

            ModelsLoaded?.Invoke(account.Login);
            return Task.FromResult<IReadOnlyList<PluginModelInfo>>([new PluginModelInfo("model-a", "Model A"), new PluginModelInfo("model-b", "Model B")]);
        }

        public async Task<string> ProcessAsync(
            CopilotAccount account,
            string systemPrompt,
            string userText,
            string model,
            CancellationToken ct
        )
        {
            if (Hold)
            {
                Holding.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            if (!SignedIn.Contains(account.Login))
            {
                throw new CopilotSignInRequiredException();
            }

            if (ProcessFailure is not null)
            {
                throw ProcessFailure;
            }

            Processed.Enqueue((account.Login, model));
            return EmptyResult ? " " : $"{account.Login}:{model}:{userText}";
        }

        public void InvalidateCache()
        {
        }

        public void SetCatalogLifetime(TimeSpan lifetime) => CatalogLifetime = lifetime;

        internal int SessionCleanups { get; private set; }

        public void RemoveSessionState() => SessionCleanups++;
    }

    private sealed class ArchiveHandler(byte[] archive) : HttpMessageHandler
    {
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        }
    }

    private sealed class TestHost(string dataDirectory) : IPluginHostServices
    {
        private readonly Dictionary<string, JsonElement> _settings = [];
        // Background refreshes log while the test thread does.
        internal ConcurrentQueue<string> Logs { get; } = new();
        private int _notifications;
        internal int Notifications => Volatile.Read(ref _notifications);
        internal string? FailSettingName { get; set; }

        public string PluginDataDirectory
        {
            get
            {
                Directory.CreateDirectory(dataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                return dataDirectory;
            }
        }

        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus => throw new NotSupportedException();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public IPluginLocalization Localization { get; } = new PluginLocalization(PluginDirectory(), "en");
        public Task StoreSecretAsync(string key, string value) => throw new NotSupportedException();
        public Task<string?> LoadSecretAsync(string key) => throw new NotSupportedException();
        public Task DeleteSecretAsync(string key) => throw new NotSupportedException();

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value) ? value.Deserialize<T>() : default;

        public void SetSetting<T>(string key, T value)
        {
            if (key == FailSettingName)
            {
                throw new IOException("setting write failed");
            }

            _settings[key] = JsonSerializer.SerializeToElement(value);
        }

        public void Log(PluginLogLevel level, string message) => Logs.Enqueue(message);
        public void NotifyCapabilitiesChanged() => Interlocked.Increment(ref _notifications);
        internal string RawSettings() => JsonSerializer.Serialize(_settings);
    }
}
