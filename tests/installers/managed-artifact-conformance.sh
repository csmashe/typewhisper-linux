#!/usr/bin/env bash
# Offline conformance suite for scripts/lib/managed-artifacts.sh.
set -euo pipefail

SELF="$(readlink -f "${BASH_SOURCE[0]}")"
REPO_ROOT="$(cd "$(dirname "$SELF")/../.." && pwd)"
LIBRARY="$REPO_ROOT/scripts/lib/managed-artifacts.sh"

fail() {
  printf 'FAIL: %s\n' "$*" >&2
  exit 1
}

assert_file_text() {
  local path="$1"
  local expected="$2"
  [ -f "$path" ] || fail "expected regular file: $path"
  [ "$(cat "$path")" = "$expected" ] \
    || fail "unexpected contents in $path"
}

fixture_plan() {
  local action="$1"
  # shellcheck source=../../scripts/lib/managed-artifacts.sh
  source "$LIBRARY"
  ma_initialize conformance "$CASE_ROOT/state"
  if [ "$action" = install ]; then
    ma_register_directory app "$CASE_ROOT/install/typewhisper-app" "$CASE_ROOT/source/app"
    ma_register_file desktop "$CASE_ROOT/data/applications/typewhisper.desktop" "$CASE_ROOT/source/typewhisper.desktop" 0644
    ma_register_file icon "$CASE_ROOT/data/icons/typewhisper.png" "$CASE_ROOT/source/typewhisper.png" 0644
  else
    ma_register_directory app "$CASE_ROOT/install/typewhisper-app"
    ma_register_file desktop "$CASE_ROOT/data/applications/typewhisper.desktop"
    ma_register_file icon "$CASE_ROOT/data/icons/typewhisper.png"
  fi
  ma_register_link launcher "$CASE_ROOT/home/.local/bin/typewhisper" "$CASE_ROOT/install/typewhisper-app/typewhisper"
  ma_register_adoption app payload
  ma_register_adoption desktop desktop "$CASE_ROOT/install/typewhisper-app"
  ma_register_adoption icon icon "$CASE_ROOT/data/applications/typewhisper.desktop"
  ma_register_adoption launcher link-into "$CASE_ROOT/install/typewhisper-app"

  case "$action" in
    install) ma_install ;;
    remove) ma_remove ;;
    *) fail "unknown fixture action: $action" ;;
  esac
}

if [ "${1:-}" = --fixture ]; then
  [ -n "${CASE_ROOT:-}" ] || fail "CASE_ROOT is required in fixture mode"
  fixture_plan "${2:-}"
  exit 0
fi

TEST_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/managed-artifact-conformance.XXXXXX")"
cleanup() {
  rm -rf -- "$TEST_ROOT"
}
trap cleanup EXIT

prepare_source() {
  local case_root="$1"
  local version="$2"
  mkdir -p "$case_root/source/app/Cli"
  printf 'application %s\n' "$version" >"$case_root/source/app/typewhisper"
  printf 'cli %s\n' "$version" >"$case_root/source/app/Cli/typewhisper-cli"
  printf 'assembly %s\n' "$version" >"$case_root/source/app/typewhisper.dll"
  printf '{"runtimeOptions":{"tfm":"net10.0"}}\n' \
    >"$case_root/source/app/typewhisper.runtimeconfig.json"
  printf '[Desktop Entry]\nExec=%s/typewhisper\nIcon=%s\nStartupWMClass=typewhisper\nX-Version=%s\n' \
    "$case_root/install/typewhisper-app" "$case_root/data/icons/typewhisper.png" "$version" \
    >"$case_root/source/typewhisper.desktop"
  printf 'icon %s\n' "$version" >"$case_root/source/typewhisper.png"
  chmod 0755 "$case_root/source/app/typewhisper" \
    "$case_root/source/app/Cli/typewhisper-cli"
  chmod 0644 "$case_root/source/typewhisper.desktop" \
    "$case_root/source/typewhisper.png"
}

