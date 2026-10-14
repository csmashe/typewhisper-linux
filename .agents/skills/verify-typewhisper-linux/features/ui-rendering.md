# UI rendering and UX flows

## Sub-features

- Every Settings section renders inside the real main window with real ViewModels.
- Text fits its space in every UI language (English, German, Spanish, Russian) at the default and a compact window size.
- No raw localization key, and no text from another UI language, reaches the screen.
- Every interactive control has an accessible name, so screen readers and UI tests can find it. That includes the sidebar navigation.

## How to get to it (user POV)

Open TypeWhisper and click through the Settings navigation. Change Settings, then General, then Interface Language to switch the UI language. Shrink the window to about 960 by 640 to see the compact layout.

## Driving it with the headless UI tests

```bash
just build-release
just ui-check artifacts/verification/$(date +%F)-ui
```

`tests/TypeWhisper.UiTests` starts Avalonia headless with Skia rendering, builds the production service graph with the integration suite's headless boundaries, and renders the main window once per section, language, and size. It writes one PNG per variant, for example `Dictation-de-dark-compact.png`, and `findings-<language>.json`. It runs in `just ci` (as `test-ui`) and in the PR workflow, which uploads the PNGs. The test fails on:

- `raw-localization-key`: a known catalog key, or text shaped like one, on screen.
- `clipped-text`: an untrimmed `TextBlock` whose widest line is wider than its box. Wrapped text is checked too, because a single long word cannot break.
- `unnamed-control`: a button, toggle, combo box, text box, number box, or slider whose automation peer has no name. Template parts such as scrollbar buttons are skipped.
- `wrong-language-text`: text that only another UI language's catalog contains. This catches computed labels that were not refreshed after a language change.

Each failure lists the section, control, detail, nearby labels, and affected sizes. Open the matching PNG to see the problem. Proven on 2026-10-14: all four languages pass after fixing 59 unnamed controls, clipped labels in Dashboard, Dictation, Shortcuts, and Advanced, and stale engine and model labels after a language switch.

## Gotchas

- The PNG shows only the visible viewport; checks cover the whole section, including content scrolled out of view.
- The app pins the Dark theme on every window, so only Dark is rendered.
- Plugins are not loaded in the test graph, so plugin-provided settings pages and lists render empty.
- Clipping is measured against the TextBlock's own box. A parent with `ClipToBounds` that hides part of a wider box is not detected.
- Wrapped lines are measured without their trailing space, which is never drawn.
- Name a control with `AutomationProperties.Name="{loc:Str <label key>}"`, using the key of its visible row label, so the name follows the UI language. Items generated from a template, such as the sidebar buttons, use `AutomationProperties.Name="{Binding Label}"`; without a name, Avalonia does not expose them over AT-SPI at all.
- Prefer layouts that survive long translations: let labels wrap, give label columns a minimum width, cap control widths instead of fixing them, and put wide control groups under their title.

## Live flows in a hidden desktop

```bash
just ui-live artifacts/verification/$(date +%F)-hidden-desktop
just ui-shortcuts artifacts/verification/$(date +%F)-shortcuts
```

`scripts/verification/hidden_desktop.py` starts a private D-Bus session, the AT-SPI bus and registry on it, and `mutter --headless --wayland --virtual-monitor 1440x900`. An isolated app instance draws into that compositor's Xwayland display.

- `HiddenDesktop.accessibility(click=name)` reads the AT-SPI tree, with each node's role, name, screen extents, and text, and invokes the first action of the control with that name.
- `HiddenDesktop.input(steps)` clicks at screen coordinates and types key combinations through mutter's RemoteDesktop API.
- `HiddenDesktop.screenshot(path)` captures a real composited frame through mutter's ScreenCast API and GStreamer `pipewiresrc`.

Proven on 2026-10-14: Open Wizard opened the welcome wizard, the Shortcuts nav item opened its section, and the shortcut recorder saved `Ctrl+Shift+F8`, each with screenshots.

- The private bus cannot start the a11y bus or registry through systemd; the helper starts `at-spi-bus-launcher` and `at-spi2-registryd` itself.
- Settings sections nest deeply; the AT-SPI walk goes 40 levels deep.
- Never use `ydotool` or other uinput tools here; they type into the user's real desktop. RemoteDesktop input reaches only the hidden compositor.
- Delivery inside the hidden desktop goes to that session's clipboard, never the user's.
