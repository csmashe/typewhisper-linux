using System.Reflection;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Covers plugin settings actions in <see cref="PluginsSectionViewModel" />: loading the
///     buttons, confirmation, saving pending settings first, running without the validation
///     time limit, cancellation, activity progress and rows rebuilt while an action runs.
/// </summary>
public sealed class PluginSettingsActionsViewModelTests : IDisposable
{
    private const string TestPluginId = "com.test.actions";
    private static readonly TimeSpan s_hangGuard = TimeSpan.FromSeconds(5);

    private readonly string _tempDir = Path.Join(Path.GetTempPath(), "tw-vm-actions-" + Guid.NewGuid().ToString("N"));

    public PluginSettingsActionsViewModelTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Fact]
    public async Task Expand_LoadsActionsWithTheirState()
    {
        var plugin = new FakeActionsPlugin();
        var (vm, row) = await CreateExpandedAsync(plugin);

        Assert.True(row.HasActions);
        Assert.Equal(["download", "remove"], row.Actions.Select(action => action.Id));
        Assert.Equal("Download files", row.Actions[0].Label);
        Assert.True(row.Actions[0].HasDescription);
        Assert.True(row.Actions[0].CanRun);
        Assert.False(row.Actions[1].CanRun);
        Assert.False(row.IsActionRunning);
        Assert.Same(vm, row.Owner);
    }

    [Fact]
    public async Task RunAction_SavesPendingSettingsFirstThenShowsResultAndReloadsActions()
    {
        var plugin = new FakeActionsPlugin();
        var (vm, row) = await CreateExpandedAsync(plugin);
        row.SettingFields.Single().Value = "accepted";
        var reads = plugin.ActionReads;

        await vm.RunSettingsActionCommand.ExecuteAsync(row.Actions[0]);

        Assert.Equal(["set value=accepted", "run download"], plugin.Calls);
        var current = CurrentRow(vm);
        Assert.Equal("download done", current.Status);
        Assert.True(plugin.ActionReads > reads);
        Assert.False(current.IsActionRunning);
    }

    [Fact]
    public async Task ConfirmableAction_RunsOnlyAfterConfirmation()
    {
        var plugin = new FakeActionsPlugin { RemoveEnabled = true };
        var (vm, row) = await CreateExpandedAsync(plugin);
        var remove = row.Actions[1];

        await vm.RunSettingsActionCommand.ExecuteAsync(remove);
        Assert.Same(remove, row.PendingAction);
        Assert.True(row.HasPendingAction);
        Assert.DoesNotContain("run remove", plugin.Calls);

        vm.DismissSettingsActionConfirmationCommand.Execute(row);
        Assert.False(row.HasPendingAction);

        // A double click on the original button only asks again.
        await vm.RunSettingsActionCommand.ExecuteAsync(remove);
        await vm.RunSettingsActionCommand.ExecuteAsync(remove);
        Assert.DoesNotContain("run remove", plugin.Calls);
        Assert.Same(remove, row.PendingAction);

        await vm.ConfirmSettingsActionCommand.ExecuteAsync(row);

        Assert.Contains("run remove", plugin.Calls);
        Assert.Equal("remove done", CurrentRow(vm).Status);
    }

    [Fact]
    public async Task RunningAction_DisablesActionsShowsActivityAndCanBeCancelled()
    {
        var plugin = new FakeActionsPlugin { HoldUntilCancelled = true };
        var (vm, row) = await CreateExpandedAsync(plugin);

        var run = vm.RunSettingsActionCommand.ExecuteAsync(row.Actions[0]);
        await plugin.Started.Task.WaitAsync(s_hangGuard);

        Assert.True(row.IsActionRunning);
        Assert.All(row.Actions, action => Assert.False(action.CanRun));
        Assert.Equal(Loc.Instance["Plugins.ActionRunning"], row.ActivityMessage);
        Assert.True(row.IsActivityIndeterminate);

        plugin.ReportActivity("Downloading", 0.5);
        Assert.Equal("Downloading", row.ActivityMessage);
        Assert.Equal(50, row.ActivityPercent);
        Assert.False(row.IsActivityIndeterminate);

        vm.CancelSettingsActionCommand.Execute(row);
        await run.WaitAsync(s_hangGuard);

        var current = CurrentRow(vm);
        Assert.Equal(Loc.Instance["Plugins.ActionCancelled"], current.Status);
        Assert.False(current.IsActionRunning);
        Assert.Null(current.ActivityMessage);
        Assert.True(current.Actions[0].CanRun);
    }

    [Fact]
    public async Task SecondClickWhileSettingsSave_DoesNotStartAnotherRun()
    {
        var saveGate = new TaskCompletionSource();
        var plugin = new FakeActionsPlugin { SaveGate = saveGate.Task };
        var (vm, row) = await CreateExpandedAsync(plugin);

        var first = vm.RunSettingsActionCommand.ExecuteAsync(row.Actions[0]);
        var second = vm.RunSettingsActionCommand.ExecuteAsync(row.Actions[0]);
        Assert.True(row.IsActionRunning);
        saveGate.SetResult();
        await Task.WhenAll(first, second).WaitAsync(s_hangGuard);

        Assert.Single(plugin.Calls, call => call == "run download");
        Assert.False(CurrentRow(vm).IsActionRunning);
    }

    [Fact]
    public async Task EditsMadeWhileAnActionRuns_AreKept()
    {
        var plugin = new FakeActionsPlugin { HoldUntilCancelled = true };
        var (vm, row) = await CreateExpandedAsync(plugin);
        var run = vm.RunSettingsActionCommand.ExecuteAsync(row.Actions[0]);
        await plugin.Started.Task.WaitAsync(s_hangGuard);
        var reads = plugin.ActionReads;

        row.SettingFields.Single().Value = "edited during download";
        vm.CancelSettingsActionCommand.Execute(row);
        await run.WaitAsync(s_hangGuard);

        var current = CurrentRow(vm);
        Assert.Equal("edited during download", current.SettingFields.Single().Value);
        Assert.Equal(Loc.Instance["Plugins.ActionCancelled"], current.Status);
        Assert.True(plugin.ActionReads > reads);
        Assert.False(current.IsActionRunning);
    }

    [Fact]
    public async Task ValuesSavedByTheAction_AreReloadedFromThePlugin()
    {
        var plugin = new FakeActionsPlugin { NormalizeValue = value => value.Trim() };
        var (vm, row) = await CreateExpandedAsync(plugin);
        row.SettingFields.Single().Value = "  padded  ";

        await vm.RunSettingsActionCommand.ExecuteAsync(row.Actions[0]);

        var current = CurrentRow(vm);
        Assert.Equal("padded", current.SettingFields.Single().Value);
        Assert.False(current.HasUnsavedSettings);
    }

    [Fact]
    public async Task RunAction_IsNotBoundByTheValidationTimeout()
    {
        var plugin = new FakeActionsPlugin { Delay = TimeSpan.FromMilliseconds(200) };
        var (vm, row) = await CreateExpandedAsync(plugin, validationTimeout: TimeSpan.FromMilliseconds(40));

        await vm.RunSettingsActionCommand.ExecuteAsync(row.Actions[0]);

        Assert.Equal("download done", CurrentRow(vm).Status);
    }

    [Fact]
    public async Task FailingAction_IsLoggedAndReported()
    {
        var plugin = new FakeActionsPlugin { Failure = new InvalidOperationException("boom") };
        var errorLog = new ErrorLogService(_tempDir);
        var (vm, row) = await CreateExpandedAsync(plugin, errorLog: errorLog);

        await vm.RunSettingsActionCommand.ExecuteAsync(row.Actions[0]);

        Assert.Equal(Loc.Instance["Plugins.ActionFailed"], CurrentRow(vm).Status);
        Assert.Contains(errorLog.Entries, entry => entry.Message.Contains("boom"));
    }

    [Fact]
    public async Task RowsRebuiltDuringAnAction_KeepShowingItAsRunning()
    {
        var plugin = new FakeActionsPlugin { HoldUntilCancelled = true };
        var (vm, row) = await CreateExpandedAsync(plugin);
        var run = vm.RunSettingsActionCommand.ExecuteAsync(row.Actions[0]);
        await plugin.Started.Task.WaitAsync(s_hangGuard);

        InvokeRefresh(vm);
        var rebuilt = CurrentRow(vm);
        await WaitUntilAsync(() => rebuilt.HasActions);

        Assert.NotSame(row, rebuilt);
        Assert.True(rebuilt.IsActionRunning);
        Assert.False(plugin.Cancelled);

        vm.CancelSettingsActionCommand.Execute(rebuilt);
        await run.WaitAsync(s_hangGuard);
        Assert.False(CurrentRow(vm).IsActionRunning);
    }

    [Fact]
    public async Task ReplacedPlugin_CancelsItsRunningAction()
    {
        var plugin = new FakeActionsPlugin { HoldUntilCancelled = true };
        var (vm, row) = await CreateExpandedAsync(plugin);
        var run = vm.RunSettingsActionCommand.ExecuteAsync(row.Actions[0]);
        await plugin.Started.Task.WaitAsync(s_hangGuard);

        ReplaceLoadedPlugins(vm, new FakeActionsPlugin());
        InvokeRefresh(vm);
        await run.WaitAsync(s_hangGuard);

        Assert.True(plugin.Cancelled);
    }

    private async Task<(PluginsSectionViewModel Vm, PluginRow Row)> CreateExpandedAsync(
        FakeActionsPlugin plugin,
        TimeSpan? validationTimeout = null,
        ErrorLogService? errorLog = null)
    {
        var loaded = TestPluginManagerFactory.CreateLoadedPlugin(_tempDir, TestPluginId, plugin);
        var manager = TestPluginManagerFactory.Create(loadedPlugins: [loaded], activatedPluginIds: [TestPluginId]);
        var vm = new PluginsSectionViewModel(
            manager,
            errorLog ?? new ErrorLogService(_tempDir),
            TimeSpan.FromSeconds(5),
            validationTimeout,
            // Synchronous post: mirrors the real Dispatcher.UIThread serialization
            // without a headless dispatcher to pump.
            action => action());
        var row = CurrentRow(vm);
        await vm.ToggleExpandedCommand.ExecuteAsync(row);
        return (vm, row);
    }

    private static PluginRow CurrentRow(PluginsSectionViewModel vm) =>
        vm.EnabledGroups.Concat(vm.DisabledGroups).SelectMany(group => group.Plugins).First(row => row.Id == TestPluginId);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_hangGuard;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition was not met in time.");
            await Task.Delay(10);
        }
    }

    private static void InvokeRefresh(PluginsSectionViewModel vm) =>
        (typeof(PluginsSectionViewModel).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)
         ?? throw new MissingMethodException(nameof(PluginsSectionViewModel), "Refresh")).Invoke(vm, null);

    // PluginManager has no public seam for swapping a loaded instance; reflection mirrors a reload.
    private void ReplaceLoadedPlugins(PluginsSectionViewModel vm, ITypeWhisperPlugin replacement)
    {
        var manager = (PluginManager)typeof(PluginsSectionViewModel)
            .GetField("_pluginManager", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm)!;
        var field = typeof(PluginManager).GetField("_allPlugins", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(nameof(PluginManager), "_allPlugins");
        field.SetValue(manager, new List<LoadedPlugin> { TestPluginManagerFactory.CreateLoadedPlugin(_tempDir, TestPluginId, replacement) });
    }

    private sealed class FakeActionsPlugin
        : ITypeWhisperPlugin, IPluginSettingsProvider, IPluginSettingsActions, IPluginSettingsActivity
    {
        private string _value = "initial";

        public bool RemoveEnabled { get; init; }
        public Task? SaveGate { get; init; }
        public Func<string, string>? NormalizeValue { get; init; }
        public bool HoldUntilCancelled { get; init; }
        public TimeSpan Delay { get; init; }
        public Exception? Failure { get; init; }
        public List<string> Calls { get; } = [];
        public int ActionReads { get; private set; }
        public bool Cancelled { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string PluginId => TestPluginId;
        public string PluginName => "Actions";
        public string PluginVersion => "1.0.0";
        public double? SettingsProgress { get; private set; }

        public event Action<string?>? SettingsActivityChanged;

        public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
            [new("value", "Value", Kind: PluginSettingKind.Text)];

        public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<string?>(_value);

        public async Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
        {
            if (SaveGate is not null)
                await SaveGate;
            _value = NormalizeValue?.Invoke(value ?? string.Empty) ?? value ?? string.Empty;
            lock (Calls)
                Calls.Add($"set {key}={value}");
        }

        public IReadOnlyList<PluginSettingsAction> GetSettingsActions()
        {
            ActionReads++;
            return
            [
                new PluginSettingsAction("download", "Download files", "Fetches the files."),
                new PluginSettingsAction("remove", "Remove files", IsEnabled: RemoveEnabled, ConfirmationMessage: "Really remove?"),
            ];
        }

        public async Task<PluginSettingsValidationResult> ExecuteSettingsActionAsync(string actionId, CancellationToken ct)
        {
            lock (Calls)
                Calls.Add($"run {actionId}");
            Started.TrySetResult();
            if (Failure is not null)
                throw Failure;
            try
            {
                if (HoldUntilCancelled)
                    await Task.Delay(Timeout.Infinite, ct);
                if (Delay > TimeSpan.Zero)
                    await Task.Delay(Delay, ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }

            return new PluginSettingsValidationResult(true, $"{actionId} done");
        }

        public void ReportActivity(string? message, double? progress)
        {
            SettingsProgress = progress;
            // Plugins report from worker threads.
            Task.Run(() => SettingsActivityChanged?.Invoke(message)).Wait(s_hangGuard);
        }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void Dispose() { }
    }

}
