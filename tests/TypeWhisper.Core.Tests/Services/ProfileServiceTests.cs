using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

/// <summary>Covers <see cref="ProfileService" /> persistence/round-tripping and forced/hotkey-only profile matching rules.</summary>
public sealed class ProfileServiceTests : IDisposable
{
    private static readonly TimeSpan s_testGuard = TimeSpan.FromSeconds(5);

    // The blocking writer holds the ProfileService lock until the test's finally (or Dispose)
    // calls ReleaseFirst — which happens AFTER the orchestration's SpinUntil(s_testGuard) returns.
    // Its own release-wait must therefore outlast s_testGuard by a wide margin, or under heavy load
    // (parallel test projects) the two 5 s timers race and the writer times out first. This is a
    // pure backstop against a genuinely wedged test, not part of the timing contract.
    private static readonly TimeSpan s_writerReleaseGuard = TimeSpan.FromSeconds(60);

    private readonly string _filePath;
    private readonly ProfileService _sut;

    public ProfileServiceTests()
    {
        _filePath = Path.GetTempFileName();
        _sut = new ProfileService(_filePath);
    }

    public void Dispose()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }

    [Fact]
    public void ToggleProfileEnabled_MissingId_DoesNotWriteOrNotify()
    {
        var original = new Profile
        {
            Id = "existing",
            Name = "Existing",
            IsEnabled = false
        };
        new ProfileService(_filePath).AddProfile(original);
        var writes = 0;
        var service = new ProfileService(
            _filePath,
            (_, _) =>
            {
                Interlocked.Increment(ref writes);
                throw new InvalidOperationException("A missing profile must not be written.");
            }
        );
        var initialProfiles = service.Profiles;
        var initialJson = File.ReadAllText(_filePath);
        var notifications = 0;
        service.ProfilesChanged += () => notifications++;

        var result = service.ToggleProfileEnabled("missing");

        Assert.Null(result);
        Assert.Equal(0, writes);
        Assert.Equal(0, notifications);
        Assert.Same(initialProfiles, service.Profiles);
        Assert.False(Assert.Single(service.Profiles).IsEnabled);
        Assert.Equal(initialJson, File.ReadAllText(_filePath));
    }

    [Fact]
    public async Task ToggleProfileEnabled_ConcurrentSameProfile_AppliesBothInversions()
    {
        var original = new Profile
        {
            Id = "profile",
            Name = "Profile",
            IsEnabled = false
        };
        new ProfileService(_filePath).AddProfile(original);
        using var writer = new BlockingAfterCommitWriter();
        var service = new ProfileService(_filePath, writer.Write);
        var notifications = 0;
        // ReSharper disable once AccessToModifiedClosure -- notifications is an intentional shared counter read via Volatile.Read below
        service.ProfilesChanged += () => Interlocked.Increment(ref notifications);
        var secondCallerStarted = CreateCompletionSource();
        var secondCompletion = new TaskCompletionSource<Profile?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var firstToggle = Task.Run(() => service.ToggleProfileEnabled(original.Id));
        Thread? secondThread = null;
        bool secondReachedGateOrWriter;

        try
        {
            await writer.FirstCommitted.WaitAsync(s_testGuard);
            secondThread = new Thread(() =>
            {
                secondCallerStarted.TrySetResult();
                try
                {
                    secondCompletion.TrySetResult(service.ToggleProfileEnabled(original.Id));
                }
                catch (Exception ex)
                {
                    secondCompletion.TrySetException(ex);
                }
            })
            {
                IsBackground = true
            };
            secondThread.Start();
            await secondCallerStarted.Task.WaitAsync(s_testGuard);

            secondReachedGateOrWriter = SpinWait.SpinUntil(
                // ReSharper disable once AccessToDisposedClosure -- lambda runs synchronously inside SpinUntil, before writer is disposed on scope exit
                () =>
                    writer.SecondEntered.IsCompleted
                    || IsWaiting(secondThread)
                    || !secondThread.IsAlive,
                s_testGuard
            );
        }
        finally
        {
            writer.ReleaseFirst();
            await CompleteBestEffort(firstToggle, secondCompletion.Task);
            if (secondThread is { IsAlive: true })
            {
                secondThread.Join(s_testGuard);
            }
        }

        var results = await Task.WhenAll(firstToggle, secondCompletion.Task)
            .WaitAsync(s_testGuard);

        Assert.True(secondReachedGateOrWriter);
        Assert.NotNull(results[0]);
        Assert.True(results[0]!.IsEnabled);
        Assert.NotNull(results[1]);
        Assert.False(results[1]!.IsEnabled);
        Assert.False(Assert.Single(service.Profiles).IsEnabled);
        Assert.False(Assert.Single(new ProfileService(_filePath).Profiles).IsEnabled);
        Assert.Equal(2, Volatile.Read(ref notifications));
        Assert.Equal(2, writer.InvocationCount);
        Assert.Equal(1, writer.MaximumConcurrency);
        Assert.False(writer.SecondEnteredBeforeFirstRelease);
    }

    [Fact]
    public async Task ToggleProfileEnabled_ConcurrentDifferentProfiles_PreservesBothAndKeepsDiskWithCache()
    {
        var profileA = new Profile
        {
            Id = "profile-a",
            Name = "Profile A",
            IsEnabled = false,
            Priority = 20
        };
        var profileB = new Profile
        {
            Id = "profile-b",
            Name = "Profile B",
            IsEnabled = false,
            Priority = 10
        };
        var seed = new ProfileService(_filePath);
        seed.AddProfile(profileA);
        seed.AddProfile(profileB);
        using var writer = new BlockingAfterCommitWriter();
        var service = new ProfileService(_filePath, writer.Write);
        var notifications = 0;
        // ReSharper disable once AccessToModifiedClosure -- notifications is an intentional shared counter read via Volatile.Read below
        service.ProfilesChanged += () => Interlocked.Increment(ref notifications);
        var secondCallerStarted = CreateCompletionSource();
        var secondCompletion = new TaskCompletionSource<Profile?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var firstToggle = Task.Run(() => service.ToggleProfileEnabled(profileA.Id));
        Thread? secondThread = null;
        bool secondReachedGateOrWriter;

        try
        {
            await writer.FirstCommitted.WaitAsync(s_testGuard);
            secondThread = new Thread(() =>
            {
                secondCallerStarted.TrySetResult();
                try
                {
                    secondCompletion.TrySetResult(service.ToggleProfileEnabled(profileB.Id));
                }
                catch (Exception ex)
                {
                    secondCompletion.TrySetException(ex);
                }
            })
            {
                IsBackground = true
            };
            secondThread.Start();
            await secondCallerStarted.Task.WaitAsync(s_testGuard);

            secondReachedGateOrWriter = SpinWait.SpinUntil(
                // ReSharper disable once AccessToDisposedClosure -- lambda runs synchronously inside SpinUntil, before writer is disposed on scope exit
                () =>
                    writer.SecondEntered.IsCompleted
                    || IsWaiting(secondThread)
                    || !secondThread.IsAlive,
                s_testGuard
            );
        }
        finally
        {
            writer.ReleaseFirst();
            await CompleteBestEffort(firstToggle, secondCompletion.Task);
            if (secondThread is { IsAlive: true })
            {
                secondThread.Join(s_testGuard);
            }
        }

        var results = await Task.WhenAll(firstToggle, secondCompletion.Task)
            .WaitAsync(s_testGuard);
        var inMemory = service.Profiles
            .Select(profile => (profile.Id, profile.IsEnabled))
            .ToArray();
        var persisted = new ProfileService(_filePath).Profiles
            .Select(profile => (profile.Id, profile.IsEnabled))
            .ToArray();

        Assert.True(secondReachedGateOrWriter);
        Assert.NotNull(results[0]);
        Assert.True(results[0]!.IsEnabled);
        Assert.NotNull(results[1]);
        Assert.True(results[1]!.IsEnabled);
        Assert.Equal(
            [(profileA.Id, true), (profileB.Id, true)],
            inMemory
        );
        Assert.Equal(inMemory, persisted);
        Assert.Equal(2, Volatile.Read(ref notifications));
        Assert.Equal(2, writer.InvocationCount);
        Assert.Equal(1, writer.MaximumConcurrency);
        Assert.False(writer.SecondEnteredBeforeFirstRelease);
    }

    [Fact]
    public void PromptActionId_RoundTrips()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test Profile",
            PromptActionId = "prompt-123"
        };

        _sut.AddProfile(profile);

        var freshService = new ProfileService(_filePath);
        var loaded = freshService.Profiles.First(p => p.Id == profile.Id);
        Assert.Equal("prompt-123", loaded.PromptActionId);
    }

    [Fact]
    public void PromptActionId_NullRoundTrips()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "No Prompt",
            PromptActionId = null
        };

        _sut.AddProfile(profile);

        var freshService = new ProfileService(_filePath);
        var loaded = freshService.Profiles.First(p => p.Id == profile.Id);
        Assert.Null(loaded.PromptActionId);
    }

    [Fact]
    public void UpdateProfile_ChangesPromptActionId()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test",
            PromptActionId = null
        };

        _sut.AddProfile(profile);
        _sut.UpdateProfile(profile with { PromptActionId = "action-456" });

        var freshService = new ProfileService(_filePath);
        var loaded = freshService.Profiles.First(p => p.Id == profile.Id);
        Assert.Equal("action-456", loaded.PromptActionId);
    }

    [Fact]
    public void HotkeyData_RoundTrips()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "With Hotkey",
            HotkeyData = "{\"key\":\"Ctrl+1\"}"
        };

        _sut.AddProfile(profile);

        var freshService = new ProfileService(_filePath);
        var loaded = freshService.Profiles.First(p => p.Id == profile.Id);
        Assert.Equal("{\"key\":\"Ctrl+1\"}", loaded.HotkeyData);
    }

    [Fact]
    public void HotkeyData_NullByDefault()
    {
        var profile = new Profile { Id = Guid.NewGuid().ToString(), Name = "No Hotkey" };

        _sut.AddProfile(profile);

        var freshService = new ProfileService(_filePath);
        var loaded = freshService.Profiles.First(p => p.Id == profile.Id);
        Assert.Null(loaded.HotkeyData);
    }

    [Fact]
    public void StylePreset_RoundTrips()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Email",
            StylePreset = ProfileStylePreset.FormalEmail
        };

        _sut.AddProfile(profile);

        var freshService = new ProfileService(_filePath);
        var loaded = freshService.Profiles.First(p => p.Id == profile.Id);
        Assert.Equal(ProfileStylePreset.FormalEmail, loaded.StylePreset);
    }

    [Fact]
    public void StylePreset_DefaultsToRawForLegacyJson()
    {
        File.WriteAllText(
            _filePath,
            """
            [
              {
                "Id": "legacy",
                "Name": "Legacy"
              }
            ]
            """
        );

        var freshService = new ProfileService(_filePath);

        var loaded = Assert.Single(freshService.Profiles);
        Assert.Equal(ProfileStylePreset.Raw, loaded.StylePreset);
    }

    [Fact]
    public void StylePresetService_ResolvesDeveloperWithoutTerminalSafe()
    {
        var result = ProfileStylePresetService.Resolve(ProfileStylePreset.Developer);

        Assert.Equal(CleanupLevel.None, result.CleanupLevel);
        Assert.True(result.DeveloperFormattingEnabled);
        Assert.False(result.TerminalSafe);
    }

    [Fact]
    public void StylePresetService_ResolvesCleanWithLightCleanup()
    {
        var result = ProfileStylePresetService.Resolve(ProfileStylePreset.Clean);

        Assert.Equal(CleanupLevel.Light, result.CleanupLevel);
        Assert.True(result.SmartFormattingEnabled);
        Assert.False(result.TerminalSafe);
    }

    [Fact]
    public void HotkeyBehavior_RoundTrips()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Selection",
            HotkeyData = "Ctrl+Shift+S",
            HotkeyBehavior = ProfileHotkeyBehavior.ProcessSelectedText
        };

        _sut.AddProfile(profile);

        var freshService = new ProfileService(_filePath);
        var loaded = freshService.Profiles.First(p => p.Id == profile.Id);
        Assert.Equal(ProfileHotkeyBehavior.ProcessSelectedText, loaded.HotkeyBehavior);
    }

    [Fact]
    public void HotkeyBehavior_DefaultsToStartDictationForLegacyJson()
    {
        File.WriteAllText(
            _filePath,
            """
            [
              {
                "Id": "legacy",
                "Name": "Legacy"
              }
            ]
            """
        );

        var freshService = new ProfileService(_filePath);

        var loaded = Assert.Single(freshService.Profiles);
        Assert.Equal(ProfileHotkeyBehavior.StartDictation, loaded.HotkeyBehavior);
    }

    [Fact]
    public void MatchProfile_ForcedEnabledProfile_ReturnsManualOverride()
    {
        var forced = new Profile
        {
            Id = "forced",
            Name = "Forced",
            ProcessNames = ["never-matches"]
        };
        _sut.AddProfile(forced);

        // No process/url context matches the forced profile, yet the forced id
        // wins as a ManualOverride.
        var result = _sut.MatchProfile("some-other-app", null, "forced");

        Assert.Equal(MatchKind.ManualOverride, result.Kind);
        Assert.NotNull(result.Profile);
        Assert.Equal("forced", result.Profile.Id);
    }

    [Fact]
    public void MatchProfile_ForcedDisabledProfile_FallsThrough()
    {
        // A normal enabled profile with no matchers acts as the global
        // fallback; the forced id pointing at a disabled profile must fall
        // through to it rather than honoring the disabled selection.
        _sut.AddProfile(new Profile { Id = "fallback", Name = "Fallback" });
        _sut.AddProfile(new Profile
        {
            Id = "forced",
            Name = "Forced",
            IsEnabled = false
        });

        var result = _sut.MatchProfile(null, null, "forced");

        Assert.Equal(MatchKind.Global, result.Kind);
        Assert.NotNull(result.Profile);
        Assert.Equal("fallback", result.Profile.Id);
    }

    [Fact]
    public void MatchProfile_ForcedMissingProfile_FallsThrough()
    {
        _sut.AddProfile(new Profile { Id = "fallback", Name = "Fallback" });

        var result = _sut.MatchProfile(null, null, "does-not-exist");

        Assert.Equal(MatchKind.Global, result.Kind);
        Assert.NotNull(result.Profile);
        Assert.Equal("fallback", result.Profile.Id);
    }

    [Fact]
    public void MatchProfile_HotkeyOnlyProfileWithNoMatchers_IsExcludedFromGlobalFallback()
    {
        // A profile with no app/URL matchers but a hotkey is hotkey-only: it
        // must NOT act as the global fallback, or it would hijack plain
        // dictation in every window.
        _sut.AddProfile(new Profile
        {
            Id = "hotkey-only",
            Name = "Hotkey Only",
            HotkeyData = "Ctrl+Alt+E"
        });

        var result = _sut.MatchProfile("some-app", null);

        Assert.Equal(MatchKind.NoMatch, result.Kind);
        Assert.Null(result.Profile);
    }

    [Fact]
    public void MatchProfile_HotkeyOnlyProfile_StillForceMatchesByHotkey()
    {
        // The exclusion only applies to the ambient cascade; forcing the
        // profile by id (its chord was pressed) must still win.
        _sut.AddProfile(new Profile
        {
            Id = "hotkey-only",
            Name = "Hotkey Only",
            HotkeyData = "Ctrl+Alt+E"
        });

        var result = _sut.MatchProfile("some-app", null, "hotkey-only");

        Assert.Equal(MatchKind.ManualOverride, result.Kind);
        Assert.Equal("hotkey-only", result.Profile!.Id);
    }

    [Fact]
    public void MatchProfile_EmptyMatcherProfileWithoutHotkey_RemainsGlobalFallback()
    {
        // Regression guard: the exclusion is gated on having a hotkey. A plain
        // no-matcher profile is still the global fallback as before.
        _sut.AddProfile(new Profile
        {
            Id = "global",
            Name = "Global"
        });

        var result = _sut.MatchProfile("some-app", null);

        Assert.Equal(MatchKind.Global, result.Kind);
        Assert.Equal("global", result.Profile!.Id);
    }

    private static TaskCompletionSource CreateCompletionSource()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static bool IsWaiting(Thread thread)
    {
        return (thread.ThreadState & ThreadState.WaitSleepJoin) != 0;
    }

    private static async Task CompleteBestEffort(params Task?[] tasks)
    {
        var activeTasks = tasks.Where(task => task is not null).Cast<Task>().ToArray();
        if (activeTasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(activeTasks).WaitAsync(s_testGuard);
        }
        catch
        {
            // Best-effort bounded completion before temporary-file cleanup.
        }
    }

    private sealed class BlockingAfterCommitWriter : IDisposable
    {
        private readonly TaskCompletionSource _firstCommitted = CreateCompletionSource();
        private readonly ManualResetEventSlim _releaseFirst = new(false);
        private readonly TaskCompletionSource _secondEntered = CreateCompletionSource();
        private int _activeWriters;
        private int _firstReleased;
        private int _invocations;
        private int _maximumConcurrency;
        private int _secondEnteredBeforeFirstRelease;

        public Task FirstCommitted => _firstCommitted.Task;
        public Task SecondEntered => _secondEntered.Task;
        public int InvocationCount => Volatile.Read(ref _invocations);
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
        public bool SecondEnteredBeforeFirstRelease =>
            Volatile.Read(ref _secondEnteredBeforeFirstRelease) != 0;

        public void Write(string path, string contents)
        {
            var invocation = Interlocked.Increment(ref _invocations);
            var activeWriters = Interlocked.Increment(ref _activeWriters);
            UpdateMaximum(activeWriters);
            try
            {
                if (invocation == 1)
                {
                    AtomicFileWrite.WriteAllText(path, contents);
                    _firstCommitted.TrySetResult();
                    if (!_releaseFirst.Wait(s_writerReleaseGuard))
                    {
                        throw new TimeoutException("The first committed writer was not released.");
                    }
                }
                else
                {
                    if (Volatile.Read(ref _firstReleased) == 0)
                    {
                        Interlocked.Exchange(ref _secondEnteredBeforeFirstRelease, 1);
                    }

                    _secondEntered.TrySetResult();
                    AtomicFileWrite.WriteAllText(path, contents);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeWriters);
            }
        }

        public void ReleaseFirst()
        {
            Volatile.Write(ref _firstReleased, 1);
            _releaseFirst.Set();
        }

        public void Dispose()
        {
            ReleaseFirst();
            _releaseFirst.Dispose();
        }

        private void UpdateMaximum(int activeWriters)
        {
            var current = Volatile.Read(ref _maximumConcurrency);
            while (
                activeWriters > current
                && Interlocked.CompareExchange(
                    ref _maximumConcurrency,
                    activeWriters,
                    current
                ) != current
            )
            {
                current = Volatile.Read(ref _maximumConcurrency);
            }
        }
    }
}
