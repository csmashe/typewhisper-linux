# Setup-test reset toolkit

Tools to test the TypeWhisper onboarding setup repeatedly **without reinstalling
the OS**. They snapshot the machine's clean state, then surgically undo
everything the setup created.

## Why a baseline?

Most of what the setup writes is unambiguously ours — files stamped
`Installed by TypeWhisper`, or paths that are exclusively TypeWhisper's
(`~/.local/share/TypeWhisper`, the `typewhisper-*` GNOME shortcut,
`typewhisper.desktop`). Those are always safe to remove.

Two things are **not** self-identifying:

- **Packages** (`ydotool`, `wl-clipboard`, `xclip`, `xdotool`, `wtype`,
  `ffmpeg`) — some are usually pre-installed.
- **The GNOME "Window Calls" extension** — often already present.

`snapshot.sh` records which of those were already there, so `reset.sh` removes
**only** what the setup actually added.

## Workflow

```bash
# 1. ONCE, on a clean machine, before testing:
scripts/setup-test/snapshot.sh

# 2. Run the setup wizard / exercise the setup tasks.

# 3. See what reset would remove (changes nothing):
scripts/setup-test/reset.sh --dry-run

# 4. Actually reset:
scripts/setup-test/reset.sh            # prompts for confirmation
#   or: reset.sh --yes                 # no prompt

# 5. Test again from step 2. Re-run snapshot.sh only if the machine's
#    legitimate clean state has changed.
```

### Useful flags for `reset.sh`

| Flag | Effect |
|------|--------|
| `--dry-run` | Print every action, change nothing. Your "list of what to remove." |
| `--yes` / `-y` | Skip the confirmation prompt. |
| `--keep-models` | Preserve `~/.local/share/TypeWhisper/Models` so you don't re-download models each pass. Everything else (settings, onboarding flag, DB, logs) is still wiped, so the wizard re-runs. |
| `--keep-packages` | Don't uninstall any packages this pass. |

## What `reset.sh` undoes

1. **ydotool**: `systemctl --user disable --now ydotoold.service`, removes the
   marker'd `~/.config/systemd/user/ydotoold.service` and
   `/etc/udev/rules.d/60-ydotool.rules` (via `pkexec`), reloads udev.
2. **GNOME shortcut**: removes the `typewhisper-*` entry from
   `org.gnome.settings-daemon.plugins.media-keys custom-keybindings` and resets
   its `name`/`command`/`binding` (leaves any other custom shortcuts untouched).
3. **Config files** (marker- or name-guarded): `~/.local/bin/typewhisper`,
   `~/.config/autostart/typewhisper.desktop`,
   `~/.config/environment.d/typewhisper-accessibility.conf`,
   `~/.local/share/kglobalaccel/typewhisper*.desktop`, TypeWhisper-marked
   `.desktop` launcher overrides, and TypeWhisper prefs in Firefox `user.js`.
4. **App data**: `rm -rf ~/.local/share/TypeWhisper` (or all but `Models` with
   `--keep-models`).
5. **Packages**: uninstalls only target packages that were **absent at baseline**
   and are present now.
6. **Window Calls extension**: uninstalls it only if it was **absent at baseline**.

## Notes

- The baseline lives at `~/.cache/typewhisper-setup-test/` (not in the repo).
- `reset.sh` refuses to run without a baseline (it would otherwise risk removing
  a pre-existing package/extension).
- KDE/Hyprland/Sway shortcut artifacts are covered too (marker/name-guarded),
  for when those environments are added.
- A file at one of these paths that lacks the `Installed by TypeWhisper` marker
  is **left in place** and reported, never deleted.
