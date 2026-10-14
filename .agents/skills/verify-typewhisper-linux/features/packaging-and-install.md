# Packaging and install

## Sub-features

- `scripts/build-linux-packages.sh` builds a self-contained tarball, an AppImage, a `.deb`, and an `.rpm` from a fresh publish, bundling the CLI and all Linux plugins.
- `scripts/smoke-test-linux-packages.sh` checks package names, metadata, payload contents, ELF dependency floors, and plugin bundle identity. It then installs, upgrades, runs, and removes each package inside disposable Ubuntu and Fedora containers.
- `scripts/install-linux-app.sh`, `tarball-install.sh`, and `uninstall-linux-app.sh` install the tarball for one user and remove it again, keeping user data unless `--purge` is passed.

## How to get to it (user POV)

Download a release package and install it with the system package manager, or extract the tarball and run its install script. Start TypeWhisper from the desktop launcher; `typewhisper-cli --version` reports the installed version.

## Driving it with the package scripts

Build into a scratch folder, never into the repository:

```bash
just build-release
bash scripts/build-linux-packages.sh 0.17.0 /tmp/tw-verify-dist
```

Formats whose tool is missing are skipped with a warning: `.deb` needs `dpkg-deb`, `.rpm` needs `rpmbuild`. The AppImage step downloads a pinned, checksummed `appimagetool`.

Check the payload without installing anything:

```bash
mkdir -p /tmp/tw-verify-pkgcheck
tar -xzf /tmp/tw-verify-dist/typewhisper-linux-x64-0.17.0.tar.gz -C /tmp/tw-verify-pkgcheck
/tmp/tw-verify-pkgcheck/typewhisper-linux-x64-0.17.0/Cli/typewhisper-cli --version   # typewhisper-cli 0.17.0
/tmp/tw-verify-pkgcheck/typewhisper-linux-x64-0.17.0/typewhisper --help              # usage text
ls /tmp/tw-verify-pkgcheck/typewhisper-linux-x64-0.17.0/Plugins | wc -l              # 38
```

Run the full smoke test, which needs all four packages, Docker, and the host tools `cpio dpkg-deb file readelf rpm rpm2cpio`:

```bash
bash scripts/smoke-test-linux-packages.sh 0.17.0 /tmp/tw-verify-dist
```

`--validate-only` skips the containers, but it still needs every package and the same host tools. CI runs the full smoke test in `.github/workflows/package-dry-run-linux.yml`.

Proven on 2026-10-14: the build produced the tarball, the AppImage, and the `.deb` (`.rpm` skipped: no `rpmbuild`). The extracted tarball's CLI reported `0.17.0`, the app printed its usage, and 38 plugins were bundled. The smoke test did not run locally because the host lacks `rpm` and `rpmbuild`; on Arch-based systems they come from `rpm-tools`, which needs a system package install.

## Gotchas

- Delete only the scratch folders the run created (`/tmp/tw-verify-dist`, `/tmp/tw-verify-pkgcheck`). They hold about 400 MB.
- The package build runs `dotnet publish` against the Release `obj` folders; do not run another Release build at the same time.
- `typewhisper --help` creates the app's data folders under the real `XDG_DATA_HOME` before printing. Run it with an isolated `HOME` and XDG folders on a machine without TypeWhisper data.
- Package-manager installs happen only inside the smoke test's containers. Do not install a test package on the host; it would replace the user's installation.
- `--validate-only` is rejected when `CI=true`; CI must run the container checks.
