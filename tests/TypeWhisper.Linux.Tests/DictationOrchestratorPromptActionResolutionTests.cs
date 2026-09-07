using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Coverage for the B13 auto-pipeline filter on
///     <see cref="DictationOrchestrator.ResolveAutoPromptAction" />. Manual-only
///     actions must not be reachable from a Profile binding even when their
///     Id matches.
/// </summary>
public sealed class DictationOrchestratorPromptActionResolutionTests
{
    [Fact]
    public void ResolveAutoPromptAction_ReturnsActionWhenNotManualOnly()
    {
        var action = new PromptAction
        {
            Id = "auto",
            Name = "Auto",
            SystemPrompt = "x",
        };

        var resolved = DictationOrchestrator.ResolveAutoPromptAction("auto", [action]);

        Assert.Same(action, resolved);
    }

    [Fact]
    public void ResolveAutoPromptAction_ReturnsNullWhenManualOnly()
    {
        var action = new PromptAction
        {
            Id = "manual",
            Name = "Manual",
            SystemPrompt = "x",
            IsManualOnly = true,
        };

        var resolved = DictationOrchestrator.ResolveAutoPromptAction("manual", [action]);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveAutoPromptAction_ReturnsNullWhenIdMissing()
    {
        var resolved = DictationOrchestrator.ResolveAutoPromptAction(null, []);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveAutoPromptAction_ReturnsNullWhenIdDoesNotMatch()
    {
        var action = new PromptAction
        {
            Id = "other",
            Name = "Other",
            SystemPrompt = "x",
        };

        var resolved = DictationOrchestrator.ResolveAutoPromptAction("missing", [action]);

        Assert.Null(resolved);
    }
}