run_fixture() {
  local case_root="$1"
  local action="$2"
  CASE_ROOT="$case_root" bash "$SELF" --fixture "$action"
}

expect_fixture_failure() {
  local case_root="$1"
  local action="$2"
  local log="$case_root/expected-failure.log"
  if CASE_ROOT="$case_root" bash "$SELF" --fixture "$action" >"$log" 2>&1; then
    fail "fixture unexpectedly succeeded: $action ($case_root)"
  fi
  grep -Eq 'refusing|unsafe|modified|foreign|customized|symlink' "$log" \
    || fail "fixture failed without a refusal diagnostic: $log"
}

printf '==> foreign destination refusal\n'
case_root="$TEST_ROOT/foreign"
prepare_source "$case_root" v1
mkdir -p "$case_root/install/typewhisper-app"
printf 'foreign payload\n' >"$case_root/install/typewhisper-app/foreign"
expect_fixture_failure "$case_root" install
assert_file_text "$case_root/install/typewhisper-app/foreign" "foreign payload"
[ ! -e "$case_root/data/applications/typewhisper.desktop" ] \
  || fail "validation changed another destination after foreign refusal"

printf '==> symlinked destination refusal\n'
case_root="$TEST_ROOT/symlink"
prepare_source "$case_root" v1
mkdir -p "$case_root/data/applications"
printf 'foreign desktop target\n' >"$case_root/desktop-target"
ln -s "$case_root/desktop-target" "$case_root/data/applications/typewhisper.desktop"
expect_fixture_failure "$case_root" install
assert_file_text "$case_root/desktop-target" "foreign desktop target"
[ -L "$case_root/data/applications/typewhisper.desktop" ] \
  || fail "desktop symlink was replaced"
[ ! -e "$case_root/install/typewhisper-app" ] \
  || fail "validation published app payload before symlink refusal"

printf '==> customized recorded destination refusal\n'
case_root="$TEST_ROOT/customized"
prepare_source "$case_root" v1
run_fixture "$case_root" install
printf 'user customization\n' >>"$case_root/install/typewhisper-app/typewhisper"
prepare_source "$case_root" v2
expect_fixture_failure "$case_root" install
assert_file_text "$case_root/data/icons/typewhisper.png" "icon v1"
expect_fixture_failure "$case_root" remove
[ -L "$case_root/home/.local/bin/typewhisper" ] \
  || fail "validate-before-remove deleted the recorded launcher"

printf '==> exact legacy adoption\n'
case_root="$TEST_ROOT/legacy"
prepare_source "$case_root" v1
mkdir -p "$case_root/install/typewhisper-app" \
  "$case_root/data/applications" \
  "$case_root/data/icons" \
  "$case_root/home/.local/bin"
cp -a "$case_root/source/app/." "$case_root/install/typewhisper-app/"
cp "$case_root/source/typewhisper.desktop" "$case_root/data/applications/typewhisper.desktop"
cp "$case_root/source/typewhisper.png" "$case_root/data/icons/typewhisper.png"
chmod 0644 "$case_root/data/applications/typewhisper.desktop" \
  "$case_root/data/icons/typewhisper.png"
ln -s "$case_root/install/typewhisper-app/typewhisper" \
  "$case_root/home/.local/bin/typewhisper"
run_fixture "$case_root" install
[ -s "$case_root/state/installation.manifest" ] \
  || fail "exact legacy install was not adopted into a manifest"
assert_file_text "$case_root/install/typewhisper-app/typewhisper" "application v1"
run_fixture "$case_root" remove
[ ! -e "$case_root/install/typewhisper-app" ] \
  || fail "adopted directory remained after removal"

printf '==> pre-manifest old-layout upgrade adopts on ownership evidence\n'
case_root="$TEST_ROOT/old-layout"
prepare_source "$case_root" v2
mkdir -p "$case_root/install/typewhisper-app/Cli" \
  "$case_root/data/applications" \
  "$case_root/data/icons" \
  "$case_root/home/.local/bin"
