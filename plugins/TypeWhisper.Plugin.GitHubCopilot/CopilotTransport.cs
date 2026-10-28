using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GitHubCopilot;

internal interface ICopilotTransport
{
    Task<IReadOnlyList<CopilotAccount>> GetAccountsAsync(CancellationToken ct);
    Task<IReadOnlyList<PluginModelInfo>> GetModelsAsync(CopilotAccount account, CancellationToken ct);

    Task<string> ProcessAsync(
        CopilotAccount account,
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    );

    /// <summary>Discards cached account and model checks after a settings change.</summary>
    void InvalidateCache();

    void SetCatalogLifetime(TimeSpan lifetime);

    /// <summary>Deletes session logs left by the runtime; call only while no request runs.</summary>
    void RemoveSessionState();
}

// Only public account identity is retained. SDK account tokens and opaque selection IDs
// are never persisted, logged, or exposed through plugin settings.
internal sealed record CopilotAccount(string Host, string Login)
{
    internal string Key => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Host.ToLowerInvariant().TrimEnd('/') + "\n" + Login.ToLowerInvariant())));

    internal string DisplayName => Login + " · " + new Uri(Host).Host;

    internal bool Matches(CopilotAccount other) => Key == other.Key;
}

internal sealed class CopilotSignInRequiredException : Exception;

internal sealed class CopilotModelUnavailableException : Exception;

/// <summary>
///     Talks to the Copilot runtime through the official SDK. Every call starts its own runtime
///     over stdio and stops it afterwards, so no Copilot process outlives a request.
/// </summary>
internal sealed class CopilotTransport : ICopilotTransport
{
    internal static TimeSpan CatalogLifetime { get; } = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromMinutes(2);

    // Bounds everything around the send: runtime start, account and model checks, session setup.
    private static readonly TimeSpan s_setupTimeout = TimeSpan.FromSeconds(30);

    private const UnixFileMode OwnerExecutable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    // Environment the runtime needs to find the user's Copilot login (~/.copilot or the desktop
    // keyring over the session bus) and reach the network. Tokens, endpoint/runtime overrides and
    // NODE_OPTIONS are deliberately absent.
    private static readonly string[] s_inheritedEnvironment =
    [
        "PATH", "HOME", "USER", "LOGNAME", "LANG", "LC_ALL", "TMPDIR",
        "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "XDG_CACHE_HOME",
        "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS",
        "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "https_proxy", "http_proxy", "no_proxy",
    ];

    private readonly string _dataDirectory;
    private readonly Func<string> _runtimePath;
    private readonly Func<CopilotClientOptions, CopilotClient> _createClient;
    private readonly TimeProvider _time;
    private readonly Func<string, string?> _readEnvironment;
    private TimeSpan _catalogLifetime = CatalogLifetime;

    // Per-turn account resolution and model listing are only pre-checks: every turn still binds and
    // verifies the account on its session and switches with requireAvailable before sending text.
    // Only public model metadata is cached in memory; selection IDs are never retained.
    private readonly ConcurrentDictionary<string, (IReadOnlyList<PluginModelInfo> Models, DateTimeOffset Expires)>
        _catalogs = new();

    internal CopilotTransport(
        string dataDirectory,
        Func<string> runtimePath,
        Func<CopilotClientOptions, CopilotClient>? createClient = null,
        TimeProvider? time = null,
        Func<string, string?>? readEnvironment = null
    )
    {
        _dataDirectory = dataDirectory;
        _runtimePath = runtimePath;
        _createClient = createClient ?? (options => new CopilotClient(options));
        _time = time ?? TimeProvider.System;
        _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
    }

    private string WorkspaceDirectory => Path.Join(_dataDirectory, "workspace");
    private string ConfigDirectory => Path.Join(_dataDirectory, "config");
    internal string SessionStateDirectory => Path.Join(ConfigDirectory, "session-state");
    internal string GhStubDirectory => Path.Join(_dataDirectory, "bin");

    public void InvalidateCache() => _catalogs.Clear();

