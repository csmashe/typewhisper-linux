using System.Text.RegularExpressions;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.AuthenticatedCli;

/// <summary>A saved CLI profile: another signed-in session of one of the supported CLIs.</summary>
internal sealed record CliProfileSettings(
    string Id,
    string Name,
    string Provider,
    string? Executable,
    Dictionary<string, string>? Environment,
    OpenCodeModelCatalogCache? OpenCodeCatalog = null
);

/// <summary>The OpenCode free-model catalog of one built-in provider or profile.</summary>
internal sealed class OpenCodeCatalogState
{
    internal List<OpenCodeCatalogModel> FreeModels { get; set; } = [];
    internal DateTimeOffset? RefreshedAt { get; set; }
    internal string? LastRefreshError { get; set; }
    internal bool IsLastKnownGood { get; set; }
    internal int Revision { get; set; }
}

// The built-in Codex, Claude Code and OpenCode providers keep their settings and selection IDs.
// Profiles add further sessions of the same CLIs, each exposed as its own LLM provider whose
// selection ID is derived from the profile's stable ID, so renaming one keeps workflows working.
public sealed partial class AuthenticatedCliPlugin
{
    internal const string ProfilesSettingName = "cliProfiles.v1";
    internal const string ProfilesCollectionKey = "profiles";
    internal const int MaximumProfiles = 32;
    private const int MaximumProfileNameLength = 100;

    private const string ProfileIdField = "__id";
    private const string ProfileNameField = "name";
    private const string ProfileProviderField = "provider";
    private const string ProfileExecutableField = "executable";
    private const string ProfileEnvironmentField = "environment";

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileIdRegex();

    private Dictionary<string, CliProviderDescriptor> _descriptors;
    private List<CliProviderDescriptor> _descriptorOrder = [.. CliProviderDescriptor.All];
    private List<CliProfileSettings> _profiles = [];
    private readonly List<AuthenticatedCliProviderRole> _builtInRoles;
    private readonly Dictionary<string, AuthenticatedCliProviderRole> _profileRoles = new(StringComparer.Ordinal);
    private IReadOnlyList<ILlmProviderRole> _additionalLlmProviders;
    private Task? _profileRefreshTask;

    /// <summary>The built-in providers followed by the profiles, in the order the user arranged them.</summary>
    private List<CliProviderDescriptor> CurrentDescriptors()
    {
        lock (_stateLock)
        {
            return _descriptorOrder.ToList();
        }
    }

    internal CliProviderDescriptor? FindDescriptor(string key)
    {
        lock (_stateLock)
        {
            return _descriptors.GetValueOrDefault(key);
        }
    }

    // A rename keeps the connection, so requests and probes that started under the old name stay
    // valid; another CLI, installation or session directory does not.
    private bool IsCurrent(CliProviderDescriptor descriptor)
    {
        lock (_stateLock)
        {
            return _descriptors.TryGetValue(descriptor.Key, out var current)
                   && (ReferenceEquals(current, descriptor) || HaveSameConnection(current, descriptor));
        }
    }

    private void EnsureCurrent(CliProviderDescriptor descriptor)
    {
        if (IsCurrent(descriptor))
        {
            return;
        }

        throw new PluginRequestException(
            FindDescriptor(descriptor.Key) is null
                ? Loc.L("Error.ProfileRemoved")
                : Loc.L("Error.ProfileChanged", descriptor.MessageName),
            PluginRequestFailureKind.Configuration,
            isTransient: false
        );
    }

    private static bool HaveSameConnection(CliProviderDescriptor first, CliProviderDescriptor second) =>
        first.Kind == second.Kind
        && string.Equals(first.ProfileExecutable, second.ProfileExecutable, StringComparison.Ordinal)
        && first.ProfileEnvironment.Count == second.ProfileEnvironment.Count
        && first.ProfileEnvironment.All(pair =>
            second.ProfileEnvironment.TryGetValue(pair.Key, out var value)
            && string.Equals(pair.Value, value, StringComparison.Ordinal));

    private string GetDisplayName(CliProviderDescriptor descriptor) =>
        descriptor.ProfileName ?? GetString(descriptor.DisplayKey);

