using System.Globalization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Plugins.Shared.Net;

namespace TypeWhisper.Plugin.GitHubCopilot;

public sealed partial class GitHubCopilotPlugin
{
    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions()
    {
        return
        [
            new PluginSettingDefinition(
                AccountSettingKey,
                Loc.L("Settings.Account"),
                Description: DescribeStatus(),
                Options: [new PluginSettingOption("", Loc.L("Settings.NoAccount")), .. AccountOptions()],
                Kind: PluginSettingKind.Dropdown
            ),
            new PluginSettingDefinition(
                CacheSettingKey,
                Loc.L("Settings.Cache"),
                Description: Loc.L("Settings.CacheDescription"),
                Options: s_cacheMinutes
                    .Select(minutes => new PluginSettingOption(
                        minutes.ToString(CultureInfo.InvariantCulture),
                        minutes switch
                        {
                            < 60 => Loc.L("Settings.Minutes", minutes),
                            60 => Loc.L("Settings.OneHour"),
                            _ => Loc.L("Settings.Hours", minutes / 60),
                        }))
                    .ToList(),
                Kind: PluginSettingKind.Dropdown
            ),
        ];
    }

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            return Task.FromResult(key switch
            {
                AccountSettingKey => _configuration.DefaultAccount?.Key ?? "",
                CacheSettingKey => _configuration.CacheMinutes.ToString(CultureInfo.InvariantCulture),
                _ => null,
            });
        }
    }

    public Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        CopilotConfiguration next;
        lock (_stateLock)
        {
            switch (key)
            {
                case AccountSettingKey:
                    var account = string.IsNullOrEmpty(value)
                        ? null
                        : FindKnownAccount(value) ?? throw new ArgumentException(Loc.L("Error.UnknownAccount"));
                    if (account?.Key == _configuration.DefaultAccount?.Key)
                    {
                        return Task.CompletedTask;
                    }

                    next = _configuration with { DefaultAccount = account };
                    break;
                case CacheSettingKey:
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
                        || !s_cacheMinutes.Contains(minutes))
                    {
                        throw new ArgumentException(Loc.L("Error.InvalidCache"));
                    }

                    if (minutes == _configuration.CacheMinutes)
                    {
                        return Task.CompletedTask;
                    }

                    next = _configuration with { CacheMinutes = minutes };
                    break;
                default:
                    return Task.CompletedTask;
            }
        }

        Commit(next);
        RefreshIfCatalogMissing();
        return Task.CompletedTask;
    }

    public IReadOnlyList<PluginCollectionDefinition> GetCollectionDefinitions()
    {
        return
        [
            new PluginCollectionDefinition(
                ProfilesCollectionKey,
                Loc.L("Settings.Profiles"),
                Loc.L("Settings.ProfilesDescription"),
                [
                    new PluginSettingDefinition(
                        ProfileNameField,
                        Loc.L("Settings.ProfileName"),
                        Placeholder: Loc.L("Settings.ProfileNamePlaceholder"),
                        Kind: PluginSettingKind.Text
                    ),
                    new PluginSettingDefinition(
                        ProfileAccountField,
                        Loc.L("Settings.Account"),
                        Options: AccountOptions(),
                        Kind: PluginSettingKind.Dropdown
                    ),
                    new PluginSettingDefinition(ProfileIdField, ProfileIdField, Kind: PluginSettingKind.Text),
                ],
                ProfileNameField,
                Loc.L("Settings.AddProfile")
            ),
        ];
    }

    public Task<IReadOnlyList<PluginCollectionItem>> GetItemsAsync(string collectionKey, CancellationToken ct = default)
    {
        if (!string.Equals(collectionKey, ProfilesCollectionKey, StringComparison.Ordinal))
        {
            return Task.FromResult<IReadOnlyList<PluginCollectionItem>>([]);
        }

        lock (_stateLock)
        {
            IReadOnlyList<PluginCollectionItem> items = _configuration.Profiles
                .Select(profile => new PluginCollectionItem(new Dictionary<string, string?>
                {
                    [ProfileIdField] = profile.Id,
                    [ProfileNameField] = profile.Name,
                    [ProfileAccountField] = profile.Account.Key,
                }))
                .ToList();
            return Task.FromResult(items);
        }
    }

    public Task<PluginSettingsValidationResult> SetItemsAsync(
        string collectionKey,
        IReadOnlyList<PluginCollectionItem> items,
        CancellationToken ct = default
    )
    {
        ct.ThrowIfCancellationRequested();
        if (!string.Equals(collectionKey, ProfilesCollectionKey, StringComparison.Ordinal))
        {
            return Task.FromResult(new PluginSettingsValidationResult(false, Loc.L("Error.UnknownCollection")));
        }

        if (items.Count > MaximumProfiles)
        {
            return Task.FromResult(new PluginSettingsValidationResult(
                false,
                Loc.L("Error.TooManyProfiles", MaximumProfiles)));
        }

        CopilotConfiguration next;
        lock (_stateLock)
        {
            var profiles = new List<CopilotProfile>(items.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                var name = (item.Values.GetValueOrDefault(ProfileNameField) ?? "").Trim();
                if (name.Length is 0 or > MaximumNameLength)
                {
                    return Task.FromResult(new PluginSettingsValidationResult(
                        false,
                        Loc.L("Error.ProfileName", MaximumNameLength)));
                }

                var accountKey = item.Values.GetValueOrDefault(ProfileAccountField) ?? "";
                if (FindKnownAccount(accountKey) is not { } account)
                {
                    return Task.FromResult(new PluginSettingsValidationResult(
                        false,
                        Loc.L("Error.ProfileAccount", name)));
                }

                var id = item.Values.GetValueOrDefault(ProfileIdField);
                if (id is null || !ProfileIdPattern().IsMatch(id) || !seen.Add(id))
                {
                    id = Guid.NewGuid().ToString("N");
                    seen.Add(id);
                }

                profiles.Add(new CopilotProfile(id, name, account));
            }

            next = _configuration with { Profiles = profiles };
        }

        Commit(next);
        RefreshIfCatalogMissing();
        return Task.FromResult(new PluginSettingsValidationResult(true, Loc.L("Settings.ProfilesSaved")));
    }

    public IReadOnlyList<PluginSettingsAction> GetSettingsActions()
    {
        var installed = IsRuntimeInstalled;
        var supported = _installer?.Asset is not null;
        return
        [
            new PluginSettingsAction(
                DownloadActionId,
                Loc.L("Action.Download"),
                supported ? Loc.L("Action.DownloadDescription") : Loc.L("Error.UnsupportedPlatform"),
                IsEnabled: supported && !installed),
            new PluginSettingsAction(
                RefreshActionId,
                Loc.L("Action.Refresh"),
                Loc.L("Action.RefreshDescription"),
                IsEnabled: installed),
            new PluginSettingsAction(
                RemoveActionId,
                Loc.L("Action.Remove"),
                Loc.L("Action.RemoveDescription"),
                IsEnabled: installed,
                ConfirmationMessage: Loc.L("Action.RemoveConfirm")),
        ];
    }

    public async Task<PluginSettingsValidationResult> ExecuteSettingsActionAsync(string actionId, CancellationToken ct)
    {
        if (_installer is not { } installer || _lifetime is null)
        {
            return new PluginSettingsValidationResult(false, Loc.L("Error.Inactive"));
        }

        try
        {
            switch (actionId)
            {
                case DownloadActionId:
                    ReportActivity(Loc.L("Activity.Downloading"), 0);
                    await installer
                        .InstallAsync(new InlineProgress(value => ReportActivity(Loc.L("Activity.Downloading"), value)), ct)
                        .ConfigureAwait(false);
                    ReportActivity(Loc.L("Activity.Refreshing"), null);
                    await RefreshWithDeadlineAsync(ct).ConfigureAwait(false);
                    return new PluginSettingsValidationResult(true, DescribeStatus());
                case RefreshActionId:
                    ReportActivity(Loc.L("Activity.Refreshing"), null);
                    await RefreshWithDeadlineAsync(ct).ConfigureAwait(false);
                    return new PluginSettingsValidationResult(true, DescribeStatus());
                case RemoveActionId:
                    await _gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await installer.RemoveAsync(ct).ConfigureAwait(false);
                        _transport?.RemoveSessionState();
                    }
                    finally
                    {
                        _gate.Release();
                    }

                    _host?.NotifyCapabilitiesChanged();
                    return new PluginSettingsValidationResult(true, Loc.L("Action.Removed"));
                default:
                    return new PluginSettingsValidationResult(false, Loc.L("Error.UnknownAction"));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InsufficientDownloadSpaceException ex)
        {
            return new PluginSettingsValidationResult(false, ex.Message);
        }
        // SDK and download errors may carry URLs or tokens; only the type is logged.
        catch (Exception ex)
        {
            Log(PluginLogLevel.Warning, $"event=action-failed action={actionId} type={ex.GetType().Name}");
            return new PluginSettingsValidationResult(false, ex switch
            {
                InvalidDataException => Loc.L("Error.RuntimeCorrupt"),
                _ when actionId == DownloadActionId && !IsRuntimeInstalled =>Loc.L("Error.DownloadFailed"),
                TimeoutException => Loc.L("Error.SettingsTimeout"),
                _ => Loc.L("Error.RefreshFailed"),
            });
        }
        finally
        {
            CompleteActivity();
        }
    }

    private async Task RefreshWithDeadlineAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(s_settingsTimeout);
        try
        {
            await RefreshAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private void RefreshIfCatalogMissing()
    {
        bool missing;
        lock (_stateLock)
        {
            missing = BoundAccounts().Any(account => !_catalogs.ContainsKey(account.Key));
        }

        // A model list takes a runtime start; never inside the host's short save boundary.
        if (missing && IsRuntimeInstalled)
        {
            StartBackgroundRefresh();
        }
    }

    private string DescribeStatus()
    {
        if (_installer?.Asset is null)
        {
            return Loc.L("Error.UnsupportedPlatform");
        }

        if (!IsRuntimeInstalled)
        {
            return Loc.L("Status.RuntimeMissing");
        }

        lock (_stateLock)
        {
            var lines = new List<string>
            {
                _accounts.Count == 0
                    ? Loc.L("Status.NoAccounts")
                    : Loc.L("Status.Accounts", string.Join(", ", _accounts.Select(account => account.DisplayName))),
            };
            lines.AddRange(BoundAccounts()
                .Where(account => _signedOutAccounts.Contains(account.Key))
                .Select(account => Loc.L("Status.SignedOut", account.DisplayName)));
            return string.Join("\n", lines);
        }
    }

    private List<PluginSettingOption> AccountOptions()
    {
        lock (_stateLock)
        {
            return _accounts
                .Concat(BoundAccounts())
                .DistinctBy(account => account.Key)
                .Select(account => new PluginSettingOption(
                    account.Key,
                    _signedOutAccounts.Contains(account.Key)
                        ? Loc.L("Settings.AccountSignedOut", account.DisplayName)
                        : account.DisplayName))
                .ToList();
        }
    }

    // Must be called under _stateLock. Only signed-in or already bound accounts can be chosen.
    private CopilotAccount? FindKnownAccount(string key) =>
        _accounts.Concat(BoundAccounts()).FirstOrDefault(account => account.Key == key);

    private void ReportActivity(string message, double? progress)
    {
        SettingsProgress = progress;
        SettingsActivityChanged?.Invoke(message);
    }

    private void CompleteActivity()
    {
        SettingsProgress = null;
        SettingsActivityChanged?.Invoke(null);
    }
}

// Reports on the downloading thread, so no progress update can arrive after the activity ends.
internal sealed class InlineProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}
