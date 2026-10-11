#!/usr/bin/env bash
# Smoke-tests a staged build without needing a credential store (nothing here stores a secret value).
# Usage: scripts/smoke.sh <staging-dir> <expected-version> [store]
#   store: also do a real credential-store round trip (informational; CI runners may lack a usable store)
set -uo pipefail

dir=${1:?staging directory}
version=${2:?expected version}
mode=${3:-}

ext=""
[ -f "$dir/keyturn.exe" ] && ext=".exe"
k="$dir/keyturn$ext"
failures=0

check() { # check <description> <command...>
  local description=$1; shift
  if "$@"; then echo "ok   - $description"; else echo "FAIL - $description"; failures=$((failures + 1)); fi
}
exit_is() { # exit_is <code> <command...>
  local want=$1; shift
  "$@" >/dev/null 2>&1; local got=$?
  [ "$got" -eq "$want" ] || { echo "       expected exit $want, got $got: $*"; return 1; }
}

export KEYTURN_CONFIG_DIR
KEYTURN_CONFIG_DIR=$(mktemp -d)

# On macOS the app is a bundle; everywhere else it is a single file beside the CLI.
app="$dir/Keyturn.App$ext"
bundle_exe="$dir/Keyturn.app/Contents/MacOS/Keyturn.App"
[ -f "$bundle_exe" ] && app=$bundle_exe

check "keyturn$ext exists"             test -f "$k"
check "the app exists"                 test -f "$app"
check "keyturn can locate the app"     exit_is 0 "$k" watch install --print
check "--version is $version"          test "$("$k" --version)" = "$version"
check "add defines a secret"           exit_is 0 "$k" add smoke 12h "ci smoke"
check "list --json shows it"           bash -c "\"$k\" list --json | grep -q '\"name\":\"smoke\"'"
check "get with no value exits 2"      exit_is 2 "$k" get smoke
check "status of unknown exits 1"      exit_is 1 "$k" status nope
check "due exits 3 when due"           exit_is 3 "$k" due

# The app, against an empty config: nothing is due, so it must exit 0 without opening a window.
app_silent() { KEYTURN_CONFIG_DIR=$(mktemp -d) "$app"; }
app_selftest() {
  local log; log=$(mktemp)
  KEYTURN_SELFTEST_LOG=$log "$app" --self-test >/dev/null 2>&1 || { cat "$log" 2>/dev/null; return 1; }
}
# The background watcher, in an isolated config dir: it starts, a second copy refuses to run, `watch status` sees it,
# and `watch stop` ends it. (Nothing here opens a window: no secret is ever due.)
watcher_lifecycle() (
  export KEYTURN_CONFIG_DIR
  KEYTURN_CONFIG_DIR=$(mktemp -d)
  "$app" --watch >/dev/null 2>&1 &
  pid=$!
  up=0
  for _ in $(seq 1 60); do
    "$k" watch status >/dev/null 2>&1 && { up=1; break; }
    sleep 0.25
  done
  [ "$up" = 1 ] || { kill "$pid" 2>/dev/null; echo "       the watcher never reported running"; return 1; }
  "$app" --watch >/dev/null 2>&1 || { echo "       a second watcher did not exit cleanly"; return 1; }   # exits 0 at once: one per user
  "$k" watch status --json | grep -q '"running": *true' || { echo "       status --json does not say running"; return 1; }
  "$k" watch stop >/dev/null 2>&1 || { echo "       watch stop failed"; return 1; }
  "$k" watch status >/dev/null 2>&1
  [ $? -eq 2 ] || { echo "       the watcher is still reported running after stop"; return 1; }
  wait "$pid" 2>/dev/null
  return 0
)

check "app exits silently when nothing is due"        exit_is 0 app_silent
check "the watcher starts, runs once, and stops"      watcher_lifecycle
check "app's bundled Skia/HarfBuzz natives load"      app_selftest

if [ -f "$bundle_exe" ]; then
  echo "-- macOS bundle"
  b="$dir/Keyturn.app"
  check "Info.plist is present"               test -f "$b/Contents/Info.plist"
  check "the icon is present"                 test -f "$b/Contents/Resources/keyturn.icns"
  # -f, not -e: on a case-insensitive volume "Keyturn.App" resolves to the Keyturn.app folder, which exists.
  check "no loose bare executable beside it"  test ! -f "$dir/Keyturn.App"
  if command -v codesign >/dev/null 2>&1; then
    check "the bundle's signature verifies"   codesign --verify --strict "$b"
  fi
fi

if [ "$mode" = "store" ]; then
  echo "-- credential store round trip"
  check "set stores a value"           bash -c "printf 'ci-secret' | \"$k\" set smoke"
  check "get returns it"               test "$("$k" get smoke)" = "ci-secret"
  check "remove deletes it"            exit_is 0 "$k" remove smoke
fi

[ "$failures" -eq 0 ] && echo "smoke: all passed" || { echo "smoke: $failures failed"; exit 1; }
