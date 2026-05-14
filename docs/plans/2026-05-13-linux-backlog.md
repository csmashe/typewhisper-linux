# Linux backlog — known issues after Wayland global-shortcuts work

Status as of 2026-05-13, branch `linux-wayland`. Phases 1–6 of the Wayland
global-hotkey fix are done and committed; this file tracks what's still
known-broken or known-imperfect, so we don't lose track when we pick this
back up.

## High priority — user-visible

### 1. CUDA / GPU acceleration — DONE

Resolved on the test machine on 2026-05-13. Transcription now runs on the
GeForce GTX 1070 instead of CPU; the ~27 s baseline for ~5 s of audio with
`large-v3-turbo` should be gone. (Details of the fix — driver path, plugin
packaging, log honesty — are in the commit history; this entry is left as
a marker so future readers see the previous state and know it's now
addressed.)

If a regression surfaces, the three originally-stacked issues were:
- nouveau loaded instead of the proprietary nvidia driver (kernel module
  missing despite `libcuda.so.595` being present).
- Plugin runtime shipped only CPU ggml backend — no
  `libggml-cuda-whisper.so` in `runtimes/linux-x64/`.
- Misleading log: plugin emitted `[Info] Loaded model … using CUDA` based
  on the *requested* runtime, not what actually executed.

### 2. Auto-paste / auto-type on GNOME Wayland — DONE, helper UX still needs work

**Status as of 2026-05-13 end of day**: the runtime path is fixed end-to-end.
ydotool is wired in as a Wayland backend; the per-compositor chain prefers
ydotool on GNOME/KDE and wtype on wlroots; the orchestrator yields focus
before paste; terminals and unknown-target apps route through direct typing
to avoid Ctrl+V semantics in apps like Claude Code (image paste) and bash
readline (quoted-insert). Once ydotool is installed and its daemon is
running, dictation works reliably.

**What still needs fixing — the setup UX** (task #8 in this session's task
list):

The in-app "Settings → Text insertion → Set up ydotool" button currently
**does not work on Fedora**. It hardcodes
`systemctl --user enable --now ydotoold.service`, but Fedora's `ydotool`
package only ships `/usr/lib/systemd/system/ydotool.service` — a
system-level unit named `ydotool.service`, not the user-level
`ydotoold.service` the helper expects.

For the manual workaround that *does* work (used to unblock the current
session), see the bottom of this entry — that exact recipe is what the
helper should automate.

#### Concrete fix sketch for `YdotoolSetupHelper.SetUpAsync`

1. **Detect existing units.** If `systemctl --user is-enabled ydotoold.service`
   succeeds, the user (or a distro package) already has a working unit —
   just `--now` it. If `systemctl --system is-enabled ydotool.service`
   reports it as installed (Fedora case), skip writing our own and instead
   use the system one *only if* its socket location ends up reachable by
   the user — for Fedora's default `ExecStart=/usr/bin/ydotoold` (no args,
   runs as root), the socket lands at `/tmp/.ydotool_socket` with root
   ownership, which the user can't read. Conclusion: prefer writing our
   own user unit regardless of the system unit's presence.
2. **Write `~/.config/systemd/user/ydotoold.service`** with this content
   (same as the one currently installed on the test machine):
   ```
   [Unit]
   Description=ydotool user daemon (started by TypeWhisper)
   Documentation=https://github.com/ReimuNotMoe/ydotool
   After=default.target

   [Service]
   Type=simple
   ExecStart=/usr/bin/ydotoold
   Restart=on-failure
   RestartSec=2

   [Install]
   WantedBy=default.target
   ```
   Use `Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? "~/.config", "systemd/user/ydotoold.service")`.
3. **Run** `systemctl --user daemon-reload && systemctl --user enable --now ydotoold.service`.
4. **Poll** `/run/user/$UID/.ydotool_socket` until it appears (existing
   logic already does this).
5. **Skip the pkexec/udev rule step when /dev/uinput is already
   accessible.** Check group membership: `getent group input` and `getent group uinput`,
   compare against the current user's groups (via `id -nG`). If the user
   is already in `input` or the file mode allows the user
   (`crw-rw----+ root:input` with input membership = OK), skip the udev
   step entirely. Many Fedora and Arch installs put users in `input` by
   default and don't need the rule.
6. **Keep the existing functional probe** (no-op `ydotool key 56:0`) —
   confirms the socket → daemon → /dev/uinput path is usable before
   declaring success.

#### What the Text insertion panel should also surface

The current panel shows status rows for xdotool / wtype / ydotool. For
the average user it should:

- Detect when the binary is missing and show a distro-specific install
  command (currently does via `ManualInstallCommand`, which is fine).
- Distinguish "binary installed but daemon not running" from "binary not
  installed" — the user shouldn't see the same "ydotool not configured"
  message in both cases (the install vs. setup actions are different).