    public void SetCatalogLifetime(TimeSpan lifetime)
    {
        if (lifetime == _catalogLifetime)
        {
            return;
        }

        _catalogLifetime = lifetime;
        InvalidateCache();
    }

    private bool IsCached(string key, string model) =>
        _catalogs.TryGetValue(key, out var entry)
        && entry.Expires > _time.GetUtcNow()
        && entry.Models.Any(m => m.Id == model);

    private IReadOnlyList<PluginModelInfo> Cache(string key, IReadOnlyList<PluginModelInfo> models)
    {
        _catalogs[key] = (models, _time.GetUtcNow() + _catalogLifetime);
        return models;
    }

    internal CopilotClientOptions CreateClientOptions()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in s_inheritedEnvironment)
        {
            if (_readEnvironment(name) is { Length: > 0 } value)
            {
                environment[name] = value;
            }
        }

        // The runtime otherwise reports the gh CLI login, which the SDK cannot parse (it omits the
        // token) and which is not a Copilot account profile anyway. GH_CONFIG_DIR alone does
        // not hide it (gh falls back to the keyring), so a gh that always fails goes first on PATH.
        environment["GH_CONFIG_DIR"] = Path.Join(_dataDirectory, "gh");
        environment["PATH"] = environment.TryGetValue("PATH", out var path)
            ? GhStubDirectory + Path.PathSeparator + path
            : GhStubDirectory;
        return new CopilotClientOptions
        {
            // Empty mode disables the user's stored login. Keep login compatibility and explicitly
            // disable ambient agent features on every session instead.
            Mode = CopilotClientMode.CopilotCli,
            Connection = RuntimeConnection.ForStdio(_runtimePath()),
            WorkingDirectory = WorkspaceDirectory,
            Environment = environment,
            UseLoggedInUser = true,
            LogLevel = CopilotLogLevel.None,
            EnableRemoteSessions = false,
            BuiltinPluginDirectories = [],
        };
    }

    // Safety controls include experimental APIs; changes require review when upgrading the pinned SDK.
