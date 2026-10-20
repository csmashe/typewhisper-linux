# PR 138 verification

The work started at origin/feat/openrouter-catalog-refresh with lease
`78ae0b6dc19a0342baf6739a3caddad242606482`. The three original PR commits were
rebased onto upstream/linux `e6be760bce3399e3541f897fe0c27ef09731eeb5`.
No fork linux commits were added to the branch.

The rebased original commits are `f0333bb6`, `a64daea1`, and `d5bb5ba1`.
The first commit's range-diff includes the upstream test helper signature change.
The other two patches are unchanged.

The Release build passed with zero warnings and zero errors. The full Core,
CLI, Integration, PluginSystem, and Linux test projects were run. The logs record
the commands and results. Opt-in model and user-data checks remain skipped.
The branch does not contain the newer TypeWhisper.UiTests project. The real
Settings check below supplies rendered UI evidence for this change.

Tests were written before production changes. `row-rebuild-before.log` records
the failing row-retention test before its fix. That test also checks collection
draft saving, edits made during refresh, and the running marker after an ambient
rebuild. Provider tests cover both catalogs, unchanged notifications, total
failure, newer refreshes, changed credentials, and a deadline with an HTTP
handler that ignores cancellation. The deadline test uses 50 ms; production
uses 20 seconds, below the host's 30-second boundary.

Run the Settings check from the repository root:

```sh
DOTNET_ROOT=/tmp/typewhisper-dotnet PATH=/tmp/typewhisper-dotnet:$PATH \
  uv run --no-project artifacts/verification/2026-10-20-maintainer-pr-138/verify_settings.py
```

The script extracts the two verification helpers from local commit `4f3aa26d`.
It needs the prerequisites documented by that commit's verification skill,
including mutter, AT-SPI, system Python GI, and GStreamer. It uses accessibility
actions in a private desktop. It does not need to unlock or restart the user's
desktop. Generated helpers, the private TLS key, and the throwaway instance
remain local artifacts and are not committed.

The fixture is a local HTTPS CONNECT proxy. Only the isolated app trusts its
temporary certificate. It accepts the real OpenRouter host and these exact
paths:

- `GET https://openrouter.ai/api/v1/models`
- `GET https://openrouter.ai/api/v1/models?output_modalities=transcription`

Other hosts receive 502. Other OpenRouter paths receive 503. No production
endpoint override, fake cleanup LLM, real credential, or host trust change is
used. This proves routing through the real provider code with controlled
responses. It does not prove availability or account authorization at the public
OpenRouter service.

The live check verifies all of the following:

- [x] Expanding Settings makes no catalog request and does not change the settings file or its modification time.
- [x] Explicit Refresh persists both fixture catalogs.
- [x] The saved unknown LLM model and its marker match the baseline captured after activation.
- [x] Repeating an unchanged refresh does not write the settings file.
- [x] Both endpoints returning 503 shows the failure message and preserves the cache byte for byte.
- [x] A temperature edit made while both responses are held survives completion. Clicking Save persists `llmTemperatureValue: 0.7`.
- [x] Provider notifications do not duplicate controls. OpenRouter appears under two categories, with one set of fields in each card.

`settings-results.json`, `provider-requests.json`, and `persisted-catalog.json`
record the assertions, actual requests, and successful cache. `failure.png`
shows the visible failure message with the previous catalog still present.
The complete local AT-SPI snapshots and isolated instance remain in this folder.

No dictation or physical microphone was used. No ASR model or acceleration
backend was loaded. Transcription, cleanup, and delivery timings do not apply.
The user's app PID 1628450 remained running. User settings, model folders,
history, recordings, credentials, and clipboard were not changed.

To repeat the Release build and test commands on this older branch, extract
`4f3aa26d:justfile` to `/tmp/pr138-verification-tools/justfile`, then use:

```sh
export DOTNET_ROOT=/tmp/typewhisper-dotnet
export PATH=/tmp/typewhisper-dotnet:$PATH
just --justfile /tmp/pr138-verification-tools/justfile --working-directory "$PWD" restore
just --justfile /tmp/pr138-verification-tools/justfile --working-directory "$PWD" build-release
just --justfile /tmp/pr138-verification-tools/justfile --working-directory "$PWD" test tests/TypeWhisper.Core.Tests/TypeWhisper.Core.Tests.csproj
just --justfile /tmp/pr138-verification-tools/justfile --working-directory "$PWD" test tests/TypeWhisper.PluginSystem.Tests/TypeWhisper.PluginSystem.Tests.csproj
just --justfile /tmp/pr138-verification-tools/justfile --working-directory "$PWD" test tests/TypeWhisper.Linux.Tests/TypeWhisper.Linux.Tests.csproj
just --justfile /tmp/pr138-verification-tools/justfile --working-directory "$PWD" test tests/TypeWhisper.Cli.Tests/TypeWhisper.Cli.Tests.csproj
just --justfile /tmp/pr138-verification-tools/justfile --working-directory "$PWD" test tests/TypeWhisper.Integration.Tests/TypeWhisper.Integration.Tests.csproj
```

No push, PR creation, or GitHub comment was performed. The parent owns review
and publication.

The parent independently reran the Settings script at commit `2cf37edb`.
All six assertions passed. The parent also checked all twelve source and Release
binary hashes against `verification-manifest.json`; every hash matched.
The updated `run.json` records this rerun. Its dirty entries are generated
evidence files only. The source code was unchanged.
