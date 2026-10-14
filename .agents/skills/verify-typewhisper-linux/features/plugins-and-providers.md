# Plugins and providers

## Sub-features

- The app bundles 38 plugins beside its binary and copies them into its data folder on first start.
- Each plugin is enabled or disabled in `pluginEnabledState`; enabled plugins activate at startup.
- Transcription engines (Sherpa, Whisper.cpp, Whistle, cloud ASR) and LLM providers (OpenAI-compatible, OpenRouter, Gemini, and others) register with the plugin manager.
- Settings, then Prompts, selects the cleanup LLM (`defaultLlmProvider`, written as `plugin:<plugin id>:<model>`). Each plugin keeps its own settings in `PluginData/<plugin id>/settings.json`.

## How to get to it (user POV)

Settings, then Plugins: enable a plugin and fill its settings, such as a base URL, API key, or model. Settings, then Prompts: choose that provider as the LLM for cleanup. Settings, then Dictation: choose a transcription model from an enabled engine.

## Driving it with an isolated instance

Write the plugin's enabled state and settings into the isolated instance before launch, then prove that the app activated it and routes work to it. `FakeLlm.plugin_overrides()` produces both for the OpenAI-compatible plugin:

```bash
just build-release
just eval-capture artifacts/evals/$(date +%F)-provider/results.jsonl
```

Check, in order:

1. `<out>-instance/app.log` lists `Discovered 38 plugin(s)`, `[Plugin:com.typewhisper.openai-compatible] [Info] Activated (configured=True, ...)`, and `Activated plugin: com.typewhisper.openai-compatible`.
2. `<out>-run.json` shows one cleanup request per case (`llm_requests` equals `case_count`), a short `provider_wait_seconds`, and no `llm_rejected_paths`.
3. Each result row names `cleanup_provider` `com.typewhisper.openai-compatible` and `cleanup_model` `fixture-cleanup`.

For a transcription engine, pass its plugin ID to `IsolatedApp(plugin_data=[...])`; this copies its model folder from the user's `PluginData` and enables it. Then `IsolatedApp.load_model(engine, model)` must succeed, and `/v1/status` must report the expected `acceleration.active_backend`. `just live-smoke` does this for `com.typewhisper.sherpa-onnx` with `parakeet-tdt-0.6b`.

Proven on 2026-10-14: provider activation and 30 of 30 routed cleanup calls (`artifacts/evals/2026-10-14-text-capture-3/`), and Sherpa model load with live dictation (`artifacts/verification/2026-10-14-launcher/live-smoke-7/`).

## Gotchas

- The provider registers about 1 s after the API answers. A cleanup request in that window silently falls back to Light; wait for a canary request to reach the provider.
- `llm_calls[].succeeded` is true only for a nonempty completed response. A call that appears without it is an attempt, not a cleanup.
- Real provider credentials are encrypted with the user's `secret-protection.key`. Copying plugin settings without that key leaves the provider unconfigured; copying the key puts a credential in the instance folder. The eval thread's `dictation_evals.py capture-real` keeps such copies in a private temporary instance that is removed on exit.
- Installing or updating plugins from the online catalog is not covered here; it needs network access and changes the plugin folder.
- The rendered UI tests do not load plugins, so the Plugins section renders empty there.
- `PluginSystem` tests have 17 known OpenAI plugin failures on this branch; compare against that baseline before calling a plugin change a regression.