- Show a clearer success state with the socket path + "auto-starts on
  every login" message once setup is done.
- (Stretch) explain what's actually happening if they care: "TypeWhisper
  uses ydotool to type into apps because GNOME/Wayland rejects the
  simpler wtype protocol."

#### Manual workaround (until the helper is fixed)

This is what we walked through in the 2026-05-13 session, captured verbatim
so it can be reproduced without re-deriving:

1. Install the package: `sudo dnf install -y ydotool` (Fedora) /
   `sudo apt install -y ydotool` (Debian/Ubuntu) /
   `sudo pacman -S ydotool` (Arch).
2. Confirm /dev/uinput is reachable: `groups | grep -qw input || sudo usermod -aG input "$USER"` (the latter requires a re-login). On most Fedora installs the user is already in `input`.
3. Write `~/.config/systemd/user/ydotoold.service` with the unit content above.
4. `systemctl --user daemon-reload`
5. `systemctl --user enable --now ydotoold.service`
6. Verify with `systemctl --user is-active ydotoold.service` (`active`),
   `ls -l /run/user/$(id -u)/.ydotool_socket` (exists and user-owned),
   and `YDOTOOL_SOCKET=/run/user/$(id -u)/.ydotool_socket ydotool key 56:0 ; echo $?` (`0`).
7. Restart TypeWhisper. The platform's `SnapshotChanged` subscription will
   rebuild the backend chain to include ydotool as the preferred Wayland
   backend.

#### Already-shipped lower-effort improvements (this session)

- ✅ Detect wtype's "Compositor does not support" stderr; sticky-disable
  the backend for the process so we don't pay ~225 ms retrying a doomed
  path on every dictation.
- ✅ Reason-aware fallback popup — `LastFailureReason` distinguishes
  compositor rejection, ydotool socket unreachable, focus failure, etc.,
  and the orchestrator's `ClipboardFallbackMessage` surfaces the relevant
  remediation step instead of a generic "paste with Ctrl+V."
- ✅ Pattern-matching terminal detection (any process ending in `term` or
  `-terminal`, plus an explicit list) so we don't have to hand-add every
  new terminal emulator. Combined with a Wayland-no-xdotool fallback that
  prefers direct typing for unknown targets — handles terminals, Claude
  Code, vim, etc. without per-app rules.

### 3. Window restore from tray opens in unviewable state

When the main window is minimized to the system tray and the user reopens it
(tray menu or relaunch via socket), it comes back in a state where the user
can't see the contents. Likely culprits to investigate:
- Window stays on a virtual desktop that no longer exists.
- Window position lands off-screen (saved coords from a previous monitor
  layout).
- WindowState/Visible flags inconsistent after `Hide()`/`Show()` cycle.
- Avalonia's `Activate()` doesn't always raise on GNOME Wayland — may need
  to set `WindowState` first, then `Show()`, then `Activate()` in that order.

`App.axaml.cs ShowMainWindow` is the relevant helper. Reproduce on GNOME
Wayland, capture the window's actual position/state at the moment of failure,
then decide.

### 4. Recording overlay not visible above other windows (GNOME Wayland)

`DictationOverlayWindow` is configured `Topmost = true` in both AXAML (line 14)
and code-behind (line 31), but GNOME Mutter on Wayland silently ignores
application-set always-on-top — only the user can toggle it via right-click
"Always on Top". So while dictation is recording, the user can't see the
overlay indicator over their target app and has no visual confirmation that
recording is live.

Compositor support reality:
- Sway, Hyprland (wlroots): honor `Topmost` via the layer-shell protocol —
  works as intended.
- GNOME Mutter Wayland: ignores `Topmost`. No supported mechanism for an app
  to force always-on-top.
- KDE Plasma Wayland: partially honors via window rules but inconsistent.

Options:
- Detect compositor at overlay-show time. On wlroots-based, current behavior
  works. On GNOME/KDE, switch to a different indicator (desktop notification,
  tray icon change, sound cue + screen flash).