#pragma warning disable GHCP001
    internal SessionConfig CreateSessionConfig(string systemPrompt, string model) => new()
    {
        SessionId = "typewhisper-" + Guid.NewGuid().ToString("N"),
        Model = model,
        WorkingDirectory = WorkspaceDirectory,
        ConfigDirectory = ConfigDirectory,
        SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Replace, Content = systemPrompt },
        AvailableTools = [],
        ExcludedTools = new ToolSet().AddBuiltIn("*").AddCustom("*").AddMcp("*"),
        Tools = [],
        McpServers = new Dictionary<string, McpServerConfig>(),
        DisabledMcpServers = ["*"],
        CustomAgents = [],
        CustomAgentsLocalOnly = true,
        IncludedBuiltinSkills = [],
        SkillDirectories = [],
        PluginDirectories = [],
        InstructionDirectories = [],
        AdditionalDirectories = [],
        EnableConfigDiscovery = false,
        EnableOnDemandInstructionDiscovery = false,
        SkipCustomInstructions = true,
        EnableSkills = false,
        EnableFileHooks = false,
        EnableHostGitOperations = false,
        EnableFileChangeTracking = false,
        EnableSessionStore = false,
        EnableSessionTelemetry = false,
        EnableExperimentalMode = false,
        EnableCitations = false,
        SkipEmbeddingRetrieval = true,
        EmbeddingCacheStorage = EmbeddingCacheStorageMode.InMemory,
        Memory = new MemoryConfiguration { Enabled = false },
        ToolSearch = new ToolSearchConfig { Enabled = false },
        McpOAuthTokenStorage = McpOAuthTokenStorageMode.InMemory,
        CoauthorEnabled = false,
        ManageScheduleEnabled = false,
        Streaming = false,
        IncludeSubAgentStreamingEvents = false,
        InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
        OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.Reject("TypeWhisper only processes text.")),
        Hooks = new SessionHooks
        {
            OnPreToolUse = (_, _) => Task.FromResult<PreToolUseHookOutput?>(new PreToolUseHookOutput { PermissionDecision = "deny" }),
        },
    };

    public async Task<IReadOnlyList<CopilotAccount>> GetAccountsAsync(CancellationToken ct)
    {
        InvalidateCache(); // Account discovery is a settings refresh; re-verify every account afterwards.
        await using var client = CreateClient();
        using var setup = Deadline(ct);
        try
        {
            await client.StartAsync(setup.Token);
            return (await client.Rpc.Account.GetAllUsersAsync(setup.Token))
                .Select(Identity)
                .OfType<CopilotAccount>()
                .DistinctBy(a => a.Key)
                .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
        finally
        {
            await client.ForceStopAsync();
        }
    }

    public async Task<IReadOnlyList<PluginModelInfo>> GetModelsAsync(CopilotAccount account, CancellationToken ct)
    {
        _catalogs.TryRemove(account.Key, out _);
        await using var client = CreateClient();
        using var setup = Deadline(ct);
        try
        {
            var selected = await ResolveAccountAsync(client, account, setup.Token);
            return Cache(account.Key, await ListModelsAsync(client, selected.SelectionId!, setup.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
        finally
        {
            await client.ForceStopAsync();
        }
    }

    public async Task<string> ProcessAsync(
        CopilotAccount account,
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    )
    {
        RemoveSessionState();
        await using var client = CreateClient();
        var config = CreateSessionConfig(systemPrompt, model);
        CopilotSession? session = null;
        var sessionCreationStarted = false;
        using var setup = Deadline(ct);
        try
        {
            if (IsCached(account.Key, model))
            {
                await client.StartAsync(setup.Token);
            }
            else
            {
                var selected = await ResolveAccountAsync(client, account, setup.Token);
                if (Cache(account.Key, await ListModelsAsync(client, selected.SelectionId!, setup.Token))
                    .All(m => m.Id != model))
                {
                    throw new CopilotModelUnavailableException();
                }
            }

            // Bind authentication before choosing the request model or sending any text.
            config.Model = null;
            sessionCreationStarted = true;
            session = await client.CreateSessionAsync(config, setup.Token);
            var binding = await session.Rpc.GitHubAuth.SetCredentialsAsync(
                new SettableAuthInfoUser { Host = account.Host, Login = account.Login },
                setup.Token);
            var auth = await session.Rpc.GitHubAuth.GetStatusAsync(setup.Token);
            if (!binding.Success
                || !auth.IsAuthenticated
                || auth.AuthType != AuthInfoType.User
                || string.IsNullOrWhiteSpace(auth.Host)
                || string.IsNullOrWhiteSpace(auth.Login)
                || !account.Matches(new CopilotAccount(auth.Host, auth.Login)))
            {
                throw new CopilotSignInRequiredException();
            }

            var switched = await session.Rpc.Model.SwitchToAsync(
                model,
                requireAvailable: true,
                cancellationToken: setup.Token);
            if (switched.Deferred == true || switched.ModelId != model)
            {
                throw new CopilotModelUnavailableException();
            }

            var response = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = userText },
                timeout: s_requestTimeout,
                cancellationToken: ct);
            return response?.Data.Content ?? "";
        }
        catch (OperationCanceledException) when (setup.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _catalogs.TryRemove(account.Key, out _);
            throw new TimeoutException();
        }
        // Sign-in, credential, model or provider failures re-verify the account on the next turn.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _catalogs.TryRemove(account.Key, out _);
            throw;
        }
        finally
        {
            // Cancellation of SendAndWait only cancels the local wait. Abort the remote turn
            // before deleting its session, including when session creation lost its response.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (session is not null)
                {
                    try
                    {
                        await session.AbortAsync(cleanup.Token);
                    }
                    catch (Exception)
                    {
                        // Deletion must still be attempted after an abort failure.
                    }
                }

                if (sessionCreationStarted)
                {
                    await client.DeleteSessionAsync(config.SessionId!, cleanup.Token);
                }
            }
            catch (Exception)
            {
                // Stop the owned runtime below even if its RPC connection broke.
            }
            finally
            {
                await client.ForceStopAsync();
                // The runtime wrote the prompt and text to session-state but cannot delete it there.
                RemoveSessionState();
            }
        }
    }

    /// <summary>
    ///     Deletes every session directory under the plugin-owned config directory. Requests are
    ///     serialized and each runtime is stopped first, so no live session is affected.
    /// </summary>
    public void RemoveSessionState()
    {
        if (!Directory.Exists(SessionStateDirectory))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(SessionStateDirectory))
        {
            try
            {
                if (new DirectoryInfo(directory).LinkTarget is null)
                {
                    Directory.Delete(directory, recursive: true);
                }
                else
                {
                    Directory.Delete(directory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retried before and after the next request.
            }
        }
    }

    private static CancellationTokenSource Deadline(CancellationToken ct)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(s_setupTimeout);
        return deadline;
    }

    private CopilotClient CreateClient()
    {
        PrivateDirectory.Ensure(_dataDirectory);
        PrivateDirectory.Ensure(WorkspaceDirectory);
        PrivateDirectory.Ensure(ConfigDirectory);
        PrivateDirectory.Ensure(Path.Join(_dataDirectory, "gh"));
        PrivateDirectory.Ensure(GhStubDirectory);
        WriteGhStub();
        return _createClient(CreateClientOptions());
    }

    private void WriteGhStub()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        const string script = "#!/bin/sh\nexit 1\n";
        var target = Path.Join(GhStubDirectory, "gh");
        var info = new FileInfo(target);
        if (info is { Exists: true, LinkTarget: null, UnixFileMode: OwnerExecutable }
            && File.ReadAllText(target) == script)
        {
            return;
        }

        var staging = target + "." + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(staging, new FileStreamOptions
               {
                   Mode = FileMode.CreateNew,
                   Access = FileAccess.Write,
                   UnixCreateMode = OwnerExecutable,
               }))
        {
            stream.Write(Encoding.ASCII.GetBytes(script));
        }

        // The create mode is subject to umask; a non-executable stub would be skipped for the real gh.
        File.SetUnixFileMode(staging, OwnerExecutable);
        File.Move(staging, target, overwrite: true);
    }

    private static CopilotAccount? Identity(AccountAllUsers entry) =>
        entry.AuthInfo is AuthInfoUser { Host: { } host, Login: { } login }
        && !string.IsNullOrWhiteSpace(login)
        && login.Length <= 100
        && Uri.TryCreate(host, UriKind.Absolute, out var uri)
        && uri is { Scheme: "https", UserInfo.Length: 0, Query.Length: 0, Fragment.Length: 0, AbsolutePath: "/" }
            ? new CopilotAccount(uri.GetLeftPart(UriPartial.Authority), login)
            : null;

    private static async Task<AccountAllUsers> ResolveAccountAsync(
        CopilotClient client,
        CopilotAccount account,
        CancellationToken ct
    )
    {
        await client.StartAsync(ct);
        var matches = (await client.Rpc.Account.GetAllUsersAsync(ct))
            .Where(entry => Identity(entry) is { } identity && account.Matches(identity))
            .ToArray();
        if (matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0].SelectionId))
        {
            throw new CopilotSignInRequiredException();
        }

        return matches[0];
    }

    private static async Task<IReadOnlyList<PluginModelInfo>> ListModelsAsync(
        CopilotClient client,
        string selectionId,
        CancellationToken ct
    ) =>
        (await client.Rpc.Models.ListAsync(selectionId: selectionId, cancellationToken: ct)).Models
        .Where(m => !string.IsNullOrWhiteSpace(m.Id) && (m.Policy is null || m.Policy.State == ModelPolicyState.Enabled))
        .DistinctBy(m => m.Id, StringComparer.Ordinal)
        .Select(m => new PluginModelInfo(m.Id, string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name))
        .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToArray();
#pragma warning restore GHCP001
}
