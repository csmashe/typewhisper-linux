using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Moq;
using TypeWhisper.FakeProviderCli;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Plugin.AuthenticatedCli;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

// The provider CLIs are discovered by file name on PATH, so every end-to-end case runs against a
// shell shim named codex/claude/opencode that execs the FakeProviderCli assembly.
public sealed class AuthenticatedCliPluginTests
{
    private static readonly JsonSerializerOptions s_manifestJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task CodexProvider_UsesStructuredStdinAndFixedArguments()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        var role = GetRole(plugin, "authenticated-cli-codex");
        const string instruction = "Transform the text and return only JSON.";
        const string input = "\"; $(touch marker) & whoami | echo $TOKEN `cmd`\r\n今天天气很好 --model evil";

        var result = await role.ProcessAsync(instruction, input, "default", CancellationToken.None);

        Assert.Equal("processed", result);
        Assert.True(role.IsAvailable);
        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(fake.CapturePath));
        var root = capture.RootElement;
        using var envelope = JsonDocument.Parse(root.GetProperty("standardInput").GetString()!);
        Assert.Equal(
            "typewhisper.prompt-processing.v1",
            envelope.RootElement.GetProperty("protocol").GetString()
        );
        Assert.Equal(instruction, envelope.RootElement.GetProperty("instruction").GetString());
        Assert.Equal(input, envelope.RootElement.GetProperty("input").GetString());
        var arguments = root.GetProperty("arguments").EnumerateArray()
            .Select(value => value.GetString()!)
            .ToList();
        Assert.DoesNotContain(arguments, argument => argument.Contains(input, StringComparison.Ordinal));
        Assert.Contains("--ignore-user-config", arguments);
        Assert.Contains("--strict-config", arguments);
        Assert.Contains("features.shell_tool=false", arguments);
        Assert.Contains("features.apps=false", arguments);
        Assert.Contains("apps._default.enabled=false", arguments);
        Assert.Contains("agents.enabled=false", arguments);
        Assert.Contains("project_doc_max_bytes=0", arguments);
        // A shell would have expanded the payload into the request directory before the CLI ran.
        Assert.DoesNotContain(
            "marker",
            root.GetProperty("workingDirectoryEntries").EnumerateArray()
                .Select(entry => entry.GetString()!)
        );
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task ClaudeProvider_UsesSafeModeAndStructuredOutput()
    {
        using var fake = FakeCliInstallation.Create("success", "claude");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        var role = GetRole(plugin, "authenticated-cli-claude");

        var result = await role.ProcessAsync("Instruction", "Input", "default", CancellationToken.None);

        Assert.Equal("processed", result);
        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(fake.CapturePath));
        var arguments = capture.RootElement.GetProperty("arguments").EnumerateArray()
            .Select(value => value.GetString()!)
            .ToList();
        Assert.Contains("--safe-mode", arguments);
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.Contains("--no-session-persistence", arguments);
        Assert.Contains("--disallowedTools", arguments);
        Assert.Contains("--system-prompt", arguments);
        // Dropped on the fork: the shipping CLI no longer has the flag, and passing it would
        // fail the request with "unknown option".
        Assert.DoesNotContain("--max-turns", arguments);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task SignedOutProvider_IsNotAdvertisedAsAvailable()
    {
        using var fake = FakeCliInstallation.Create("signed-out", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);

        await plugin.RefreshFromSettingsAsync();

        Assert.Equal(
            CliAvailabilityState.SignedOut,
            plugin.GetSnapshot(Descriptor(CliProviderKind.Codex)).State
        );
        Assert.False(GetRole(plugin, "authenticated-cli-codex").IsAvailable);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task ClaudeAuthenticationProbe_RequiresPositiveLoggedInField()
    {
        using var fake = FakeCliInstallation.Create("auth-unknown", "claude");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);

        await plugin.RefreshFromSettingsAsync();

        Assert.Equal(
            CliAvailabilityState.AuthenticationUnknown,
            plugin.GetSnapshot(Descriptor(CliProviderKind.Claude)).State
        );
        Assert.False(GetRole(plugin, "authenticated-cli-claude").IsAvailable);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task RuntimeAuthenticationFailure_DisablesProviderAndIsActionable()
    {
        using var fake = FakeCliInstallation.Create("auth-error", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        var role = GetRole(plugin, "authenticated-cli-codex");

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => role.ProcessAsync(
            "Instruction",
            "Input",
            "default",
            CancellationToken.None
        ));

        Assert.Equal(PluginRequestFailureKind.Authentication, error.FailureKind);
        Assert.False(error.IsTransient);
        Assert.False(role.IsAvailable);
        Assert.Equal(
            CliAvailabilityState.SignedOut,
            plugin.GetSnapshot(Descriptor(CliProviderKind.Codex)).State
        );
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task RuntimeRateLimit_RemainsTransientWithoutDisablingProvider()
    {
        using var fake = FakeCliInstallation.Create("rate-limit", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        var role = GetRole(plugin, "authenticated-cli-codex");

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => role.ProcessAsync(
            "Instruction",
            "Input",
            "default",
            CancellationToken.None
        ));

        Assert.Equal(PluginRequestFailureKind.RateLimit, error.FailureKind);
        Assert.True(error.IsTransient);
        Assert.True(role.IsAvailable);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task NetworkFailureMentioningAuthenticationAndModel_RemainsTransient()
    {
        using var fake = FakeCliInstallation.Create("network-auth-model", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        var role = GetRole(plugin, "authenticated-cli-codex");

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => role.ProcessAsync(
            "Instruction",
            "Input",
            "default",
            CancellationToken.None
        ));

        Assert.Equal(PluginRequestFailureKind.Network, error.FailureKind);
        Assert.True(error.IsTransient);
        Assert.True(role.IsAvailable);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task UnsupportedModel_FailsBeforeLaunchingProviderCli()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        var role = GetRole(plugin, "authenticated-cli-codex");

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => role.ProcessAsync(
            "Instruction",
            "Input",
            "unsupported-model",
            CancellationToken.None
        ));

        Assert.Equal(PluginRequestFailureKind.InvalidRequest, error.FailureKind);
        Assert.False(error.IsTransient);
        Assert.False(File.Exists(fake.CapturePath));
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task PreviouslySelectedExecutable_DoesNotSilentlySwitchAfterPathChange()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        var host = CreateHost();
        host.Setup(service => service.GetSetting<string>("codexInstallation"))
            .Returns(Path.Join(fake.DirectoryPath, "previous", "codex"));

        await plugin.ActivateAsync(host.Object);
        await plugin.RefreshFromSettingsAsync();

        var snapshot = plugin.GetSnapshot(Descriptor(CliProviderKind.Codex));
        Assert.Equal(CliAvailabilityState.SelectedExecutableMissing, snapshot.State);
        Assert.False(GetRole(plugin, "authenticated-cli-codex").IsAvailable);
        Assert.Equal(fake.ExecutablePath("codex"), Assert.Single(snapshot.Candidates));
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task MissingCli_BecomesReadyAfterRefreshWithoutRestart()
    {
        using var fake = FakeCliInstallation.CreateEmpty("success");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        var host = CreateHost();
        await plugin.ActivateAsync(host.Object);
        await plugin.RefreshFromSettingsAsync();
        Assert.Equal(
            CliAvailabilityState.MissingExecutable,
            plugin.GetSnapshot(Descriptor(CliProviderKind.Codex)).State
        );

        fake.Install("codex");
        await plugin.RefreshFromSettingsAsync();

        Assert.Equal(
            CliAvailabilityState.Ready,
            plugin.GetSnapshot(Descriptor(CliProviderKind.Codex)).State
        );
        host.Verify(service => service.NotifyCapabilitiesChanged(), Times.AtLeastOnce);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task MultipleInstallations_RequireExplicitSelection()
    {
        using var first = FakeCliInstallation.Create("success", "codex");
        using var second = FakeCliInstallation.Create("success", "codex");
        using var plugin = CreatePlugin($"{first.DirectoryPath}:{second.DirectoryPath}");
        await plugin.ActivateAsync(CreateHost().Object);

        await plugin.RefreshFromSettingsAsync();

        var snapshot = plugin.GetSnapshot(Descriptor(CliProviderKind.Codex));
        Assert.Equal(CliAvailabilityState.AmbiguousExecutable, snapshot.State);
        Assert.Equal(2, snapshot.Candidates.Count);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public void Discovery_SearchesPath_ResolvesSymlinks_AndRejectsNonExecutables()
    {
        using var fake = FakeCliInstallation.CreateEmpty("success");
        string? processPath = null;
        // ReSharper disable once AccessToModifiedClosure
        // Rewriting PATH between assertions is what this test is checking.
        var discovery = new CliExecutableDiscovery(() => processPath);

        Assert.Empty(discovery.FindCandidates("codex"));
        fake.Install("codex");
        // A relative entry and a directory that does not exist are both skipped.
        processPath = $"{fake.DirectoryPath}:.:{Path.Join(fake.DirectoryPath, "missing")}";
        Assert.Equal(fake.ExecutablePath("codex"), Assert.Single(discovery.FindCandidates("codex")));

        // The same directory twice yields one candidate; Linux paths are case-sensitive, so a
        // differently-cased name is a different file, not a duplicate.
        processPath = $"{fake.DirectoryPath}:{fake.DirectoryPath}";
        Assert.Single(discovery.FindCandidates("codex"));
        Assert.Empty(discovery.FindCandidates("Codex"));

        // A candidate must carry the exact provider file name, and a bare name is not a path.
        var scriptPath = Path.Join(fake.DirectoryPath, "codex.sh");
        File.WriteAllText(scriptPath, "exit 0");
        Assert.False(CliExecutableDiscovery.IsUsableExecutable(scriptPath, "codex"));
        Assert.False(CliExecutableDiscovery.IsUsableExecutable("codex", "codex"));
        Assert.False(CliPathSafety.IsSafeLocalDirectory("relative/path"));
    }

    [Fact]
    public void Discovery_AcceptsSymlinkedInstallationsAndRejectsUnsetExecuteBit()
    {
        using var fake = FakeCliInstallation.CreateEmpty("success");
        fake.Install("codex");
        var linkDirectory = Path.Join(fake.RootPath, "link-bin");
        Directory.CreateDirectory(linkDirectory);
        var linkPath = Path.Join(linkDirectory, "codex");
        File.CreateSymbolicLink(linkPath, fake.ExecutablePath("codex"));

        Assert.Equal(
            fake.ExecutablePath("codex"),
            CliExecutableDiscovery.ResolveRealPath(linkPath, "codex")
        );
        Assert.Equal(
            linkPath,
            Assert.Single(new CliExecutableDiscovery(() => linkDirectory).FindCandidates("codex"))
        );

        var plainPath = Path.Join(linkDirectory, "opencode");
        File.WriteAllText(plainPath, "#!/bin/sh\n");
        SetUnixMode(plainPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Null(CliExecutableDiscovery.ResolveRealPath(plainPath, "opencode"));
    }

    [Fact]
    public void AnsiStripping_SurvivesHostileEscapeInput()
    {
        // 64 KiB of OSC starts: a backtracking engine goes quadratic here, and it would do so
        // while the plugin's refresh gate is held. The bound is a second because the measured
        // backtracking cost at this size is several, while the non-backtracking pattern is
        // milliseconds, so anything in between still fails the test.
        var hostile = string.Concat(Enumerable.Repeat("\e]", 32 * 1024));
        var stopwatch = Stopwatch.StartNew();

        Assert.False(Descriptor(CliProviderKind.OpenCode).IsAuthenticated(0, hostile));
        Assert.Throws<CliProtocolException>(() =>
            OpenCodeModelCatalogLoader.Parse(hostile, DateTimeOffset.UnixEpoch));

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Stripping escape sequences took {stopwatch.Elapsed}."
        );
    }

    [Fact]
    public void ScratchDirectories_AreCreatedPrivateAndRefuseASharedOrSymlinkedParent()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "TypeWhisperScratchTests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        try
        {
            var fresh = Path.Join(root, "fresh");
            AuthenticatedCliPlugin.CreatePrivateDirectory(fresh);
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                GetUnixMode(fresh)
            );
            // Re-entering our own private directory is what every later request does.
            AuthenticatedCliPlugin.CreatePrivateDirectory(fresh);

            var shared = Path.Join(root, "shared");
            Directory.CreateDirectory(shared);
            SetUnixMode(
                shared,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
            );
            Assert.Equal(
                PluginRequestFailureKind.Configuration,
                Assert.Throws<PluginRequestException>(() =>
                    AuthenticatedCliPlugin.CreatePrivateDirectory(shared)).FailureKind
            );

            var planted = Path.Join(root, "planted");
            Directory.CreateSymbolicLink(planted, fresh);
            Assert.Equal(
                PluginRequestFailureKind.Configuration,
                Assert.Throws<PluginRequestException>(() =>
                    AuthenticatedCliPlugin.CreatePrivateDirectory(planted)).FailureKind
            );
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Request_ReverifiesAnExecutableRetargetedByASelfUpdate()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        var linkDirectory = Path.Join(fake.RootPath, "link-bin");
        Directory.CreateDirectory(linkDirectory);
        var linkPath = Path.Join(linkDirectory, "codex");
        File.CreateSymbolicLink(linkPath, fake.ExecutablePath("codex"));
        using var plugin = CreatePlugin(linkDirectory);
        await plugin.ActivateAsync(CreateHost().Object);
        await plugin.RefreshFromSettingsAsync();
        Assert.Equal(
            CliAvailabilityState.Ready,
            plugin.GetSnapshot(Descriptor(CliProviderKind.Codex)).State
        );

        // What these CLIs' own installers do on every update: repoint the launcher at a new
        // version directory, which must re-verify rather than disable the provider.
        var updated = Path.Join(fake.RootPath, "updated-codex");
        File.Copy(fake.ExecutablePath("codex"), updated);
        SetUnixMode(
            updated,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        );
        File.Delete(linkPath);
        File.CreateSymbolicLink(linkPath, updated);

        var result = await GetRole(plugin, "authenticated-cli-codex").ProcessAsync(
            "Instruction",
            "Input",
            "default",
            CancellationToken.None
        );

        Assert.Equal("processed", result);
        Assert.Equal(
            updated,
            plugin.GetSnapshot(Descriptor(CliProviderKind.Codex)).ResolvedExecutablePath
        );
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task Request_RefusesAnExecutableSwappedForOneItCannotVerify()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        var linkDirectory = Path.Join(fake.RootPath, "link-bin");
        Directory.CreateDirectory(linkDirectory);
        var linkPath = Path.Join(linkDirectory, "codex");
        File.CreateSymbolicLink(linkPath, fake.ExecutablePath("codex"));
        using var plugin = CreatePlugin(linkDirectory);
        await plugin.ActivateAsync(CreateHost().Object);
        await plugin.RefreshFromSettingsAsync();
        Assert.Equal(
            CliAvailabilityState.Ready,
            plugin.GetSnapshot(Descriptor(CliProviderKind.Codex)).State
        );

        var swapped = Path.Join(fake.RootPath, "swapped-codex");
        await File.WriteAllTextAsync(swapped, "#!/bin/sh\n");
        SetUnixMode(swapped, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Delete(linkPath);
        File.CreateSymbolicLink(linkPath, swapped);

        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            GetRole(plugin, "authenticated-cli-codex").ProcessAsync(
                "Instruction",
                "Input",
                "default",
                CancellationToken.None
            ));

        Assert.Equal(PluginRequestFailureKind.Configuration, error.FailureKind);
        Assert.Equal(
            CliAvailabilityState.MissingExecutable,
            plugin.GetSnapshot(Descriptor(CliProviderKind.Codex)).State
        );
        await plugin.DeactivateAsync();
    }

    [Fact]
    public void CapabilityProbe_RequiresExactSafetyFlagTokens()
    {
        var codex = Descriptor(CliProviderKind.Codex);
        const string exact =
            "--ignore-user-config --ignore-rules --ephemeral --output-schema --strict-config --json --sandbox --skip-git-repo-check";
        const string substringOnly =
            "--ignore-user-config --ignore-rules --ephemeral --output-schema --strict-config --json-schema --sandbox --skip-git-repo-check";

        Assert.True(codex.HasRequiredCapabilities(exact));
        Assert.False(codex.HasRequiredCapabilities(substringOnly));
    }

    [Fact]
    public async Task ThrowingSettingsActivitySubscriber_DoesNotLeakRefreshGate()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        plugin.SettingsActivityChanged += _ => throw new InvalidOperationException("subscriber failure");
        await plugin.ActivateAsync(CreateHost().Object);

        await plugin.RefreshFromSettingsAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await plugin.RefreshFromSettingsAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(
            CliAvailabilityState.Ready,
            plugin.GetSnapshot(Descriptor(CliProviderKind.Codex)).State
        );
        await plugin.DeactivateAsync();
    }

    [Fact]
    public void ProcessCommand_UsesNoShellAndDropsUnrelatedSecrets()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        var request = CreateRunnerRequest(fake, "input", TimeSpan.FromSeconds(5));
        using (new TemporaryEnvironment(
                   ("TYPEWHISPER_TEST_SECRET", "must-not-leak"),
                   ("OPENAI_API_KEY", "must-not-leak"),
                   ("ANTHROPIC_API_KEY", "must-not-leak")))
        {
            var command = CliProcessRunner.CreateProcessCommand(request);

            Assert.Equal(fake.ExecutablePath("codex"), command.FileName);
            Assert.Equal(fake.WorkingDirectory, command.WorkingDirectory);
            Assert.Equal(request.Arguments, command.Arguments);
            var environment = command.Environment!;
            Assert.False(environment.ContainsKey("TYPEWHISPER_TEST_SECRET"));
            Assert.False(environment.ContainsKey("OPENAI_API_KEY"));
            Assert.False(environment.ContainsKey("ANTHROPIC_API_KEY"));
            Assert.Equal(
                $"{fake.DirectoryPath}:/usr/local/bin:/usr/bin:/bin",
                environment["PATH"]
            );
            Assert.Equal(fake.WorkingDirectory, environment["TMPDIR"]);
            Assert.Equal("1", environment["NO_COLOR"]);
            Assert.Equal("dumb", environment["TERM"]);
            // Codex, not Claude: the Claude-only history opt-out must not travel everywhere.
            Assert.False(environment.ContainsKey("CLAUDE_CODE_SKIP_PROMPT_HISTORY"));
        }
    }

    [Fact]
    public async Task Request_LaunchesTheChildWithOnlyTheAllowListedEnvironment()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        using (new TemporaryEnvironment(
                   ("TYPEWHISPER_TEST_SECRET", "must-not-leak"),
                   ("OPENAI_API_KEY", "must-not-leak"),
                   ("ANTHROPIC_API_KEY", "must-not-leak")))
        {
            await GetRole(plugin, "authenticated-cli-codex").ProcessAsync(
                "Instruction",
                "Input",
                "default",
                CancellationToken.None
            );
        }

        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(fake.CapturePath));
        var environment = capture.RootElement.GetProperty("environment");
        Assert.False(environment.TryGetProperty("TYPEWHISPER_TEST_SECRET", out _));
        Assert.False(environment.TryGetProperty("OPENAI_API_KEY", out _));
        Assert.False(environment.TryGetProperty("ANTHROPIC_API_KEY", out _));
        Assert.Equal("1", environment.GetProperty("NO_COLOR").GetString());
        Assert.Equal("dumb", environment.GetProperty("TERM").GetString());
        Assert.Equal(
            $"{fake.DirectoryPath}:/usr/local/bin:/usr/bin:/bin",
            environment.GetProperty("PATH").GetString()
        );
        await plugin.DeactivateAsync();
    }

    [Fact]
    public void ProcessCommand_AppliesOpenCodeOverridesAndRefusesUnsafeOnes()
    {
        using var fake = FakeCliInstallation.Create("success", "opencode");
        var safeData = Path.Join(fake.WorkingDirectory, "data");
        Directory.CreateDirectory(safeData);
        var previous = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", safeData);
        try
        {
            var request = CreateRunnerRequest(fake, "", TimeSpan.FromSeconds(5)) with
            {
                ExecutablePath = fake.ExecutablePath("opencode"),
                ProviderEnvironmentVariables = ["XDG_DATA_HOME"],
                EnvironmentOverrides =
                    AuthenticatedCliPlugin.CreateOpenCodeEnvironmentOverrides(fake.WorkingDirectory),
            };

            var environment = CliProcessRunner.CreateProcessCommand(request).Environment!;

            Assert.StartsWith(fake.WorkingDirectory, environment["XDG_CONFIG_HOME"], StringComparison.Ordinal);
            Assert.StartsWith(fake.WorkingDirectory, environment["XDG_CACHE_HOME"], StringComparison.Ordinal);
            Assert.StartsWith(fake.WorkingDirectory, environment["XDG_STATE_HOME"], StringComparison.Ordinal);
            Assert.StartsWith(fake.WorkingDirectory, environment["OPENCODE_DB"], StringComparison.Ordinal);
            Assert.Equal("typewhisper", environment["OPENCODE_CLIENT"]);
            Assert.Equal("{\"*\":\"deny\"}", environment["OPENCODE_PERMISSION"]);
            Assert.Equal("false", environment["OPENCODE_AUTO_SHARE"]);
            Assert.Equal("1", environment["OPENCODE_DISABLE_PROJECT_CONFIG"]);
            Assert.Equal("1", environment["OPENCODE_DISABLE_DEFAULT_PLUGINS"]);

            using var inlineConfig = JsonDocument.Parse(environment["OPENCODE_CONFIG_CONTENT"]);
            Assert.Equal("disabled", inlineConfig.RootElement.GetProperty("share").GetString());
            Assert.False(inlineConfig.RootElement.GetProperty("snapshot").GetBoolean());
            Assert.False(inlineConfig.RootElement.GetProperty("autoupdate").GetBoolean());
            Assert.Equal(
                "deny",
                inlineConfig.RootElement.GetProperty("permission").GetProperty("*").GetString()
            );
            var agents = inlineConfig.RootElement.GetProperty("agent");
            Assert.Equal(["typewhisper"], agents.EnumerateObject().Select(property => property.Name));
            Assert.Equal("primary", agents.GetProperty("typewhisper").GetProperty("mode").GetString());
            Assert.Contains(
                "untrusted source text",
                agents.GetProperty("typewhisper").GetProperty("prompt").GetString(),
                StringComparison.Ordinal
            );

            Assert.Throws<InvalidOperationException>(() => CliProcessRunner.CreateProcessCommand(
                request with
                {
                    EnvironmentOverrides = new Dictionary<string, string> { ["BAD=NAME"] = "value" },
                }
            ));
            Assert.Throws<InvalidOperationException>(() => CliProcessRunner.CreateProcessCommand(
                request with
                {
                    EnvironmentOverrides = new Dictionary<string, string> { ["PATH"] = "unsafe" },
                }
            ));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previous);
        }
    }

    [Fact]
    public void ProcessCommand_DropsAnAuthenticationStoreThatIsNotALocalDirectory()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable(
            "CODEX_HOME",
            Path.Join(fake.WorkingDirectory, "not-a-directory")
        );
        try
        {
            var environment = CliProcessRunner
                .CreateProcessCommand(CreateRunnerRequest(fake, "", TimeSpan.FromSeconds(5)))
                .Environment!;

            Assert.False(environment.ContainsKey("CODEX_HOME"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
        }
    }

    [Fact]
    public async Task Runner_DrainsLargeStderrWithoutDeadlock()
    {
        using var fake = FakeCliInstallation.Create("stderr", "codex");

        var result = await CreateRunner().RunAsync(
            CreateRunnerRequest(fake, "input", TimeSpan.FromSeconds(30)),
            CancellationToken.None
        );

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(48 * 1024, result.StandardErrorBytes);
        Assert.Equal(
            "processed",
            Descriptor(CliProviderKind.Codex).ParseSuccessfulOutput(result.StandardOutput)
        );
    }

    [Fact]
    public async Task Runner_RejectsInvalidJsonAfterSuccessfulExit()
    {
        using var fake = FakeCliInstallation.Create("invalid-json", "codex");

        var result = await CreateRunner().RunAsync(
            CreateRunnerRequest(fake, "input", TimeSpan.FromSeconds(30)),
            CancellationToken.None
        );

        Assert.Equal(0, result.ExitCode);
        Assert.ThrowsAny<JsonException>(() =>
            Descriptor(CliProviderKind.Codex).ParseSuccessfulOutput(result.StandardOutput));
    }

    [Fact]
    public async Task Runner_RejectsInvalidUtf8AsMalformedOutput()
    {
        using var fake = FakeCliInstallation.Create("invalid-utf8", "codex");

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => CreateRunner().RunAsync(
            CreateRunnerRequest(fake, "input", TimeSpan.FromSeconds(30)),
            CancellationToken.None
        ));

        Assert.Equal(PluginRequestFailureKind.Unknown, error.FailureKind);
        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task Runner_StartFailureCarriesTheLauncherDiagnostic()
    {
        using var fake = FakeCliInstallation.CreateEmpty("success");
        InstallUnstartableShim(fake);

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => CreateRunner().RunAsync(
            CreateRunnerRequest(fake, "input", TimeSpan.FromSeconds(30)),
            CancellationToken.None
        ));

        Assert.Equal(PluginRequestFailureKind.Configuration, error.FailureKind);
        // The launcher's diagnostic is the only clue when the interpreter is off the child's PATH.
        Assert.Contains("could not be started: ", error.Message, StringComparison.Ordinal);
        Assert.Contains("codex", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AvailabilityProbe_LogsTheStartDiagnosticWhenTheCliCannotLaunch()
    {
        using var fake = FakeCliInstallation.CreateEmpty("success");
        InstallUnstartableShim(fake);
        var logs = new ConcurrentQueue<string>();
        var host = CreateHost();
        host.Setup(service => service.Log(It.IsAny<PluginLogLevel>(), It.IsAny<string>()))
            .Callback((PluginLogLevel _, string message) => logs.Enqueue(message));
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(host.Object);

        await plugin.RefreshFromSettingsAsync();

        Assert.Equal(
            CliAvailabilityState.Error,
            plugin.GetSnapshot(Descriptor(CliProviderKind.Codex)).State
        );
        var line = logs.FirstOrDefault(entry =>
            entry.Contains("event=availability state=error", StringComparison.Ordinal));
        Assert.NotNull(line);
        Assert.Contains("could not be started: ", line, StringComparison.Ordinal);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task Runner_ReportsCrashWithoutTreatingOutputAsSuccess()
    {
        using var fake = FakeCliInstallation.Create("crash", "codex");

        var result = await CreateRunner().RunAsync(
            CreateRunnerRequest(fake, "input", TimeSpan.FromSeconds(30)),
            CancellationToken.None
        );

        Assert.Equal(42, result.ExitCode);
        Assert.Equal("", result.StandardOutput);
    }

    [Fact]
    public async Task Runner_RejectsOversizedOutput()
    {
        using var fake = FakeCliInstallation.Create("huge-output", "codex");

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => CreateRunner().RunAsync(
            CreateRunnerRequest(fake, "input", TimeSpan.FromSeconds(30)),
            CancellationToken.None
        ));

        Assert.Equal(PluginRequestFailureKind.Unknown, error.FailureKind);
        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task Runner_PreservesUserCancellation()
    {
        using var fake = FakeCliInstallation.Create("timeout", "codex");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateRunner().RunAsync(
            CreateRunnerRequest(fake, "input", TimeSpan.FromSeconds(60)),
            cancellation.Token
        ));
    }

    [Fact]
    public async Task Runner_TimesOutAndKillsTheChildProcessTree()
    {
        using var fake = FakeCliInstallation.Create("child", "codex");

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => CreateRunner().RunAsync(
            CreateRunnerRequest(fake, "input", TimeSpan.FromSeconds(10)),
            CancellationToken.None
        ));

        Assert.Equal(PluginRequestFailureKind.Timeout, error.FailureKind);
        var spawnedPid = int.Parse(
            await File.ReadAllTextAsync(Path.Join(fake.WorkingDirectory, "spawned.pid"))
        );
        Assert.True(await WaitForProcessExitAsync(spawnedPid, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Request_RemovesItsTemporaryDirectory()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        var before = ExistingTempDirectories();

        await GetRole(plugin, "authenticated-cli-codex").ProcessAsync(
            "Instruction",
            "Input",
            "default",
            CancellationToken.None
        );
        // The availability poll also creates and removes request directories, so the check waits
        // for it to stop rather than racing a probe that is still in flight.
        await plugin.DeactivateAsync();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (ExistingTempDirectories().Except(before, StringComparer.Ordinal).Any()
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Empty(ExistingTempDirectories().Except(before, StringComparer.Ordinal));
    }

    [Fact]
    public void Parsers_RequireProviderNativeTerminalEnvelopeAndExactLogicalSchema()
    {
        Assert.Equal(
            "codex",
            Descriptor(CliProviderKind.Codex).ParseSuccessfulOutput(
                "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"{\\\"text\\\":\\\"codex\\\"}\"}}\n{\"type\":\"turn.completed\"}"
            )
        );
        Assert.Equal(
            "claude",
            Descriptor(CliProviderKind.Claude).ParseSuccessfulOutput(
                "{\"type\":\"result\",\"subtype\":\"success\",\"structured_output\":{\"text\":\"claude\"}}"
            )
        );
        Assert.Throws<CliProtocolException>(() =>
            Descriptor(CliProviderKind.Claude).ParseSuccessfulOutput(
                "{\"type\":\"result\",\"subtype\":\"success\",\"structured_output\":{\"text\":\"value\",\"extra\":true}}"
            ));
        // No turn.completed: a truncated Codex stream is not a result.
        Assert.Throws<CliProtocolException>(() =>
            Descriptor(CliProviderKind.Codex).ParseSuccessfulOutput(
                "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"{\\\"text\\\":\\\"partial\\\"}\"}}"
            ));
    }

    [Fact]
    public void OpenCodeJsonlParser_UsesLastTextPartAndRejectsIncompleteOrExtraLogicalFields()
    {
        var descriptor = Descriptor(CliProviderKind.OpenCode);
        Assert.Equal(
            "last",
            descriptor.ParseSuccessfulOutput(
                "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"{\\\"text\\\":\\\"first\\\"}\"}}\n"
                + "{\"type\":\"reasoning\",\"part\":{\"type\":\"reasoning\"}}\n"
                + "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"{\\\"text\\\":\\\"last\\\"}\"}}"
            )
        );
        Assert.ThrowsAny<JsonException>(() => descriptor.ParseSuccessfulOutput("plain text"));
        Assert.Throws<CliProtocolException>(() => descriptor.ParseSuccessfulOutput(
            "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"{\\\"text\\\":\\\"ok\\\",\\\"extra\\\":true}\"}}"
        ));
        Assert.Throws<CliProtocolException>(() => descriptor.ParseSuccessfulOutput(
            "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"{\\\"text\\\":\\\"earlier\\\"}\"}}\n"
            + "{\"type\":\"text\",\"part\":{\"type\":\"text\"}}"
        ));
        Assert.ThrowsAny<JsonException>(() => descriptor.ParseSuccessfulOutput("{\"type\":\"text\""));
    }

    [Fact]
    public void OpenCodeFailureClassificationInput_UsesOnlyExplicitErrorFields()
    {
        var descriptor = Descriptor(CliProviderKind.OpenCode);
        Assert.Equal(
            "rate limit exceeded",
            descriptor.ExtractFailureText(
                "{\"type\":\"error\",\"error\":{\"data\":{\"message\":\"rate limit exceeded\"}}}",
                "prompt secret: authentication failed"
            )
        );
        Assert.Equal(
            "login required",
            descriptor.ExtractFailureText("", "{\"error\":{\"message\":\"login required\"}}")
        );
        Assert.Equal("", descriptor.ExtractFailureText("untrusted prompt text", "plain error"));
    }

    [Fact]
    public void OpenCodeCatalogParser_FiltersUnsafeEntriesAndChecksEveryCostNumber()
    {
        var output = string.Join(
            '\n',
            VerboseModel(
                "first-free",
                "First Free",
                cost: "{\"input\":0,\"output\":0,\"cache\":{\"read\":0},\"tiers\":[{\"input\":0,\"output\":0}]}",
                variants: "{\"safe\":{},\"bad value\":{}}"),
            VerboseModel("exponent-zero", "Exponent Zero", cost: "{\"input\":0e999999,\"output\":-0.0e-999999}"),
            VerboseModel("underflow-paid", "Underflow Paid", cost: "{\"input\":1e-400,\"output\":0}"),
            VerboseModel("tiny-paid", "Tiny Paid", cost: "{\"input\":1e-9,\"output\":0}"),
            VerboseModel("unknown-nested-cost", "Unknown Nested Cost", cost: "{\"input\":0,\"output\":0,\"cache\":{\"read\":\"0\"}}"),
            VerboseModel("cache-paid", "Cache Paid", cost: "{\"input\":0,\"output\":0,\"cache\":{\"read\":0.01}}"),
            VerboseModel("tier-paid", "Tier Paid", cost: "{\"input\":0,\"output\":0,\"tiers\":[{\"input\":1,\"output\":0}]}"),
            VerboseModel("direct-paid", "Direct Paid", cost: "{\"input\":1,\"output\":2}"),
            VerboseModel("deprecated", "Deprecated", cost: "{\"input\":0,\"output\":0}", status: "deprecated"),
            VerboseModel("wrong-provider", "Wrong Provider", cost: "{\"input\":0,\"output\":0}", providerId: "other"),
            VerboseModel("wrong-id", "Wrong ID", cost: "{\"input\":0,\"output\":0}", metadataId: "different"),
            VerboseModel("no-text-output", "No Text", cost: "{\"input\":0,\"output\":0}", outputModalities: "[\"image\"]"),
            VerboseModel("duplicate", "Duplicate A", cost: "{\"input\":0,\"output\":0}"),
            VerboseModel("duplicate", "Duplicate B", cost: "{\"input\":0,\"output\":0}")
        );

        var catalog = OpenCodeModelCatalogLoader.Parse(output, DateTimeOffset.UnixEpoch);

        Assert.Equal(
            [
                "opencode/first-free", "opencode/exponent-zero", "opencode/underflow-paid",
                "opencode/tiny-paid", "opencode/unknown-nested-cost", "opencode/cache-paid",
                "opencode/tier-paid", "opencode/direct-paid",
            ],
            catalog.Models.Select(model => model.Id)
        );
        Assert.True(catalog.Models[0].IsFree);
        Assert.True(catalog.Models[1].IsFree);
        Assert.Equal(["safe"], catalog.Models[0].Variants);
        Assert.All(catalog.Models.Skip(2), model => Assert.False(model.IsFree));
    }

    [Fact]
    public void OpenCodeCatalogParser_RejectsUnparseableCatalogAndSkipsMalformedEntry()
    {
        Assert.Throws<CliProtocolException>(() => OpenCodeModelCatalogLoader.Parse(
            "opencode/broken\n{not-json",
            DateTimeOffset.UnixEpoch
        ));

        var catalog = OpenCodeModelCatalogLoader.Parse(
            "opencode/broken\n{not-json\n"
            + VerboseModel("valid", "Valid", cost: "{\"input\":0,\"output\":0}"),
            DateTimeOffset.UnixEpoch
        );
        Assert.Equal("opencode/valid", Assert.Single(catalog.Models).Id);
    }

    [Theory]
    [InlineData("opencode-auth-ansi", (int)CliAvailabilityState.Ready)]
    [InlineData("opencode-auth-stderr", (int)CliAvailabilityState.Ready)]
    [InlineData("opencode-auth-missing", (int)CliAvailabilityState.AuthenticationUnknown)]
    [InlineData("opencode-auth-error", (int)CliAvailabilityState.SignedOut)]
    public async Task OpenCodeAuthentication_RequiresExplicitZenEntryFromCombinedAnsiStrippedOutput(
        string scenario,
        int expected
    )
    {
        using var fake = FakeCliInstallation.Create(scenario, "opencode");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);

        await plugin.RefreshFromSettingsAsync();

        Assert.Equal(
            (CliAvailabilityState)expected,
            plugin.GetSnapshot(Descriptor(CliProviderKind.OpenCode)).State
        );
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task OpenCodeProvider_UsesOnlyFreeCatalogModelsAndExactIsolatedInvocation()
    {
        using var fake = FakeCliInstallation.Create("success", "opencode");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        var host = CreateHost();
        await plugin.ActivateAsync(host.Object);
        await plugin.RefreshFromSettingsAsync();
        var role = GetRole(plugin, "authenticated-cli-opencode");
        var model = Assert.Single(role.SupportedModels);

        Assert.Equal("opencode/muse-spark-1.3-contributor-free", model.Id);
        Assert.Equal("Muse Spark 1.3 Free", model.DisplayName);
        Assert.True(model.IsRecommended);
        Assert.True(role.IsAvailable);
        host.Verify(service => service.SetSetting(
            AuthenticatedCliPlugin.OpenCodeCatalogSettingName,
            It.Is<OpenCodeModelCatalogCache>(cache =>
                cache.Version == 1
                && cache.Models.Count == 1
                && cache.Models[0].Id == model.Id)), Times.AtLeastOnce);

        string result;
        using (new TemporaryEnvironment(
                   ("OPENAI_API_KEY", "must-not-leak"),
                   ("ANTHROPIC_API_KEY", "must-not-leak")))
        {
            result = await role.ProcessAsync(
                "Instruction",
                "Synthetic input",
                model.Id,
                CancellationToken.None
            );
        }

        Assert.Equal("processed", result);
        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(fake.CapturePath));
        var root = capture.RootElement;
        var arguments = root.GetProperty("arguments").EnumerateArray()
            .Select(argument => argument.GetString()!)
            .ToList();
        Assert.Equal(
            [
                "run", "--pure", "--format", "json", "--title", "TypeWhisper",
                "--dir", root.GetProperty("workingDirectory").GetString()!,
                "--agent", "typewhisper", "--model", model.Id,
            ],
            arguments
        );
        Assert.DoesNotContain("--variant", arguments);
        Assert.DoesNotContain("--continue", arguments);
        Assert.DoesNotContain("--share", arguments);
        var environment = root.GetProperty("environment");
        Assert.Equal("typewhisper", environment.GetProperty("OPENCODE_CLIENT").GetString());
        Assert.False(environment.TryGetProperty("OPENAI_API_KEY", out _));
        Assert.False(environment.TryGetProperty("ANTHROPIC_API_KEY", out _));
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task OpenCodeWithNoFreeModels_IsUnavailableAndNeverFallsBackToDefault()
    {
        using var fake = FakeCliInstallation.Create("catalog-none", "opencode");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        await plugin.RefreshFromSettingsAsync();
        var role = GetRole(plugin, "authenticated-cli-opencode");

        Assert.Equal(
            CliAvailabilityState.NoFreeModels,
            plugin.GetSnapshot(Descriptor(CliProviderKind.OpenCode)).State
        );
        Assert.False(role.IsAvailable);
        Assert.Empty(role.SupportedModels);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task OpenCodeCatalogFailure_KeepsValidatedCachedFreeListVisible()
    {
        using var fake = FakeCliInstallation.Create("catalog-fail", "opencode");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        var host = CreateHost();
        host.Setup(service => service.GetSetting<OpenCodeModelCatalogCache>(
                AuthenticatedCliPlugin.OpenCodeCatalogSettingName))
            .Returns(new OpenCodeModelCatalogCache(
                1,
                DateTimeOffset.UnixEpoch,
                [new OpenCodeCachedModel("opencode/cached-free", "Cached Free", [])]
            ));
        await plugin.ActivateAsync(host.Object);

        await plugin.RefreshFromSettingsAsync();

        var role = GetRole(plugin, "authenticated-cli-opencode");
        Assert.True(role.IsAvailable);
        Assert.Equal("opencode/cached-free", Assert.Single(role.SupportedModels).Id);
        Assert.True(plugin.GetOpenCodeCatalogStatus().IsLastKnownGood);
        Assert.NotNull(plugin.GetOpenCodeCatalogStatus().LastRefreshError);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task OpenCodeCatalogFailureWithoutCache_IsUnavailableAndActionable()
    {
        using var fake = FakeCliInstallation.Create("catalog-fail", "opencode");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);

        await plugin.RefreshFromSettingsAsync();

        var role = GetRole(plugin, "authenticated-cli-opencode");
        Assert.Equal(
            CliAvailabilityState.ModelCatalogUnavailable,
            plugin.GetSnapshot(Descriptor(CliProviderKind.OpenCode)).State
        );
        Assert.False(role.IsAvailable);
        Assert.Empty(role.SupportedModels);
        await plugin.DeactivateAsync();
    }

    [Theory]
    [InlineData("default")]
    [InlineData("anthropic/claude")]
    [InlineData("opencode/paid-model")]
    [InlineData("opencode/stale-free")]
    public async Task OpenCodeRejectsNonCurrentOrPaidModelBeforeRequestLaunch(string model)
    {
        using var fake = FakeCliInstallation.Create("success", "opencode");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        await plugin.RefreshFromSettingsAsync();
        var role = GetRole(plugin, "authenticated-cli-opencode");

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => role.ProcessAsync(
            "Instruction",
            "Input",
            model,
            CancellationToken.None
        ));

        Assert.Equal(PluginRequestFailureKind.InvalidRequest, error.FailureKind);
        Assert.False(error.IsTransient);
        Assert.False(File.Exists(fake.CapturePath));
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_RefreshesOnlyTheOpenCodeCatalog()
    {
        using var fake = FakeCliInstallation.Create("success", "opencode");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);

        await plugin.RefreshModelCatalogAsync();

        Assert.Equal(
            "opencode/muse-spark-1.3-contributor-free",
            Assert.Single(GetRole(plugin, "authenticated-cli-opencode").SupportedModels).Id
        );
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task NestedRefreshFromCapabilityNotification_DoesNotDeadlock()
    {
        using var fake = FakeCliInstallation.Create("success", "opencode");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        var host = CreateHost();
        var nestedRefreshes = 0;
        host.Setup(service => service.NotifyCapabilitiesChanged()).Callback(() =>
        {
            // ReSharper disable once AccessToDisposedClosure
            // The callback only runs while the refresh below is in flight, well before disposal.
            if (Interlocked.Increment(ref nestedRefreshes) == 1)
            {
                plugin.RefreshFromSettingsAsync().Wait(TimeSpan.FromSeconds(30));
            }
        });
        await plugin.ActivateAsync(host.Object);

        await plugin.RefreshFromSettingsAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(nestedRefreshes > 0);
        Assert.Equal(
            "opencode/muse-spark-1.3-contributor-free",
            Assert.Single(GetRole(plugin, "authenticated-cli-opencode").SupportedModels).Id
        );
        await plugin.DeactivateAsync();
    }

    [Fact]
    public void Providers_ExposeThreeStableRolesWithSelectionIds()
    {
        using var plugin = new AuthenticatedCliPlugin();

        Assert.Equal(3, plugin.AdditionalLlmProviders.Count);
        Assert.Same(plugin.AdditionalLlmProviders, plugin.AdditionalLlmProviders);
        Assert.Equal(
            ["authenticated-cli-codex", "authenticated-cli-claude", "authenticated-cli-opencode"],
            plugin.AdditionalLlmProviders
                .Select(provider => ((ILlmProviderSelectionIdentity)provider).LlmSelectionId)
        );
        Assert.Equal("opencode", Descriptor(CliProviderKind.OpenCode).ExecutableName);
    }

    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(Path.Join(PluginDirectory(), "manifest.json")),
            s_manifestJsonOptions
        );

        using var plugin = new AuthenticatedCliPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, plugin.PluginVersion);
        Assert.Equal("com.typewhisper.authenticated-cli", plugin.PluginId);
    }

    [Fact]
    public async Task Settings_ExposeOneInstallationDropdownPerProviderAndRoundTripSelections()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        var host = CreateHost();
        await plugin.ActivateAsync(host.Object);
        await plugin.RefreshFromSettingsAsync();

        var definitions = plugin.GetSettingDefinitions();
        // Every row is a real control: guidance lives in the descriptions, not in text boxes
        // whose values the plugin would discard.
        Assert.Equal(
            ["codexInstallation", "claudeInstallation", "opencodeInstallation", "opencodeModel"],
            definitions.Select(definition => definition.Key)
        );
        Assert.All(definitions, definition =>
            Assert.Equal(PluginSettingKind.Dropdown, definition.Kind));
        var codex = definitions[0];
        Assert.Equal("Codex CLI", codex.Label);
        Assert.Contains("Ready", codex.Description!, StringComparison.Ordinal);
        Assert.Equal("", codex.Options![0].Value);
        Assert.Equal(fake.ExecutablePath("codex"), codex.Options[1].Value);
        Assert.Contains(
            "https://developers.openai.com/codex/cli/",
            codex.Description!,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "Privacy warning",
            definitions[3].Description!,
            StringComparison.Ordinal
        );

        var codexPath = fake.ExecutablePath("codex");
        await plugin.SetSettingValueAsync("codexInstallation", codexPath, CancellationToken.None);
        Assert.Equal(
            codexPath,
            await plugin.GetSettingValueAsync("codexInstallation", CancellationToken.None)
        );
        host.Verify(
            service => service.SetSetting("codexInstallation", codexPath),
            Times.AtLeastOnce
        );

        await plugin.SetSettingValueAsync("opencodeModel", "opencode/free", CancellationToken.None);
        Assert.Equal(
            "opencode/free",
            await plugin.GetSettingValueAsync("opencodeModel", CancellationToken.None)
        );
        Assert.Null(await plugin.GetSettingValueAsync("unknownKey", CancellationToken.None));
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task ValidateAsync_SucceedsWhenAnyProviderIsReadyAndAggregatesEveryStatus()
    {
        using var fake = FakeCliInstallation.Create("success", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);

        var result = await plugin.ValidateAsync();

        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.Contains("Codex CLI: Ready", result.Message, StringComparison.Ordinal);
        Assert.Contains("Claude Code CLI: Not installed", result.Message, StringComparison.Ordinal);
        Assert.Contains("OpenCode Zen: Not installed", result.Message, StringComparison.Ordinal);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task ValidateAsync_FailsWhenNoProviderIsReady()
    {
        using var fake = FakeCliInstallation.Create("signed-out", "codex");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);

        var result = await plugin.ValidateAsync();

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Contains(
            "Codex CLI: Installed, but not signed in",
            result.Message,
            StringComparison.Ordinal
        );
        await plugin.DeactivateAsync();
    }

    [Fact]
    public void Localization_ShipsEveryKeyInEveryLocaleWithRealTranslations()
    {
        var englishKeys = JsonDocument
            .Parse(File.ReadAllText(Path.Join(PluginDirectory(), "Localization", "en.json")))
            .RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToList();
        Assert.Contains("Manifest.Name", englishKeys);
        Assert.Contains("Manifest.Description", englishKeys);

        foreach (var locale in new[] { "en", "de", "es", "ru" })
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Join(PluginDirectory(), "Localization", locale + ".json"))
            );
            var localization = new PluginLocalization(PluginDirectory(), locale);
            foreach (var key in englishKeys)
            {
                Assert.True(document.RootElement.TryGetProperty(key, out var value), $"{locale}:{key}");
                Assert.False(string.IsNullOrWhiteSpace(value.GetString()), $"{locale}:{key}");
                Assert.NotEqual(key, localization.GetString(key));
            }
        }

        var en = new PluginLocalization(PluginDirectory(), "en");
        foreach (var locale in new[] { "de", "es", "ru" })
        {
            var translated = new PluginLocalization(PluginDirectory(), locale);
            Assert.NotEqual(en.GetString("Settings.Checking"), translated.GetString("Settings.Checking"));
            Assert.NotEqual(en.GetString("State.Ready"), translated.GetString("State.Ready"));
            Assert.NotEqual(
                en.GetString("Manifest.Description"),
                translated.GetString("Manifest.Description")
            );
        }
    }

    private static string VerboseModel(
        string headerId,
        string name,
        string cost,
        string status = "active",
        string providerId = "opencode",
        string? metadataId = null,
        string inputModalities = "[\"text\"]",
        string outputModalities = "[\"text\"]",
        string variants = "{}"
    ) =>
        $"opencode/{headerId}\n{{\"id\":{JsonSerializer.Serialize(metadataId ?? headerId)},"
        + $"\"providerID\":{JsonSerializer.Serialize(providerId)},\"name\":{JsonSerializer.Serialize(name)},"
        + $"\"status\":{JsonSerializer.Serialize(status)},\"modalities\":{{\"input\":{inputModalities},\"output\":{outputModalities}}},"
        + $"\"cost\":{cost},\"variants\":{variants}}}";

    private static AuthenticatedCliPlugin CreatePlugin(string processPath) =>
        new(new CliExecutableDiscovery(() => processPath), CreateRunner());

    private static CliProcessRunner CreateRunner() =>
        new(new PluginProcessSupervisorScope(
            "com.typewhisper.authenticated-cli",
            new ProcessRunner()
        ));

    private static Mock<IPluginHostServices> CreateHost()
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(service => service.GetSetting<string>(It.IsAny<string>())).Returns((string?)null);
        host.SetupGet(service => service.Localization)
            .Returns(new PluginLocalization(PluginDirectory(), "en"));
        return host;
    }

    private static ILlmProviderRole GetRole(AuthenticatedCliPlugin plugin, string selectionId) =>
        plugin.AdditionalLlmProviders.Single(provider =>
            string.Equals(
                ((ILlmProviderSelectionIdentity)provider).LlmSelectionId,
                selectionId,
                StringComparison.Ordinal
            ));

    private static CliProviderDescriptor Descriptor(CliProviderKind kind) =>
        CliProviderDescriptor.All.Single(descriptor => descriptor.Kind == kind);

    private static CliProcessRequest CreateRunnerRequest(
        FakeCliInstallation fake,
        string standardInput,
        TimeSpan timeout
    ) =>
        new(
            fake.ExecutablePath("codex"),
            ["exec", "--json", "-"],
            standardInput,
            fake.WorkingDirectory,
            ["CODEX_HOME"],
            timeout,
            AuthenticatedCliPlugin.MaximumStandardOutputBytes,
            AuthenticatedCliPlugin.MaximumStandardErrorBytes
        );

    /// <summary>
    ///     A codex shim whose interpreter does not exist, so the launch itself fails: the npm-global
    ///     shape on a box whose node lives outside the child's forced PATH.
    /// </summary>
    private static void InstallUnstartableShim(FakeCliInstallation fake)
    {
        var shimPath = fake.ExecutablePath("codex");
        File.WriteAllText(shimPath, "#!/typewhisper/missing/interpreter\n");
        SetUnixMode(
            shimPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        );
    }

    private static string[] ExistingTempDirectories()
    {
        var root = Path.Join(Path.GetTempPath(), "TypeWhisper", "AuthenticatedCli");
        return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
    }

    private static async Task<bool> WaitForProcessExitAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    /// <summary>Sets environment variables for the duration of a test and restores them after.</summary>
    private sealed class TemporaryEnvironment : IDisposable
    {
        private readonly (string Name, string? Value)[] _previous;

        internal TemporaryEnvironment(params (string Name, string Value)[] variables)
        {
            _previous = variables
                .Select(variable =>
                    (variable.Name, Environment.GetEnvironmentVariable(variable.Name)))
                .ToArray();
            foreach (var (name, value) in variables)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private static void SetUnixMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    private static UnixFileMode GetUnixMode(string path) =>
        OperatingSystem.IsWindows()
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : File.GetUnixFileMode(path);

    private static string PluginDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Join(
            Path.GetDirectoryName(thisFile)!,
            "..",
            "..",
            "plugins",
            "TypeWhisper.Plugin.AuthenticatedCli"
        ));

    /// <summary>
    ///     A PATH directory holding shell shims named after the real CLIs. Each shim execs the
    ///     FakeProviderCli assembly and exports which provider and scenario it is playing, because
    ///     the plugin's environment allow-list strips anything the test process sets.
    /// </summary>
    private sealed class FakeCliInstallation : IDisposable
    {
        private readonly string _scenario;

        private FakeCliInstallation(string scenario)
        {
            _scenario = scenario;
            RootPath = Path.Join(
                Path.GetTempPath(),
                "TypeWhisperAuthenticatedCliTests",
                Guid.NewGuid().ToString("N")
            );
            DirectoryPath = Path.Join(RootPath, "bin");
            WorkingDirectory = Path.Join(RootPath, "working");
            Directory.CreateDirectory(DirectoryPath);
            Directory.CreateDirectory(WorkingDirectory);
        }

        internal string RootPath { get; }
        internal string DirectoryPath { get; }
        internal string WorkingDirectory { get; }
        internal string CapturePath => Path.Join(DirectoryPath, "capture.json");

        internal static FakeCliInstallation Create(string scenario, string executableName)
        {
            var installation = new FakeCliInstallation(scenario);
            installation.Install(executableName);
            return installation;
        }

        internal static FakeCliInstallation CreateEmpty(string scenario) => new(scenario);

        internal void Install(string executableName)
        {
            var shimPath = ExecutablePath(executableName);
            File.WriteAllText(
                shimPath,
                $"""
                 #!/bin/sh
                 export TYPEWHISPER_FAKE_PROVIDER='{executableName}'
                 export TYPEWHISPER_FAKE_SCENARIO='{_scenario}'
                 export TYPEWHISPER_FAKE_DIR='{DirectoryPath}'
                 export TYPEWHISPER_FAKE_SHIM='{shimPath}'
                 exec '{DotnetPath()}' '{typeof(FakeProviderCliMarker).Assembly.Location}' "$@"

                 """
            );
            SetUnixMode(
                shimPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
            );
        }

        internal string ExecutablePath(string executableName) =>
            Path.Join(DirectoryPath, executableName);

        // The shim runs with the plugin's forced PATH, which does not include a user-local SDK,
        // so the muxer is baked in by absolute path.
        private static string DotnetPath()
        {
            if (Environment.ProcessPath is { } processPath
                && string.Equals(Path.GetFileName(processPath), "dotnet", StringComparison.Ordinal))
            {
                return processPath;
            }

            if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { } root
                && File.Exists(Path.Join(root, "dotnet")))
            {
                return Path.Join(root, "dotnet");
            }

            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Join(directory, "dotnet");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException("Could not locate the dotnet muxer for the CLI shim.");
        }

        public void Dispose()
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (Directory.Exists(RootPath))
                    {
                        Directory.Delete(RootPath, recursive: true);
                    }

                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt < 9)
                    {
                        Thread.Sleep(50);
                    }
                }
            }
        }
    }
}
