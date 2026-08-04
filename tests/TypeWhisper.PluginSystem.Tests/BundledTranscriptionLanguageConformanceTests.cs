extern alias SherpaOnnx;

using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.Plugin.AssemblyAi;
using TypeWhisper.Plugin.CloudflareAsr;
using TypeWhisper.Plugin.Deepgram;
using TypeWhisper.Plugin.ElevenLabs;
using TypeWhisper.Plugin.Gladia;
using TypeWhisper.Plugin.GoogleCloudStt;
using TypeWhisper.Plugin.Groq;
using TypeWhisper.Plugin.OpenAi;
using TypeWhisper.Plugin.OpenAiCompatible;
using TypeWhisper.Plugin.OpenRouter;
using TypeWhisper.Plugin.Qwen3Stt;
using TypeWhisper.Plugin.Reson8;
using TypeWhisper.Plugin.SmallestAi;
using TypeWhisper.Plugin.Soniox;
using TypeWhisper.Plugin.Speechmatics;
using TypeWhisper.Plugin.Voxtral;
using TypeWhisper.Plugin.WhisperCpp;
using TypeWhisper.Plugin.Xai;
using SherpaOnnxPlugin = SherpaOnnx::TypeWhisper.Plugin.SherpaOnnx.SherpaOnnxPlugin;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class BundledTranscriptionLanguageConformanceTests
{
    private static readonly string[] s_automaticInputs = [" auto ", "AUTO", "auto"];
    private static readonly string[] s_invalidInputs = ["zz-QQ-!!", "notalang"];
    private static readonly (string Raw, string Canonical)[] s_explicitInputs =
    [
        ("en", "en"),
        ("de-DE", "de-DE"),
        ("zh-Hans-CN", "zh-Hans-CN"),
    ];

    [Fact]
    public void EveryBundledRole_ConformsToLanguageSelectionMatrix()
    {
        var roles = CreateBundledRoles();
        try
        {
            Assert.Equal(19, roles.Count);
            Assert.Equal(19, roles.Select(role => role.ProviderId).Distinct().Count());

            // Role-independent, so assert once rather than 19 times inside the loop below.
            Assert.False(LanguageSelection.TryParse("", out _));
            Assert.False(LanguageSelection.TryParse("   ", out _));
            foreach (var raw in s_invalidInputs)
            {
                Assert.False(LanguageSelection.TryParse(raw, out _));
            }

            foreach (var role in roles)
            {
                var capabilities = Assert.IsType<ITranscriptionLanguageSelectionCapabilities>(
                    role,
                    exactMatch: false
                );
                Assert.NotEqual(
                    LanguageSelectionSupport.Unknown,
                    capabilities.AutomaticDetectionSupport
                );
                Assert.NotEqual(
                    LanguageSelectionSupport.Unknown,
                    capabilities.ExplicitSelectionSupport
                );

                foreach (var raw in s_automaticInputs)
                {
                    Assert.True(LanguageSelection.TryParse(raw, out var selection));
                    Assert.True(selection.IsAutomatic);
                    AssertConversion(
                        role,
                        selection,
                        capabilities.AutomaticDetectionSupport,
                        null
                    );
                }

                foreach (var (raw, canonical) in s_explicitInputs)
                {
                    Assert.True(LanguageSelection.TryParse(raw, out var selection));
                    AssertConversion(
                        role,
                        selection,
                        capabilities.ExplicitSelectionSupport,
                        canonical
                    );
                }
            }
        }
        finally
        {
            foreach (var disposable in roles.OfType<IDisposable>())
            {
                disposable.Dispose();
            }
        }
    }

    [Fact]
    public void ModelDependentRoles_AdvertiseCurrentModelCapabilities()
    {
        // Deepgram accepts automatic detection on every model, so switching
        // models must not start rejecting it.
        using var deepgram = new DeepgramPlugin();
        foreach (var model in new[] { "nova-3", "nova-2" })
        {
            deepgram.SelectModel(model);
            Assert.Equal(
                LanguageSelectionSupport.Supported,
                deepgram.AutomaticDetectionSupport
            );
            Assert.Null(deepgram.ToLegacyLanguage(LanguageSelection.Automatic));
        }

        using var sherpa = new SherpaOnnxPlugin();
        sherpa.SelectModel("parakeet-tdt-0.6b");
        Assert.Equal(
            LanguageSelectionSupport.Supported,
            sherpa.AutomaticDetectionSupport
        );
        Assert.Equal(
            LanguageSelectionSupport.Unsupported,
            sherpa.ExplicitSelectionSupport
        );
        sherpa.SelectModel("canary-180m-flash");
        AssertNoDetection(sherpa);
        Assert.Equal(
            LanguageSelectionSupport.Supported,
            sherpa.ExplicitSelectionSupport
        );
    }

    private static void AssertNoDetection(ITranscriptionEngineRole role)
    {
        var capabilities = Assert.IsType<ITranscriptionLanguageSelectionCapabilities>(
            role,
            exactMatch: false
        );
        Assert.Equal(
            LanguageSelectionSupport.Unsupported,
            capabilities.AutomaticDetectionSupport
        );
        Assert.Throws<LanguageSelectionNotSupportedException>(
            () => role.ToLegacyLanguage(LanguageSelection.Automatic)
        );
    }

    private static void AssertConversion(
        ITranscriptionEngineRole role,
        LanguageSelection selection,
        LanguageSelectionSupport support,
        string? expected
    )
    {
        if (support == LanguageSelectionSupport.Unsupported)
        {
            Assert.Throws<LanguageSelectionNotSupportedException>(
                () => role.ToLegacyLanguage(selection)
            );
            return;
        }

        Assert.Equal(expected, role.ToLegacyLanguage(selection));
    }

    private static List<ITranscriptionEngineRole> CreateBundledRoles()
    {
        var roles = new List<ITranscriptionEngineRole>
        {
            new AssemblyAiPlugin(),
            new CloudflareAsrPlugin(),
            new DeepgramPlugin(),
            new ElevenLabsPlugin(),
            new GladiaPlugin(),
            new GoogleCloudSttPlugin(),
            new GroqPlugin(),
            new OpenAiPlugin(),
            new OpenAiCompatiblePlugin(),
            new OpenRouterPlugin(),
            new Qwen3SttPlugin(),
            new Reson8Plugin(),
            new SherpaOnnxPlugin(),
            new SmallestAiPlugin(),
            new SonioxPlugin(),
            new SpeechmaticsPlugin(),
            new VoxtralPlugin(),
            new WhisperCppPlugin(),
            new XaiPlugin(),
        };

        foreach (var role in roles.Where(role => role.SelectedModelId is null))
        {
            if (role.TranscriptionModels.Count > 0)
            {
                role.SelectModel(role.TranscriptionModels[0].Id);
            }
        }

        return roles;
    }
}
