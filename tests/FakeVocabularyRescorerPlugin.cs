using TypeWhisper.PluginSDK;

namespace TypeWhisper.Tests;

internal sealed class FakeVocabularyRescorerPlugin : IVocabularyRescorerPlugin
{
    public string PluginId => "test-vocabulary";
    public string PluginName => "Test vocabulary";
    public string PluginVersion => "1.0.0";
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global -- file-linked into two test projects; the Linux tests set it.
    public bool IsReady { get; set; } = true;
    public int CallCount { get; private set; }
    public VocabularyRescoreRequest? Request { get; private set; }
    // ReSharper disable once UnusedAutoPropertyAccessor.Global -- file-linked into two test projects; the Linux tests read it.
    public CancellationToken Token { get; private set; }
    public Func<
        VocabularyRescoreRequest,
        CancellationToken,
        Task<VocabularyRescoreResult>
    > Handler { get; set; } =
        (request, _) =>
            Task.FromResult(
                new VocabularyRescoreResult(
                    request.RecordingId,
                    [new VocabularyReplacement(0, request.Text.Length, "TypeWhisper", 1)]
                )
            );

    public Task<VocabularyRescoreResult> RescoreAsync(
        VocabularyRescoreRequest request,
        CancellationToken cancellationToken
    )
    {
        CallCount++;
        Request = request;
        Token = cancellationToken;
        return Handler(request, cancellationToken);
    }

    public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;

    public Task DeactivateAsync() => Task.CompletedTask;

    public void Dispose() { }
}