# An older release: our identity files are present but every byte differs.
printf 'application v1\n' >"$case_root/install/typewhisper-app/typewhisper"
printf 'assembly v1\n' >"$case_root/install/typewhisper-app/typewhisper.dll"
printf '{"runtimeOptions":{"tfm":"net9.0"}}\n' \
  >"$case_root/install/typewhisper-app/typewhisper.runtimeconfig.json"
printf 'cli v1\n' >"$case_root/install/typewhisper-app/Cli/typewhisper-cli"
printf '[Desktop Entry]\nExec=%s/typewhisper\nIcon=%s\nStartupWMClass=typewhisper\nX-Version=v1\n' \
  "$case_root/install/typewhisper-app" "$case_root/data/icons/typewhisper.png" \
  >"$case_root/data/applications/typewhisper.desktop"
printf 'icon v1\n' >"$case_root/data/icons/typewhisper.png"
chmod 0644 "$case_root/data/applications/typewhisper.desktop" \
  "$case_root/data/icons/typewhisper.png"
ln -s "$case_root/install/typewhisper-app/typewhisper" \
  "$case_root/home/.local/bin/typewhisper"
run_fixture "$case_root" install
[ -s "$case_root/state/installation.manifest" ] \
  || fail "old-layout install was not adopted into a manifest"
assert_file_text "$case_root/install/typewhisper-app/typewhisper" "application v2"
assert_file_text "$case_root/data/icons/typewhisper.png" "icon v2"
[ ! -e "$case_root/install/typewhisper-app/typewhisper.runtimeconfig.json" ] \
  || assert_file_text "$case_root/install/typewhisper-app/typewhisper.runtimeconfig.json" \
    '{"runtimeOptions":{"tfm":"net10.0"}}'
run_fixture "$case_root" remove
[ ! -e "$case_root/install/typewhisper-app" ] \
  || fail "adopted old-layout directory remained after removal"

printf '==> unrecorded foreign content at the same paths still refuses\n'
case_root="$TEST_ROOT/foreign-lookalike"
prepare_source "$case_root" v1
mkdir -p "$case_root/install/typewhisper-app" "$case_root/data/applications"
# Same destinations, no TypeWhisper identity: not ours, so not adoptable.
printf 'someone elses app\n' >"$case_root/install/typewhisper-app/typewhisper"
printf 'someone elses library\n' >"$case_root/install/typewhisper-app/other.dll"
expect_fixture_failure "$case_root" install
assert_file_text "$case_root/install/typewhisper-app/typewhisper" "someone elses app"

printf '==> pre-manifest uninstall removes an old-layout install on ownership evidence\n'
case_root="$TEST_ROOT/old-layout-remove"
prepare_source "$case_root" v1
mkdir -p "$case_root/install/typewhisper-app/Cli" \
  "$case_root/data/applications" \
  "$case_root/data/icons" \
  "$case_root/home/.local/bin"
printf 'application v1\n' >"$case_root/install/typewhisper-app/typewhisper"
printf 'assembly v1\n' >"$case_root/install/typewhisper-app/typewhisper.dll"
printf '{"runtimeOptions":{"tfm":"net9.0"}}\n' \
  >"$case_root/install/typewhisper-app/typewhisper.runtimeconfig.json"
printf '[Desktop Entry]\nExec=%s/typewhisper\nIcon=%s\nStartupWMClass=typewhisper\n' \
  "$case_root/install/typewhisper-app" "$case_root/data/icons/typewhisper.png" \
  >"$case_root/data/applications/typewhisper.desktop"
printf 'icon v1\n' >"$case_root/data/icons/typewhisper.png"
chmod 0644 "$case_root/data/applications/typewhisper.desktop" \
  "$case_root/data/icons/typewhisper.png"
