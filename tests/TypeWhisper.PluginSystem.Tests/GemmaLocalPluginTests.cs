using System.Collections.Immutable;
using TypeWhisper.Plugin.GemmaLocal;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class GemmaLocalPluginTests
{
    private const string ModelA = "gemma4-e2b-it-q4";
    private const string ModelB = "gemma4-e4b-it-q4";

    [Fact]
    public void SupportedModels_NoActiveModel_IsEmptyAndImmutable()
    {
        using var sut = new GemmaLocalPlugin();

        var models = sut.SupportedModels;

        Assert.Empty(models);
        Assert.IsType<ImmutableArray<PluginModelInfo>>(models);
    }

    [Fact]
    public void SupportedModels_ActiveModel_ContainsExactlyActiveModel()
    {
        using var sut = new GemmaLocalPlugin(
            ModelA,
            GemmaLocalPlugin.EnsureRequestedModelIsActive
        );

        var model = Assert.Single(sut.SupportedModels);

        Assert.Equal(ModelA, model.Id);
        Assert.IsType<ImmutableArray<PluginModelInfo>>(sut.SupportedModels);
    }

    [Fact]
    public void EnsureRequestedModelIsActive_MatchingModel_IsAccepted()
    {
        var exception = Record.Exception(
            () => GemmaLocalPlugin.EnsureRequestedModelIsActive(ModelA, ModelA)
        );

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureRequestedModelIsActive_MismatchedModel_ThrowsWithBothModelIds()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => GemmaLocalPlugin.EnsureRequestedModelIsActive(ModelB, ModelA)
        );

        Assert.Equal(
            $"Requested Gemma model '{ModelB}' does not match the active Gemma model '{ModelA}'.",
            exception.Message
        );
    }

    [Fact]
    public void EnsureRequestedModelIsActive_UnknownModel_ThrowsWithRequestedAndActiveIds()
    {
        const string unknownModel = "not-a-gemma-model";

        var exception = Assert.Throws<InvalidOperationException>(
            () => GemmaLocalPlugin.EnsureRequestedModelIsActive(unknownModel, ModelA)
        );

        Assert.Equal(
            $"Requested Gemma model '{unknownModel}' is unknown; "
                + $"the active Gemma model is '{ModelA}'.",
            exception.Message
        );
    }

    [Fact]
    public void EnsureRequestedModelIsActive_NoActiveModel_ThrowsWithRequestedAndNoActiveId()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => GemmaLocalPlugin.EnsureRequestedModelIsActive(ModelA, null)
        );

        Assert.Equal(
            $"Requested Gemma model '{ModelA}' cannot run because "
                + "the active Gemma model is '(none)'.",
            exception.Message
        );
    }

    [Fact]
    public async Task ProcessAsync_InvokesRoutingGuardBeforeNativeInference()
    {
        var observation = new RoutingObservation();
        using var sut = CreatePluginWithRoutingProbe(observation);

        await Assert.ThrowsAsync<RoutingGuardObservedException>(
            () => sut.ProcessAsync("system", "user", ModelB, CancellationToken.None)
        );

        Assert.Equal(ModelB, observation.RequestedModelId);
        Assert.Equal(ModelA, observation.ActiveModelId);
        Assert.Equal(1, observation.CallCount);
    }

    [Fact]
    public async Task ProcessStreamingAsync_InvokesRoutingGuardBeforeNativeInference()
    {
        var observation = new RoutingObservation();
        using var sut = CreatePluginWithRoutingProbe(observation);

        await Assert.ThrowsAsync<RoutingGuardObservedException>(async () =>
        {
            await foreach (
                var _ in sut.ProcessStreamingAsync(
                    "system",
                    "user",
                    ModelB,
                    CancellationToken.None
                )
            ) { }
        });

        Assert.Equal(ModelB, observation.RequestedModelId);
        Assert.Equal(ModelA, observation.ActiveModelId);
        Assert.Equal(1, observation.CallCount);
    }

    [Fact]
    public async Task ProcessStreamingAsync_WhenStreamingDisabled_DelegatesToGuardedBatchPath()
    {
        var observation = new RoutingObservation();
        using var sut = CreatePluginWithRoutingProbe(observation);
        sut.SetStreamResponses(false);

        await Assert.ThrowsAsync<RoutingGuardObservedException>(async () =>
        {
            await foreach (
                var _ in sut.ProcessStreamingAsync(
                    "system",
                    "user",
                    ModelB,
                    CancellationToken.None
                )
            ) { }
        });

        Assert.Equal(ModelB, observation.RequestedModelId);
        Assert.Equal(ModelA, observation.ActiveModelId);
        Assert.Equal(1, observation.CallCount);
    }

    private static GemmaLocalPlugin CreatePluginWithRoutingProbe(
        RoutingObservation observation
    ) =>
        new(
            ModelA,
            (requestedModelId, activeModelId) =>
            {
                observation.RequestedModelId = requestedModelId;
                observation.ActiveModelId = activeModelId;
                observation.CallCount++;
                throw new RoutingGuardObservedException();
            }
        );

    private sealed class RoutingObservation
    {
        public string? RequestedModelId { get; set; }
        public string? ActiveModelId { get; set; }
        public int CallCount { get; set; }
    }

    private sealed class RoutingGuardObservedException : Exception;
}
