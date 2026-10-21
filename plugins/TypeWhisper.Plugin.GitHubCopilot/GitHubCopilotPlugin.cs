// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.
using System.Text.RegularExpressions;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GitHubCopilot;

internal sealed record CopilotProfile(string Id, string Name, CopilotAccount Account);

internal sealed record CopilotConfiguration(
    CopilotAccount? DefaultAccount,
    IReadOnlyList<CopilotProfile> Profiles,
    int CacheMinutes = 10
);

/// <summary>
///     Uses a GitHub Copilot subscription through the official SDK and runtime. The default
///     provider and every additional profile are each bound to one signed-in GitHub account;
///     a request never falls back to a different account. TypeWhisper stores only the account's
///     host and login — the runtime keeps the credentials in the user's own Copilot sign-in.
/// </summary>
public sealed partial class GitHubCopilotPlugin :
    ITypeWhisperPlugin,
    IAdditionalLlmProvidersProvider,
    IPluginSettingsProvider,
    IPluginCollectionSettingsProvider,
    IPluginSettingsActions,
    IPluginSettingsActivity,
    IModelCatalogProvider,
    IPluginLocalizationAware
{
    internal const string ConfigurationSettingName = "configuration.v1";
    internal const string CatalogsSettingName = "catalogs.v1";
    internal const string DefaultSelectionId = "github-copilot";
    internal const string ProfileSelectionIdPrefix = "github-copilot-profile-";
    internal const string AccountSettingKey = "account";
    internal const string CacheSettingKey = "catalogCacheMinutes";
    internal const string ProfilesCollectionKey = "profiles";
    internal const string ProfileIdField = "__id";
    internal const string ProfileNameField = "name";
    internal const string ProfileAccountField = "account";
    internal const string DownloadActionId = "download-runtime";
    internal const string RemoveActionId = "remove-runtime";
    internal const string RefreshActionId = "refresh";
    internal const int MaximumProfiles = 16;
    private const int MaximumNameLength = 100;
    private static readonly int[] s_cacheMinutes = [10, 30, 60, 120];
    private static readonly TimeSpan s_settingsTimeout = TimeSpan.FromSeconds(30);
    // Longer than one request (setup, send, cleanup), so only a request stuck behind more than one fails.
    private static readonly TimeSpan s_queueTimeout = TimeSpan.FromMinutes(4);
    private static readonly HttpClient s_httpClient =
        new(new ResponseHeadersTimeoutHandler(TimeSpan.FromSeconds(30))) { Timeout = Timeout.InfiniteTimeSpan };

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex ProfileIdPattern();

    private readonly Lock _stateLock = new();
    // One Copilot runtime at a time, as upstream.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<string, ICopilotTransport>? _transportFactory;
    private readonly Func<string, CopilotRuntimeInstaller>? _installerFactory;
    private readonly CopilotProviderRole _defaultRole;
    private readonly Dictionary<string, CopilotProviderRole> _profileRoles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _signedOutAccounts = new(StringComparer.Ordinal);
    private CopilotConfiguration _configuration = new(null, []);
    private Dictionary<string, IReadOnlyList<PluginModelInfo>> _catalogs = new(StringComparer.Ordinal);
    private IReadOnlyList<CopilotAccount> _accounts = [];
    private IReadOnlyList<ILlmProviderRole> _roles = [];
    private ICopilotTransport? _transport;
    private CopilotRuntimeInstaller? _installer;
    private IPluginHostServices? _host;
    private IPluginLocalization? _injectedLocalization;
    private CancellationTokenSource? _lifetime;
    private DateTimeOffset _catalogsRefreshedAt = DateTimeOffset.MinValue;

    public GitHubCopilotPlugin() : this(null, null)
    {
    }

    internal GitHubCopilotPlugin(
        Func<string, ICopilotTransport>? transportFactory,
        Func<string, CopilotRuntimeInstaller>? installerFactory
    )
    {
        _transportFactory = transportFactory;
        _installerFactory = installerFactory;
        _defaultRole = new CopilotProviderRole(this, null, DefaultSelectionId);
    }

    public string PluginId => "com.typewhisper.github-copilot";
    public string PluginName => Loc.L("Manifest.Name");
    public string PluginVersion => PluginBuildInfo.Version;

    public IReadOnlyList<ILlmProviderRole> AdditionalLlmProviders
    {
        get
        {
            lock (_stateLock)
            {
                return _roles;
            }
        }
    }

    public double? SettingsProgress { get; private set; }

    public event Action<string?>? SettingsActivityChanged;

    private IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    private bool IsRuntimeInstalled => _installer?.IsInstalled == true;

    public void SetLocalization(IPluginLocalization localization) => _injectedLocalization = localization;

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _lifetime = new CancellationTokenSource();
        var dataDirectory = host.PluginDataDirectory;
        _installer = _installerFactory?.Invoke(dataDirectory)
                     ?? new CopilotRuntimeInstaller(Path.Join(dataDirectory, "runtime"), s_httpClient);
        var installer = _installer;
        _transport = _transportFactory?.Invoke(dataDirectory)
                     ?? new CopilotTransport(Path.Join(dataDirectory, "copilot"), () => installer.ExecutablePath);
        var configuration = Restore(host);
        var catalogs = host.GetSetting<Dictionary<string, List<PluginModelInfo>>>(CatalogsSettingName);
        lock (_stateLock)
        {
            _configuration = configuration;
            _catalogs = (catalogs ?? [])
                .ToDictionary(pair => pair.Key, IReadOnlyList<PluginModelInfo> (pair) => pair.Value,
                    StringComparer.Ordinal);
            PublishRoles();
        }

        _transport.SetCatalogLifetime(TimeSpan.FromMinutes(configuration.CacheMinutes));
        // A crash mid-request skips the transport's own cleanup of the logs holding the user's text.
        _transport.RemoveSessionState();
        if (IsRuntimeInstalled)
        {
            StartBackgroundRefresh();
        }

        return Task.CompletedTask;
    }

    public async Task DeactivateAsync()
    {
        if (_lifetime is { } lifetime)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await BackgroundRefresh.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Background refresh failures were already logged.
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _transport?.InvalidateCache();
            lock (_stateLock)
            {
                _accounts = [];
                _signedOutAccounts.Clear();
            }

            _host = null;
            _lifetime?.Dispose();
            _lifetime = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
    }

    internal string ProviderNameFor(string? profileId)
    {
        var name = Loc.L("Manifest.Name");
        if (profileId is null)
        {
            return name;
        }

        lock (_stateLock)
        {
            return _configuration.Profiles.FirstOrDefault(profile => profile.Id == profileId) is { } match
                ? name + " · " + match.Name
                : name;
        }
    }

    internal bool IsAvailableFor(string? profileId)
    {
        if (_host is null || !IsRuntimeInstalled)
        {
            return false;
        }

        lock (_stateLock)
        {
            return AccountFor(profileId) is { } account
                   && !_signedOutAccounts.Contains(account.Key)
                   && _catalogs.TryGetValue(account.Key, out var models)
                   && models.Count > 0;
        }
    }

    internal IReadOnlyList<PluginModelInfo> ModelsFor(string? profileId)
    {
        lock (_stateLock)
        {
            return AccountFor(profileId) is { } account && _catalogs.TryGetValue(account.Key, out var models)
                ? models
                : [];
        }
    }

    internal async Task<string> ProcessForAsync(
        string? profileId,
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    )
    {
        CopilotAccount? account;
        lock (_stateLock)
        {
            account = AccountFor(profileId);
        }

        if (account is null || (profileId is not null && !HasProfile(profileId)))
        {
            throw new PluginRequestException(Loc.L("Error.NoAccount"), PluginRequestFailureKind.Configuration);
        }

        if (_transport is not { } transport || _lifetime is not { } lifetime)
        {
            throw new PluginRequestException(Loc.L("Error.Inactive"), PluginRequestFailureKind.Configuration);
        }

        if (!IsRuntimeInstalled)
        {
            throw new PluginRequestException(Loc.L("Error.RuntimeMissing"), PluginRequestFailureKind.Configuration);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        if (!await _gate.WaitAsync(s_queueTimeout, linked.Token).ConfigureAwait(false))
        {
            throw new PluginRequestException(Loc.L("Error.Timeout"), PluginRequestFailureKind.Timeout);
        }

        try
        {
            // The binding may have changed while this request waited; never send to the old account.
            lock (_stateLock)
            {
                if (AccountFor(profileId)?.Key != account.Key)
                {
                    throw new PluginRequestException(Loc.L("Error.NoAccount"), PluginRequestFailureKind.Configuration);
                }
            }

            var result = await transport
                .ProcessAsync(account, systemPrompt, userText, model, linked.Token)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(result))
            {
                throw new PluginRequestException(Loc.L("Error.EmptyResponse"), PluginRequestFailureKind.EmptyResponse);
            }

            lock (_stateLock)
            {
                _signedOutAccounts.Remove(account.Key);
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (PluginRequestException)
        {
            throw;
        }
        // Never forward SDK errors: they may contain the prompt, the text or credentials.
        catch (Exception ex)
        {
            Log(PluginLogLevel.Warning, $"event=request-failed type={ex.GetType().Name}");
            throw ex switch
            {
                OperationCanceledException => new PluginRequestException(
                    Loc.L("Error.Inactive"), PluginRequestFailureKind.Cancellation),
                TimeoutException => new PluginRequestException(
                    Loc.L("Error.Timeout"), PluginRequestFailureKind.Timeout),
                CopilotSignInRequiredException => SignedOut(account),
                CopilotModelUnavailableException => ModelUnavailable(account, model),
                _ => new PluginRequestException(Loc.L("Error.RequestFailed"), PluginRequestFailureKind.Unknown),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private PluginRequestException SignedOut(CopilotAccount account)
    {
        lock (_stateLock)
        {
            _signedOutAccounts.Add(account.Key);
        }

        _host?.NotifyCapabilitiesChanged();
        return new PluginRequestException(
            Loc.L("Error.SignInRequired", account.DisplayName),
            PluginRequestFailureKind.Authentication);
    }

    private PluginRequestException ModelUnavailable(CopilotAccount account, string model)
    {
        lock (_stateLock)
        {
            if (_catalogs.TryGetValue(account.Key, out var models))
            {
                _catalogs[account.Key] = models.Where(candidate => candidate.Id != model).ToArray();
            }
        }

        _host?.NotifyCapabilitiesChanged();
        return new PluginRequestException(Loc.L("Error.ModelUnavailable", model), PluginRequestFailureKind.Configuration);
    }

    /// <summary>
    ///     Runs when a model list opens. Each refresh starts the runtime once per account, so lists
    ///     still within the chosen cache duration are kept instead.
    /// </summary>
    public Task RefreshModelCatalogAsync(CancellationToken ct = default)
    {
        if (!IsRuntimeInstalled)
        {
            return Task.CompletedTask;
        }

        lock (_stateLock)
        {
            if (DateTimeOffset.UtcNow - _catalogsRefreshedAt < TimeSpan.FromMinutes(_configuration.CacheMinutes)
                && BoundAccounts().All(account => _catalogs.ContainsKey(account.Key)))
            {
                return Task.CompletedTask;
            }
        }

        return RefreshForModelListAsync(ct);
    }

    // The host traces this boundary's exception messages, which may carry SDK diagnostics.
    private async Task RefreshForModelListAsync(CancellationToken ct)
    {
        try
        {
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(PluginLogLevel.Warning, $"event=catalog-refresh-failed type={ex.GetType().Name}");
        }
    }

    /// <summary>
    ///     Re-reads the signed-in accounts, then each bound account's model list. A failed load
    ///     keeps that account's previous list; an account that is no longer signed in becomes
    ///     unavailable without being unbound or replaced.
    /// </summary>
    internal async Task RefreshAsync(CancellationToken ct)
    {
        if (_transport is not { } transport || _lifetime is not { } lifetime)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var accounts = await transport.GetAccountsAsync(linked.Token).ConfigureAwait(false);
            List<CopilotAccount> bound;
            lock (_stateLock)
            {
                _accounts = accounts;
                bound = BoundAccounts().ToList();
                foreach (var account in bound)
                {
                    if (accounts.Any(account.Matches))
                    {
                        _signedOutAccounts.Remove(account.Key);
                    }
                    else
                    {
                        _signedOutAccounts.Add(account.Key);
                    }
                }
            }

            // Accounts without a list go first, so a deadline cannot keep starving a new profile.
            List<CopilotAccount> pending;
            lock (_stateLock)
            {
                pending = bound
                    .Where(account => accounts.Any(account.Matches))
                    .OrderBy(account => _catalogs.ContainsKey(account.Key))
                    .ToList();
            }

            try
            {
                foreach (var account in pending)
                {
                    try
                    {
                        var models = await transport.GetModelsAsync(account, linked.Token).ConfigureAwait(false);
                        lock (_stateLock)
                        {
                            _catalogs[account.Key] = models;
                        }
                    }
                    // Listed by the runtime but no longer bindable; the finally below notifies the host.
                    catch (CopilotSignInRequiredException)
                    {
                        lock (_stateLock)
                        {
                            _signedOutAccounts.Add(account.Key);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log(PluginLogLevel.Warning, $"event=catalog-refresh-failed type={ex.GetType().Name}");
                    }
                }

                lock (_stateLock)
                {
                    _catalogsRefreshedAt = DateTimeOffset.UtcNow;
                }
            }
            finally
            {
                // Keeps the lists already loaded when the deadline cuts the refresh short.
                PersistCatalogs();
            }
        }
        finally
        {
            _gate.Release();
            // Also after a partial or failed refresh, so the host's model lists show what loaded.
            _host?.NotifyCapabilitiesChanged();
        }
    }

    /// <summary>Completes once every background refresh started so far has finished.</summary>
    internal Task BackgroundRefresh { get; private set; } = Task.CompletedTask;

    private void StartBackgroundRefresh()
    {
        if (_lifetime is not { } lifetime)
        {
            return;
        }

        var previous = BackgroundRefresh;
        BackgroundRefresh = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(s_settingsTimeout);
            try
            {
                await RefreshAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Log(PluginLogLevel.Warning, $"event=refresh-failed type={ex.GetType().Name}");
            }
        });
    }

    private void PersistCatalogs()
    {
        lock (_stateLock)
        {
            var keep = BoundAccounts().Select(account => account.Key).ToHashSet(StringComparer.Ordinal);
            _catalogs = _catalogs
                .Where(pair => keep.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            _host?.SetSetting(
                CatalogsSettingName,
                _catalogs.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal));
        }
    }

    // Must be called under _stateLock.
    private CopilotAccount? AccountFor(string? profileId) =>
        profileId is null
            ? _configuration.DefaultAccount
            : _configuration.Profiles.FirstOrDefault(profile => profile.Id == profileId)?.Account;

    private bool HasProfile(string profileId)
    {
        lock (_stateLock)
        {
            return _configuration.Profiles.Any(profile => profile.Id == profileId);
        }
    }

    // Must be called under _stateLock.
    private IEnumerable<CopilotAccount> BoundAccounts() =>
        new[] { _configuration.DefaultAccount }
            .Concat(_configuration.Profiles.Select(profile => profile.Account))
            .OfType<CopilotAccount>()
            .DistinctBy(account => account.Key);

    // Must be called under _stateLock. Role instances stay stable while their profile exists.
    private void PublishRoles()
    {
        foreach (var removed in _profileRoles.Keys
                     .Where(id => _configuration.Profiles.All(profile => profile.Id != id))
                     .ToList())
        {
            _profileRoles.Remove(removed);
        }

        var roles = new List<ILlmProviderRole>();
        if (_configuration.DefaultAccount is not null)
        {
            roles.Add(_defaultRole);
        }

        foreach (var profile in _configuration.Profiles)
        {
            if (!_profileRoles.TryGetValue(profile.Id, out var role))
            {
                role = new CopilotProviderRole(this, profile.Id, ProfileSelectionIdPrefix + profile.Id);
                _profileRoles[profile.Id] = role;
            }

            roles.Add(role);
        }

        _roles = roles;
    }

    private CopilotConfiguration Restore(IPluginHostServices host)
    {
        CopilotConfiguration? saved;
        try
        {
            saved = host.GetSetting<CopilotConfiguration>(ConfigurationSettingName);
        }
        catch (Exception ex)
        {
            Log(PluginLogLevel.Warning, $"event=configuration-restore-failed type={ex.GetType().Name}");
            return new CopilotConfiguration(null, []);
        }

        if (saved is null)
        {
            return new CopilotConfiguration(null, []);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        // ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract, ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- saved JSON can hold nulls the annotations rule out.
        var profiles = (saved.Profiles ?? [])
            .Where(profile => profile is not null
                              && ProfileIdPattern().IsMatch(profile.Id ?? "")
                              && seen.Add(profile.Id!)
                              && !string.IsNullOrWhiteSpace(profile.Name)
                              && IsValidAccount(profile.Account))
            .Take(MaximumProfiles)
            .ToList();
        // ReSharper restore NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract, ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        return new CopilotConfiguration(
            IsValidAccount(saved.DefaultAccount) ? saved.DefaultAccount : null,
            profiles,
            s_cacheMinutes.Contains(saved.CacheMinutes) ? saved.CacheMinutes : 10);
    }

    private static bool IsValidAccount(CopilotAccount? account) =>
        account is { Host: { } host, Login.Length: > 0 and <= 100 }
        && Uri.TryCreate(host, UriKind.Absolute, out var uri)
        && uri is { Scheme: "https", UserInfo.Length: 0, AbsolutePath: "/" };

    // The single commit point: a failed write leaves the published configuration untouched.
    private void Commit(CopilotConfiguration configuration)
    {
        lock (_stateLock)
        {
            _host?.SetSetting(ConfigurationSettingName, configuration);
            _configuration = configuration;
            PublishRoles();
        }

        _transport?.SetCatalogLifetime(TimeSpan.FromMinutes(configuration.CacheMinutes));
        _host?.NotifyCapabilitiesChanged();
    }

    private void Log(PluginLogLevel level, string message) => _host?.Log(level, message);
}

internal sealed class CopilotProviderRole(GitHubCopilotPlugin owner, string? profileId, string selectionId)
    : ILlmProviderRole, ILlmProviderSelectionIdentity
{
    public string PluginId => owner.PluginId;
    public string ProviderName => owner.ProviderNameFor(profileId);
    public bool IsAvailable => owner.IsAvailableFor(profileId);
    public IReadOnlyList<PluginModelInfo> SupportedModels => owner.ModelsFor(profileId);
    public string LlmSelectionId => selectionId;

    public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct) =>
        owner.ProcessForAsync(profileId, systemPrompt, userText, model, ct);
}