ln -s "$case_root/install/typewhisper-app/typewhisper" \
  "$case_root/home/.local/bin/typewhisper"
[ ! -e "$case_root/state/installation.manifest" ] \
  || fail "old-layout removal fixture unexpectedly had a manifest"
run_fixture "$case_root" remove
[ ! -e "$case_root/install/typewhisper-app" ] \
  || fail "unrecorded old-layout payload survived uninstall"
[ ! -e "$case_root/data/applications/typewhisper.desktop" ] \
  || fail "unrecorded old-layout desktop entry survived uninstall"
[ ! -e "$case_root/data/icons/typewhisper.png" ] \
  || fail "unrecorded old-layout icon survived uninstall"
[ ! -L "$case_root/home/.local/bin/typewhisper" ] \
  || fail "unrecorded old-layout launcher survived uninstall"

printf '==> pre-manifest uninstall skips foreign paths and still removes what is ours\n'
case_root="$TEST_ROOT/foreign-remove"
prepare_source "$case_root" v1
mkdir -p "$case_root/install/typewhisper-app" "$case_root/data/applications" \
  "$case_root/home/.local/bin"
# Foreign payload and desktop entry, alongside a launcher that resolves into the
# app directory and is therefore provably ours.
printf 'someone elses app\n' >"$case_root/install/typewhisper-app/typewhisper"
printf '[Desktop Entry]\nExec=/opt/other/app\n' \
  >"$case_root/data/applications/typewhisper.desktop"
chmod 0644 "$case_root/data/applications/typewhisper.desktop"
ln -s "$case_root/install/typewhisper-app/typewhisper" \
  "$case_root/home/.local/bin/typewhisper"
skip_log="$case_root/skip.log"
CASE_ROOT="$case_root" bash "$SELF" --fixture remove >"$skip_log" 2>&1 \
  || fail "foreign destinations aborted removal instead of being skipped"
assert_file_text "$case_root/install/typewhisper-app/typewhisper" "someone elses app"
[ -f "$case_root/data/applications/typewhisper.desktop" ] \
  || fail "foreign desktop entry was deleted by an unrecorded uninstall"
[ ! -L "$case_root/home/.local/bin/typewhisper" ] \
  || fail "a launcher resolving into the app directory was not removed"
grep -q 'app:' "$skip_log" || fail "skipped payload was not reported: $skip_log"
grep -q 'desktop:' "$skip_log" || fail "skipped desktop was not reported: $skip_log"

printf '==> a marker-bearing desktop file with a user-edited Exec is replaced (installer-owned, not a config fragment)\n'
case_root="$TEST_ROOT/customized-desktop"
prepare_source "$case_root" v1
mkdir -p "$case_root/data/applications"
# StartupWMClass proves the entry is ours. Unlike a shell-profile fragment, the
# whole file is installer-owned, so ownership evidence authorizes replacing it.
printf '[Desktop Entry]\nExec=/opt/elsewhere/typewhisper --user-flag\nIcon=%s\nStartupWMClass=typewhisper\n' \
  "$case_root/data/icons/typewhisper.png" \
  >"$case_root/data/applications/typewhisper.desktop"
chmod 0644 "$case_root/data/applications/typewhisper.desktop"
run_fixture "$case_root" install
grep -q "Exec=$case_root/install/typewhisper-app/typewhisper" \
  "$case_root/data/applications/typewhisper.desktop" \
  || fail "marker-bearing desktop file was not republished"

