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

check "keyturn$ext exists"             test -f "$k"
check "Keyturn.App$ext exists"         test -f "$dir/Keyturn.App$ext"
check "--version is $version"          test "$("$k" --version)" = "$version"
check "add defines a secret"           exit_is 0 "$k" add smoke 12h "ci smoke"
check "list --json shows it"           bash -c "\"$k\" list --json | grep -q '\"name\":\"smoke\"'"
check "get with no value exits 2"      exit_is 2 "$k" get smoke
check "status of unknown exits 1"      exit_is 1 "$k" status nope
check "due exits 3 when due"           exit_is 3 "$k" due

# The app, against an empty config: nothing is due, so it must exit 0 without opening a window.
app_silent() { KEYTURN_CONFIG_DIR=$(mktemp -d) "$dir/Keyturn.App$ext"; }
app_selftest() {
  local log; log=$(mktemp)
  KEYTURN_SELFTEST_LOG=$log "$dir/Keyturn.App$ext" --self-test >/dev/null 2>&1 || { cat "$log" 2>/dev/null; return 1; }
}
check "app exits silently when nothing is due"        exit_is 0 app_silent
check "app's bundled Skia/HarfBuzz natives load"      app_selftest

if [ "$mode" = "store" ]; then
  echo "-- credential store round trip"
  check "set stores a value"           bash -c "printf 'ci-secret' | \"$k\" set smoke"
  check "get returns it"               test "$("$k" get smoke)" = "ci-secret"
  check "remove deletes it"            exit_is 0 "$k" remove smoke
fi

[ "$failures" -eq 0 ] && echo "smoke: all passed" || { echo "smoke: $failures failed"; exit 1; }
