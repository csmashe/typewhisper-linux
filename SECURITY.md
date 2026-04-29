# Security Policy

## Reporting a Vulnerability

If you discover a security vulnerability in TypeWhisper for Linux, please report
it responsibly.

**Do not open a public issue.** Instead, email security concerns to:
**security@typewhisper.com**

You can also use GitHub's private vulnerability reporting for this repository
when it is available.

We will acknowledge your report within 48 hours and aim to provide a fix within
7 days for critical issues.

When reporting, please include:

- Affected TypeWhisper version, commit, or release package
- Linux distribution, desktop environment, display server, and CPU architecture
- Clear reproduction steps
- Security impact and affected data or privileges
- Any relevant logs, screenshots, or proof-of-concept details that avoid
  exposing secrets or personal data

## Scope

TypeWhisper for Linux handles sensitive data including:

- Microphone audio and imported audio/video files
- Transcription history and generated text
- API keys and provider credentials
- Plugin packages, plugin settings, and plugin-managed secrets
- Local HTTP API server
- Desktop launcher and autostart integration

Issues in these areas are especially relevant.

## Security Boundaries

- The local HTTP API uses a `localhost` listener and is intended for local tools
  and scripts.
- The API server is disabled by default and must be enabled explicitly in
  Settings.
- API keys and plugin secrets are stored under the current Linux user profile by
  this port and should be protected with normal user-account filesystem
  permissions.
- Exported diagnostics include app, platform, and error metadata, but should not
  include API keys, audio payloads, or transcription history.
- Plugins are executable code and should only be installed from trusted sources.
- Desktop integration depends on distribution, desktop environment, compositor,
  XDG paths, and X11/Wayland behavior.

## Supported Versions

| Version | Supported |
|---------|-----------|
| Latest Linux branch release | Yes |
| Current Linux branch preview build | Best effort |
| Older versions | No |

## Research Guidelines

Good-faith security research is welcome. Please:

- Avoid accessing, modifying, or deleting data that is not yours
- Avoid exfiltrating secrets, API keys, recordings, transcripts, or personal data
- Avoid degrading service availability or disrupting other users
- Share only the information needed to reproduce and understand the issue
- Give the maintainers reasonable time to investigate before public disclosure

This project does not currently operate a paid bug bounty program.