run_interruption_case() {
  local name="$1"
  local hook="$2"
  local expected_destination_state="$3"
  local interrupted_root="$TEST_ROOT/$name"
  local status old_image
  prepare_source "$interrupted_root" v1
  run_fixture "$interrupted_root" install
  prepare_source "$interrupted_root" v2

  set +e
  {
    CASE_ROOT="$interrupted_root" \
      MANAGED_ARTIFACTS_TEST_HOOK="kill:$hook" \
      bash "$SELF" --fixture install
  } >/dev/null 2>&1
  status=$?
  set -e
  [ "$status" -ge 128 ] || fail "kill hook did not interrupt at $hook (status $status)"
  [ -s "$interrupted_root/state/transaction.journal" ] \
    || fail "interrupted install did not retain its journal"

  old_image="$(find "$interrupted_root/install" -maxdepth 1 \
    \( -name '.typewhisper-app.ma-backup.*' -o -name '.typewhisper-app.ma-stage.*' \) \
    -print -quit)"
  [ -n "$old_image" ] || fail "interrupted install did not retain its exact old image"
  assert_file_text "$old_image/typewhisper" "application v1"
  if [ "$expected_destination_state" = absent ]; then
    [ ! -e "$interrupted_root/install/typewhisper-app" ] \
      || fail "destination was partial after backup checkpoint"
  else
    assert_file_text "$interrupted_root/install/typewhisper-app/typewhisper" "application v2"
  fi

  run_fixture "$interrupted_root" install
  assert_file_text "$interrupted_root/install/typewhisper-app/typewhisper" "application v2"
  [ ! -e "$interrupted_root/state/transaction.journal" ] \
    || fail "journal remained after recovery"
  [ -z "$(find "$interrupted_root/install" -maxdepth 1 \
    \( -name '*.ma-stage.*' -o -name '*.ma-backup.*' \) -print -quit)" ] \
    || fail "transaction siblings remained after recovery"
}

if mv --help 2>/dev/null | grep -q -- '--exchange'; then
  printf '==> atomic sibling exchange and journal recovery at exchange checkpoint\n'
  run_interruption_case interrupted-exchange install-after-exchange-app published
else
  printf '==> journaled sibling fallback and recovery after backup checkpoint\n'
  run_interruption_case interrupted-backup install-after-backup-app absent
fi
printf '==> sibling staging and journal recovery after publish checkpoint\n'
run_interruption_case interrupted-publish install-after-publish-app published

printf '==> manifest-scoped removal and recorded-link-only behavior\n'
case_root="$TEST_ROOT/removal"
prepare_source "$case_root" v1
run_fixture "$case_root" install
mkdir -p "$case_root/data/TypeWhisper"
printf 'irreplaceable user data\n' >"$case_root/data/TypeWhisper/history"
run_fixture "$case_root" remove
[ ! -e "$case_root/install/typewhisper-app" ] \
  || fail "manifest-owned app directory remained"
[ ! -e "$case_root/data/applications/typewhisper.desktop" ] \
  || fail "manifest-owned desktop file remained"
[ ! -L "$case_root/home/.local/bin/typewhisper" ] \
  || fail "recorded launcher remained"
assert_file_text "$case_root/data/TypeWhisper/history" "irreplaceable user data"

case_root="$TEST_ROOT/unrecorded-link"
prepare_source "$case_root" v1
mkdir -p "$case_root/home/.local/bin"
ln -s /usr/bin/true "$case_root/home/.local/bin/typewhisper"
# Unrecorded and pointing outside the app directory: not ours. Uninstall skips
# it, reports it, and still succeeds.
skip_log="$case_root/skip.log"
CASE_ROOT="$case_root" bash "$SELF" --fixture remove >"$skip_log" 2>&1 \
  || fail "a foreign launcher aborted removal instead of being skipped"
[ -L "$case_root/home/.local/bin/typewhisper" ] \
  || fail "unrecorded launcher was removed"
[ "$(readlink "$case_root/home/.local/bin/typewhisper")" = /usr/bin/true ] \
  || fail "unrecorded launcher was retargeted"
grep -q 'launcher:' "$skip_log" \
  || fail "skipped launcher was not reported by name: $skip_log"
grep -q 'no TypeWhisper ownership evidence' "$skip_log" \
  || fail "skipped launcher was not reported with a reason: $skip_log"

