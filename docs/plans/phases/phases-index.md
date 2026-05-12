# Phases Index

Working plan for the Wayland global-hotkey fix. See `cl-recommendation.md` for the synthesis and rationale.

| Phase | File | Status | What it does |
|---|---|---|---|
| 1 | [phase-1-interface-extraction.md](phase-1-interface-extraction.md) | ready | Lift current SharpHook logic behind `IGlobalShortcutBackend`. Zero behavior change. |
| 2 | [phase-2-evdev-backend.md](phase-2-evdev-backend.md) | blocked on 1 | **Fixes the reported bug.** Add evdev backend as primary Wayland path. |
| 3 | [phase-3-portal-and-settings-ui.md](phase-3-portal-and-settings-ui.md) | blocked on 2 | XDG portal backend (toggle-only fallback) + Settings → Shortcuts status panel + shortcut test modal. |
| 4 | [phase-4-single-instance-cli.md](done/phase-4-single-instance-cli.md) | done | Unix socket; bare `typewhisper` toggles existing instance; single-instance enforcement. |
| 5 | [phase-5-full-cli-subcommands.md](phase-5-full-cli-subcommands.md) | blocked on 4 | `typewhisper record start/stop/toggle/cancel`, `status`. Enables Hyprland/Sway true PTT via compositor binds. |
| 6 | [phase-6-de-helpers.md](phase-6-de-helpers.md) | blocked on 5 | "Set up automatically" buttons for GNOME / KDE / Hyprland / Sway. Polish. |

## Critical path

Phases 1 + 2 alone fix the user's reported bug for anyone willing to join the `input` group on Wayland. That's the smallest possible diff that solves the problem and preserves Toggle / PushToTalk / Hybrid.

Phases 3–6 broaden supported configurations without touching the core fix.

## Source documents

- `source/1cl.md` — primary implementation source (evdev backend, interface, code-level detail).
- `source/2co.md` — default hotkey discussion, simpler implementation order.
- `source/3ch.md` — daemon/CLI/state-machine concepts, setup wizard, testing matrix. (We borrow the wizard and matrix; we reject the daemon split.)
- `source/cl-recommendation.md` — my synthesis.
- `source/ch-recommendation.md` — colleague's synthesis (very close to `cl-`; idempotency callout, test panel, success criteria are folded into phase 2/3).
- `source/co-recommendation.md` — colleague's synthesis (right destination, wrong ordering — they build CLI before evdev; we don't).