- Render the overlay as a `zwlr_layer_shell_v1` surface where supported —
  requires Avalonia layer-shell support (doesn't exist out of the box) or
  a raw Wayland surface, which is a heavy lift.
- Pragmatic short-term: animate the tray icon while recording (already a
  service, would be cheap) and keep the overlay as the primary indicator
  where compositors respect it.
- Add a visible recording sound on start/stop (Settings already has
  `SoundFeedbackEnabled`); make this default-on on GNOME if we can't show
  the overlay.

### 5. Hotkey not firing the second time (just fixed — verify)

Commit `8c5a422` released `_toggleGate` after audio teardown so transcription
runs ungated. CLI smoke test passed. **Verify end-to-end** with a real
hotkey-driven cycle before we mark this closed. If it regresses, the issue is
in the dispatcher or evdev layer, not the orchestrator.

## Medium priority — correctness

### 6. Evdev device discovery misses virtual keyboards

`KeyboardDeviceDiscovery.cs` only enumerates `/dev/input/by-path/*-event-kbd`
symlinks. Virtual keyboards (kanata, vicinae snippet keyboard, etc.) don't
get those symlinks, so we don't watch them. The code's own comment flags this
as deferred work — "until we see real users on systems where the symlinks are
absent." We saw one. Fix: fall back to enumerating `/dev/input/event*` and
probing each with `ioctl(EVIOCGBIT)` for `EV_KEY` capability + a sensible key
bitmap (e.g., `KEY_SPACE` set).

### 7. Partial-transcription polling logs dispose error

Repro: `[Dictation] Partial transcription polling failed: Cannot dispose while
processing, please use DisposeAsync instead.` Surfaced in the journal on a
recent dictation cycle. Likely in `StreamingTranscriptState` or the streaming
loop — calling `Dispose()` where `DisposeAsync()` is required by the
underlying type. Cosmetic for now but the message is shipped to users via the
error log.

### 8. ModelManagerService is global state under in-flight transcription

Flagged in commit `8c5a422`'s adversarial review (not applied). After the
toggle-gate fix, a new dictation can start while a previous one is still
transcribing. If the new dictation's profile uses a different model and
triggers `EnsureModelLoadedAsync(differentModelId)`, the global
`ActiveTranscriptionPlugin` / `ActiveModelId` is replaced under the in-flight
transcription, potentially mid-call. Real fix: per-recording plugin handle
captured at the start of `TranscribeAndInsertAsync` and used for that
transcription's full lifetime. Rare in practice — same model + same profile
in most flows.

### 9. Hyprland live-bind on repeat clicks unverified

Phase 6 adversarial review flagged this. `hyprctl keyword bind` is *expected*
to replace an existing bind with the same keysym, not stack duplicates, but
this wasn't tested at runtime. If a Hyprland user clicks "Set up
automatically" twice with the same trigger, we may produce duplicate
runtime binds even though the config file stays clean. Verify by running it
on Hyprland and inspecting `hyprctl binds`.

### 10. KeyboardDeviceDiscovery + multi-device keyboards

Adjacent to #5: even when a "real" keyboard *does* get the `-event-kbd`
symlink, split keyboards (Dygma Raise2, Kinesis Advantage 360, ZSA Moonlander)
may surface multiple event devices and we currently only watch one. Confirm
behavior on a Dygma after #5 is fixed.

## Low priority — polish / cleanup

### 11. SentinelBlock can drop a trailing newline

Phase 6 review finding. `SentinelBlock.Format` round-trip can drop a single
trailing newline in the user's config file. Cosmetic, no data risk; fix by
preserving the original file's trailing-newline state.

### 12. Atomic writes don't preserve file mode

Phase 6 review finding. When the DE writers atomic-write a config file via
temp + rename, the new file gets default permissions instead of preserving
the original. Newly-created files are fine; only matters if the user has
hardened their hyprland.conf or sway/config with non-default mode.

### 13. SESSION_MANAGER warning on startup

Avalonia's X11Platform logs `SMLib/ICELib reported a new error:
SESSION_MANAGER environment variable not defined` on every startup under
Wayland. Cosmetic noise. Either set `SESSION_MANAGER=` to suppress it before
Avalonia init, or document it as expected.

### 14. `--no-single-instance` escape hatch never shipped

Phase 4 spec mentioned `--no-single-instance` as a flag for users who want to
run multiple instances (testing scenarios). Wasn't implemented; "Risks" table
called it out as a follow-up. Add when first user asks.

### 15. `status` state mapping is approximate

Phase 5 introduced `DictationOrchestrator.CurrentStateLabel` projecting the
internal state to one of `idle | recording | transcribing | injecting`. The
`injecting` state is short-lived in the current insertion path; everything
post-insertion-attempt lands as `idle`. Documented inline, but worth
tightening if anyone scripts against `typewhisper status` output.

### 16. `DictationOverlayWindow` uses an Opacity workaround instead of Hide()/Show()

`DictationOverlayWindow.UpdateWindowVisibility` calls `Show()` once and never
`Hide()`s; visibility is driven by toggling `Opacity` between `1.0` and `0.0`
(and `IsHitTestVisible` between true/false). This works around a real
Avalonia / Wayland bug: on GNOME Mutter with `ShowActivated="False"` /
`Topmost="True"` / `ShowInTaskbar="False"`, a `Window.Show()` after a prior
`Window.Hide()` does not reliably re-display the window. Reproduces as
"recording overlay shows for the first two dictations, then disappears
permanently until app restart." Dictation itself keeps working — only the
visual indicator is gone.

Workaround cost is minimal (one persistent transparent surface that modern
compositors skip when opacity is zero), but the code reads like a hack.

Investigation paths worth trying:
- Whether `Show()` with explicit `WindowState = WindowState.Normal` first
  recovers a hidden window.
- Whether a different shell type (popup, `PlatformImpl?.Show`) avoids it.
- Whether the bug is filed/known upstream in Avalonia's repo for Wayland
  + utility-window flags.
- Whether destroying and re-creating the `DictationOverlayWindow` on each
  dictation cycle (instead of toggling visibility) cleanly solves it
  without the always-shown surface — would need DI / lifecycle plumbing.

Memory note: [[project-wayland-focus-yield]] in user auto-memory documents
why the helper must not mutate overlay state. That note + this entry are
both load-bearing for anyone considering "let me just clean up the
overlay code."

### 17. Profile matching is broken on Wayland — no app process / URL capture

Symptom: profiles configured against specific apps or URLs (e.g. "when I'm
in Gmail, run my email-cleanup prompt") never match on Wayland. Dictation
still works but skips the profile-specific behavior — the user-visible
effect is "my dictation went through but the LLM cleanup that should have
fixed my bad email didn't fire."

Root cause: every active-window probe in `ActiveWindowService` is
xdotool-shaped, and xdotool only works on X11 / XWayland-mapped windows:

- `GetActiveWindowProcessName` — `if (!IsXdotoolAvailable) return null;`
  (`ActiveWindowService.cs` ~63).
- `GetActiveWindowTitle` — same gate (~81).
- `GetActiveWindowId` — same gate (~157).
- `GetBrowserUrl` — has three sub-paths but all assume something xdotool
  feeds them:
  1. AT-SPI query (`TryGetBrowserUrlViaAtSpi`) — should still work on
     accessibility-enabled apps, but in practice Firefox / Chrome on
     Wayland often don't expose URLs via AT-SPI without launch flags.
  2. Title-based URL inference (`TryInferBrowserUrlFromTitle`) — works
     for the rare case where the browser puts the URL into the window
     title; most modern browsers show *the page title*, not the URL.
  3. xclip + xdotool keystroke capture (`TryCaptureBrowserUrl`) —
     focuses the window, sends Ctrl+L → Ctrl+C, reads xclip. Requires
     both xdotool (focus + Ctrl+L send) and xclip (clipboard read).
     Hard-dead on Wayland-no-xdotool.

Result on the test machine (GNOME Wayland, no xdotool installed): every
`ActiveWindowService` call returns null; `DictationContext.AppProcess`,
`AppTitle`, and `AppUrl` are all null;
`ProfileService.MatchProfile(appProcess, appUrl)` falls through to the
default profile.

Fix paths to investigate (rough effort order):

1. **xdg-desktop-portal "Screenshot" / "GlobalShortcuts" plumbing for the
   focused window's app id.** GNOME Mutter exposes the focused
   `wl_surface`'s app_id via D-Bus extensions on some versions — worth
   checking what the Speed-of-Sound and Voxtype projects do here.
2. **AT-SPI improvements.** Today's `TryGetBrowserUrlViaAtSpi` only
   handles the URL-in-address-bar case. Extending it to also pull the
   focused app's `Application` name (the role-1 frame title) would give
   us process-name-equivalent matching for any accessibility-enabled
   app. The bigger win is making it actually work on stock Firefox and
   Chrome on Wayland — both need
   `MOZ_ENABLE_ACCESSIBILITY=1` / `--force-renderer-accessibility`,
   which we can't set on the user's behalf but we *can* document.
3. **`procfs` heuristic via PID-of-focused-surface.** Hyprland exposes
   the focused surface's PID via `hyprctl activewindow -j`, KDE Plasma
   via `qdbus org.kde.KWin /KWin org.kde.KWin.activeWindow`, Mutter via
   no documented path. Per-compositor fallback chain — ugly but covers
   wlroots well.
4. **User-configured per-app rules.** Some users wouldn't mind setting
   `WM_CLASS_HINT=email` on their email client window manually. We
   could expose a "manual app id" override per profile so the rule
   fires even without auto-detection.

Also unblocks parts of [[project-fedora-ydotool-setup]] and the
unknown-target heuristic in `TextInsertionService` — both currently
work around the same null `AppProcess` problem; getting it populated
would let the type/paste decision use the actual app rather than the
"unknown target on Wayland" branch.

For now: profiles configured against process name / URL silently no-op
on Wayland-without-xdotool. Document this in the profile UI so users
know what to expect, ideally with the "install xdotool for XWayland
matching" hint surfaced from the same compositor-detection that drives
the Text insertion panel.

## Notes

- All bullet points reference real files / commits — `git log linux-wayland`
  has the history.
- The branch is not pushed. Reviewing the diff before push remains an open
  step on the user's side.