case_root="$TEST_ROOT/retargeted-link"
prepare_source "$case_root" v1
run_fixture "$case_root" install
rm "$case_root/home/.local/bin/typewhisper"
ln -s /usr/bin/true "$case_root/home/.local/bin/typewhisper"
expect_fixture_failure "$case_root" remove
[ -d "$case_root/install/typewhisper-app" ] \
  || fail "another artifact changed before retargeted-link refusal"
[ "$(readlink "$case_root/home/.local/bin/typewhisper")" = /usr/bin/true ] \
  || fail "retargeted launcher was removed"

printf '==> source-controlled tarball installer transaction\n'
case_root="$TEST_ROOT/tarball-installer"
payload="$case_root/payload"
profile_home="$case_root/home"
data_home="$case_root/data"
state_home="$case_root/state-home"
mkdir -p "$payload/lib" "$profile_home" "$data_home/TypeWhisper" "$state_home"
cp "$REPO_ROOT/scripts/tarball-install.sh" "$payload/install.sh"
cp "$LIBRARY" "$payload/lib/managed-artifacts.sh"
printf '#!/usr/bin/env bash\nprintf "fake TypeWhisper\\n"\n' >"$payload/typewhisper"
printf 'fake icon\n' >"$payload/typewhisper.png"
printf '[Desktop Entry]\nExec=typewhisper\n' >"$payload/typewhisper.desktop"
chmod 0755 "$payload/install.sh" "$payload/typewhisper"
printf 'preserve user data\n' >"$data_home/TypeWhisper/sentinel"
env HOME="$profile_home" XDG_DATA_HOME="$data_home" XDG_STATE_HOME="$state_home" \
  bash "$payload/install.sh" >/dev/null
env HOME="$profile_home" XDG_DATA_HOME="$data_home" XDG_STATE_HOME="$state_home" \
  bash "$payload/install.sh" >/dev/null
[ -L "$profile_home/.local/bin/typewhisper" ] \
  || fail "tarball installer did not publish its recorded launcher"
assert_file_text "$data_home/typewhisper-app/typewhisper" \
  '#!/usr/bin/env bash
printf "fake TypeWhisper\n"'
env HOME="$profile_home" XDG_DATA_HOME="$data_home" XDG_STATE_HOME="$state_home" \
  bash "$payload/install.sh" --uninstall >/dev/null
[ ! -e "$data_home/typewhisper-app" ] \
  || fail "tarball installer left its recorded app directory"
[ ! -L "$profile_home/.local/bin/typewhisper" ] \
  || fail "tarball installer left its recorded launcher"
assert_file_text "$data_home/TypeWhisper/sentinel" "preserve user data"

printf '==> shell lock serialization\n'
case_root="$TEST_ROOT/lock"
prepare_source "$case_root" v1
ready="$case_root/lock-ready"
release="$case_root/lock-release"
CASE_ROOT="$case_root" \
  MANAGED_ARTIFACTS_TEST_HOOK=pause:lock-acquired \
  MANAGED_ARTIFACTS_TEST_READY_FILE="$ready" \
  MANAGED_ARTIFACTS_TEST_RELEASE_FILE="$release" \
  bash "$SELF" --fixture install &
first_pid=$!
for _ in $(seq 1 100); do
  [ -e "$ready" ] && break
  kill -0 "$first_pid" 2>/dev/null || fail "lock holder exited before pausing"
  sleep 0.05
done
[ -e "$ready" ] || fail "lock holder did not reach its checkpoint"
CASE_ROOT="$case_root" bash "$SELF" --fixture install &
second_pid=$!
sleep 0.25
kill -0 "$second_pid" 2>/dev/null \
  || fail "second installer did not wait for the shell lock"
printf 'release\n' >"$release"
wait "$first_pid"
wait "$second_pid"
assert_file_text "$case_root/install/typewhisper-app/typewhisper" "application v1"

printf 'PASS: managed artifact conformance\n'
