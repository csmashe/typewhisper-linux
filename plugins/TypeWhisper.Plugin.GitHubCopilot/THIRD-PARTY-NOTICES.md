# Third-party components

- GitHub.Copilot.SDK 1.0.13: https://github.com/github/copilot-sdk/tree/v1.0.13 (MIT; see `Licenses/GitHub.Copilot.SDK.txt`).
- Microsoft.Extensions.AI.Abstractions, Microsoft.Extensions.Logging.Abstractions and Microsoft.Extensions.DependencyInjection.Abstractions: https://github.com/dotnet/extensions and https://github.com/dotnet/runtime (MIT; see `Licenses/Microsoft.Extensions.txt`).

The GitHub Copilot runtime is not included in this package. When you choose **Download Copilot runtime**, the plugin downloads release 1.0.83 from https://github.com/github/copilot-cli, checks it against the SHA-256 pinned in `CopilotRuntimeInstaller.cs`, and keeps it in the plugin's own data folder. The runtime is a separate GitHub product under its own license, which ships inside the downloaded archive (`LICENSE.md`); the SDK's MIT license does not relicense it.

GitHub Copilot access and usage are governed by the user's GitHub agreement and Copilot plan. This plugin does not provide a subscription or a shared account.