    private void RestoreProfiles(IPluginHostServices host)
    {
        List<CliProfileSettings>? stored;
        try
        {
            stored = host.GetSetting<List<CliProfileSettings>>(ProfilesSettingName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            host.Log(PluginLogLevel.Warning, $"event=profiles-restore-failed type={ex.GetType().Name}");
            stored = null;
        }

        var restored = new List<CliProfileSettings>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        // Deserialized settings JSON: a damaged list can hold null entries.
        foreach (var profile in (stored ?? []).Where(profile => profile is not null))
        {
            // Session directories may be gone for now; the profile then reports that state.
            if (restored.Count >= MaximumProfiles
                // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
                // Same deserialized JSON: a damaged entry can lack its ID.
                || !seen.Add(profile.Id ?? "")
                || ValidateProfile(profile, requireExistingDirectories: false) is not null)
            {
                host.Log(PluginLogLevel.Warning, "event=profile-restore-skipped");
                continue;
            }

            restored.Add(profile with
            {
                Environment = new Dictionary<string, string>(profile.Environment ?? [], StringComparer.Ordinal),
            });
        }

        PublishProfiles(restored);
        foreach (var profile in restored.Where(profile => profile.OpenCodeCatalog is not null))
        {
            if (FindDescriptor(CliProviderDescriptor.ProfileSelectionIdPrefix + profile.Id) is
                { Kind: CliProviderKind.OpenCode } descriptor)
            {
                RestoreOpenCodeCatalog(descriptor, profile.OpenCodeCatalog);
            }
        }
    }

    /// <summary>Returns a localized reason the profile cannot be saved, or null when it is valid.</summary>
    /// <param name="profile">The profile to check.</param>
    /// <param name="requireExistingDirectories">Whether session directories must exist now.</param>
    /// <param name="unchangedEnvironment">
    ///     Session directories already saved for this profile; they may be offline for now and
    ///     must not block saving the rest of the list.
    /// </param>
    private string? ValidateProfile(
        CliProfileSettings profile,
        bool requireExistingDirectories,
        Dictionary<string, string>? unchangedEnvironment = null
    )
    {
        // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        // Restored profiles are deserialized settings JSON, so any field can arrive null.
        var label = string.IsNullOrWhiteSpace(profile.Name) ? "?" : profile.Name.Trim();
        if (profile.Id is null || !ProfileIdRegex().IsMatch(profile.Id))
        {
            return Loc.L("Error.ProfileInvalid", label);
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            return Loc.L("Error.ProfileNameRequired");
        }

        if (profile.Name.Length > MaximumProfileNameLength)
        {
            return Loc.L("Error.ProfileNameTooLong", label, MaximumProfileNameLength);
        }

        var descriptor = CliProviderDescriptor.All.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, profile.Provider, StringComparison.Ordinal));
        if (descriptor is null)
        {
            return Loc.L("Error.ProfileProvider", label);
        }

        if (profile.Executable is not null
            && (!Path.IsPathFullyQualified(profile.Executable)
                || !string.Equals(
                    Path.GetFileName(profile.Executable),
                    descriptor.ExecutableName,
                    StringComparison.Ordinal)))
        {
            return Loc.L("Error.ProfileExecutable", label);
        }

        foreach (var (name, value) in profile.Environment ?? [])
        {
            if (!descriptor.ProviderEnvironmentVariables.Contains(name, StringComparer.Ordinal))
            {
                return Loc.L(
                    "Error.ProfileEnvironmentVariable",
                    label,
                    name,
                    string.Join(", ", descriptor.ProviderEnvironmentVariables)
                );
            }

            if (value is null
                || value.Contains('\0')
                || !Path.IsPathFullyQualified(value)
                || requireExistingDirectories
                && !(unchangedEnvironment is not null
                     && unchangedEnvironment.TryGetValue(name, out var saved)
                     && string.Equals(saved, value, StringComparison.Ordinal))
                && !CliPathSafety.IsSafeLocalDirectory(value))
            {
                return Loc.L("Error.ProfileEnvironmentDirectory", label, name);
            }
        }
        // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract

        return null;
    }

    // Callers notify the host afterwards, outside the lock.
    private void PublishProfiles(List<CliProfileSettings> profiles)
    {
        lock (_stateLock)
        {
            var previous = _descriptors;
            var descriptors = CliProviderDescriptor.All.ToDictionary(
                descriptor => descriptor.Key,
                StringComparer.Ordinal
            );
            var order = CliProviderDescriptor.All.ToList();
            var roles = new List<ILlmProviderRole>(_builtInRoles);
            foreach (var profile in profiles)
            {
                var descriptor = CliProviderDescriptor.All
                    .Single(candidate => candidate.Key == profile.Provider)
                    .ForProfile(profile.Id, profile.Name, profile.Executable, profile.Environment ?? []);
                descriptors[descriptor.Key] = descriptor;
                order.Add(descriptor);
                if (!previous.TryGetValue(descriptor.Key, out var old) || !HaveSameConnection(old, descriptor))
                {
                    _snapshots[descriptor.Key] = CliAvailabilitySnapshot.Initial;
                    _openCodeCatalogs.Remove(descriptor.Key);
                }

                if (!_profileRoles.TryGetValue(profile.Id, out var role))
                {
                    role = new AuthenticatedCliProviderRole(this, descriptor.Key, descriptor.SelectionId);
                    _profileRoles[profile.Id] = role;
                }

                roles.Add(role);
            }

            foreach (var removed in previous.Keys.Where(key => !descriptors.ContainsKey(key)).ToList())
            {
                _snapshots.Remove(removed);
                _openCodeCatalogs.Remove(removed);
            }

            foreach (var removed in _profileRoles.Keys
                         .Where(id => profiles.All(profile => profile.Id != id))
                         .ToList())
            {
                _profileRoles.Remove(removed);
            }

            _descriptors = descriptors;
            _descriptorOrder = order;
            _profiles = profiles;
            _additionalLlmProviders = roles;
        }
    }

    // Holds the state lock across the write, so a probe's catalog cannot overwrite a profile
    // list saved meanwhile or attach itself to a connection that has since changed.
    private void PersistProfileCatalog(
        IPluginHostServices host,
        CliProviderDescriptor descriptor,
        OpenCodeModelCatalogCache cache
    )
    {
        lock (_stateLock)
        {
            if (!IsCurrent(descriptor))
            {
                return;
            }

            var profiles = _profiles
                .Select(profile => profile.Id == descriptor.ProfileId
                    ? profile with { OpenCodeCatalog = cache }
                    : profile)
                .ToList();
            host.SetSetting(ProfilesSettingName, profiles);
            _profiles = profiles;
        }
    }

    public IReadOnlyList<PluginCollectionDefinition> GetCollectionDefinitions()
    {
        var executableOptions = new List<PluginSettingOption>
        {
            new("", GetString("Settings.AutomaticInstallation")),
        };
        executableOptions.AddRange(CliProviderDescriptor.All
            .SelectMany(descriptor => _discovery.FindCandidates(descriptor.ExecutableName))
            .Select(candidate => new PluginSettingOption(candidate, candidate)));

        return
        [
            new PluginCollectionDefinition(
                ProfilesCollectionKey,
                GetString("Settings.ProfilesLabel"),
                DescribeProfiles(),
                [
                    new PluginSettingDefinition(
                        ProfileNameField,
                        GetString("Settings.ProfileName"),
                        Placeholder: GetString("Settings.ProfileNamePlaceholder"),
                        Kind: PluginSettingKind.Text
                    ),
                    new PluginSettingDefinition(
                        ProfileProviderField,
                        GetString("Settings.ProfileProvider"),
                        Options: CliProviderDescriptor.All
                            .Select(descriptor => new PluginSettingOption(
                                descriptor.Key,
                                GetString(descriptor.DisplayKey)))
                            .ToList(),
                        Kind: PluginSettingKind.Dropdown
                    ),
                    new PluginSettingDefinition(
                        ProfileExecutableField,
                        GetString("Settings.ProfileExecutable"),
                        Description: GetString("Settings.ProfileExecutableDescription"),
                        Options: executableOptions,
                        Kind: PluginSettingKind.Dropdown
                    ),
                    new PluginSettingDefinition(
                        ProfileEnvironmentField,
                        GetString("Settings.ProfileEnvironment"),
                        Placeholder: "CODEX_HOME=/home/me/.codex-work",
                        Description: GetString("Settings.ProfileEnvironmentDescription"),
                        Kind: PluginSettingKind.Multiline
                    ),
                    new PluginSettingDefinition(ProfileIdField, ProfileIdField, Kind: PluginSettingKind.Text),
                ],
                ProfileNameField,
                GetString("Settings.AddProfile")
            ),
        ];
    }

    private string DescribeProfiles()
    {
        return string.Join(
            "\n",
            [
                GetString("Settings.ProfilesDescription"),
                .. CurrentDescriptors()
                    .Where(descriptor => descriptor.IsProfile)
                    .Select(descriptor => GetString(
                        "Settings.ProfileStatus",
                        GetDisplayName(descriptor),
                        DescribeState(GetSnapshot(descriptor)))),
            ]
        );
    }

    public Task<IReadOnlyList<PluginCollectionItem>> GetItemsAsync(
        string collectionKey,
        CancellationToken ct = default
    )
    {
        if (!string.Equals(collectionKey, ProfilesCollectionKey, StringComparison.Ordinal))
        {
            return Task.FromResult<IReadOnlyList<PluginCollectionItem>>([]);
        }

        lock (_stateLock)
        {
            IReadOnlyList<PluginCollectionItem> items = _profiles
                .Select(profile => new PluginCollectionItem(new Dictionary<string, string?>
                {
                    [ProfileIdField] = profile.Id,
                    [ProfileNameField] = profile.Name,
                    [ProfileProviderField] = profile.Provider,
                    [ProfileExecutableField] = profile.Executable ?? "",
                    [ProfileEnvironmentField] = string.Join(
                        "\n",
                        (profile.Environment ?? []).Select(pair => pair.Key + "=" + pair.Value)),
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
            return Task.FromResult(new PluginSettingsValidationResult(false, GetString("Error.UnknownCollection")));
        }

        if (items.Count > MaximumProfiles)
        {
            return Task.FromResult(new PluginSettingsValidationResult(
                false,
                GetString("Error.TooManyProfiles", MaximumProfiles)));
        }

        Dictionary<string, CliProfileSettings> previous;
        lock (_stateLock)
        {
            previous = _profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        }

        var profiles = new List<CliProfileSettings>(items.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var parsed = ParseProfile(item, previous, seen);
            if (parsed.Error is not null)
            {
                return Task.FromResult(new PluginSettingsValidationResult(false, parsed.Error));
            }

            profiles.Add(parsed.Profile!);
        }

        List<CliProviderDescriptor> changed;
        lock (_stateLock)
        {
            ct.ThrowIfCancellationRequested();
            // Read under the commit lock: a probe may have stored a newer catalog during parsing.
            var latest = _profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
            profiles = profiles
                .Select(profile => latest.TryGetValue(profile.Id, out var saved) && HaveSameConnection(saved, profile)
                    ? profile with { OpenCodeCatalog = saved.OpenCodeCatalog }
                    : profile)
                .ToList();
            // The single commit point: a failed write leaves the published profiles untouched.
            _host?.SetSetting(ProfilesSettingName, profiles);
            var before = _descriptors;
            PublishProfiles(profiles);
            changed = _descriptorOrder
                .Where(descriptor => descriptor.IsProfile
                                     && (!before.TryGetValue(descriptor.Key, out var old)
                                         || !HaveSameConnection(old, descriptor)))
                .ToList();
        }

        _host?.NotifyCapabilitiesChanged();
        // Probing can take seconds per profile, longer than the host allows a save to run.
        StartProfileRefresh(changed);
        return Task.FromResult(new PluginSettingsValidationResult(
            true,
            GetString("Settings.ProfilesSaved", profiles.Count)));
    }

    private (CliProfileSettings? Profile, string? Error) ParseProfile(
        PluginCollectionItem item,
        IReadOnlyDictionary<string, CliProfileSettings> previous,
        HashSet<string> seen
    )
    {
        var id = Value(ProfileIdField);
        if (!ProfileIdRegex().IsMatch(id) || !seen.Add(id))
        {
            id = Guid.NewGuid().ToString("D");
            seen.Add(id);
        }

        var name = Value(ProfileNameField);
        var label = name.Length == 0 ? "?" : name;
        var provider = Value(ProfileProviderField);
        var descriptor = CliProviderDescriptor.All.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, provider, StringComparison.Ordinal));
        if (descriptor is null)
        {
            return (null, Loc.L("Error.ProfileProvider", label));
        }

        var executable = NullIfBlank(Value(ProfileExecutableField));
        var old = previous.GetValueOrDefault(id);
        // An installation that has since left PATH can stay saved; it then reports itself missing.
        if (executable is not null
            && !(old is not null
                 && old.Provider == provider
                 && string.Equals(old.Executable, executable, StringComparison.Ordinal))
            && !_discovery.FindCandidates(descriptor.ExecutableName).Any(candidate =>
                string.Equals(candidate, executable, StringComparison.Ordinal)
                || CliExecutableDiscovery.IsUsableAlias(executable, candidate, descriptor.ExecutableName)))
        {
            return (null, Loc.L("Error.ProfileExecutable", label));
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        // ReSharper disable once LoopCanBeConvertedToQuery -- stops at the first malformed line to report it.
        foreach (var line in Value(ProfileEnvironmentField)
                     .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator < 1 || !environment.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim()))
            {
                return (null, Loc.L("Error.ProfileEnvironmentFormat", label));
            }
        }

        var profile = new CliProfileSettings(id, name, provider, executable, environment);
        var unchangedEnvironment = old is not null && old.Provider == provider ? old.Environment : null;
        if (ValidateProfile(profile, requireExistingDirectories: true, unchangedEnvironment) is { } error)
        {
            return (null, error);
        }

        return (profile, null);

        string Value(string key) => item.Values.GetValueOrDefault(key)?.Trim() ?? "";
    }

    // A catalog belongs to the session it was loaded from.
    private static bool HaveSameConnection(CliProfileSettings first, CliProfileSettings second) =>
        first.Provider == second.Provider
        && string.Equals(first.Executable, second.Executable, StringComparison.Ordinal)
        && (first.Environment ?? []).Count == (second.Environment ?? []).Count
        && (first.Environment ?? []).All(pair =>
            (second.Environment ?? []).TryGetValue(pair.Key, out var value) && value == pair.Value);

    private void StartProfileRefresh(List<CliProviderDescriptor> descriptors)
    {
        if (descriptors.Count == 0)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_lifetimeCancellation is not { } lifetime)
            {
                return;
            }

            var previous = _profileRefreshTask ?? Task.CompletedTask;
            var keys = descriptors.Select(descriptor => descriptor.Key).ToList();
            var token = lifetime.Token;
            // Run off this thread: a probe that finishes synchronously would otherwise notify
            // the host while the state lock is held, inverting the host's lock order.
            _profileRefreshTask = Task.Run(() => RefreshProfilesAsync(previous, keys, token), CancellationToken.None);
        }
    }

    internal Task WaitForProfileRefreshAsync()
    {
        lock (_stateLock)
        {
            return _profileRefreshTask ?? Task.CompletedTask;
        }
    }

    // Chained after the previous refresh so deactivation drains one task.
    private async Task RefreshProfilesAsync(
        Task previous,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken
    )
    {
        await previous.ConfigureAwait(false);
        foreach (var key in keys)
        {
            // A profile edited again since the save is probed by the later save's refresh.
            if (FindDescriptor(key) is not { } descriptor)
            {
                continue;
            }

            try
            {
                await RefreshOneAsync(descriptor, notifyHost: true, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _host?.Log(
                    PluginLogLevel.Warning,
                    $"provider={key} event=profile-refresh-failed type={ex.GetType().Name}"
                );
            }
        }
    }
}
