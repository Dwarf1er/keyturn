#!/usr/bin/env bash
# Publishes the CLI (NativeAOT, one native file) and the app (trimmed self-contained single file, with Skia and
# HarfBuzz embedded and extracted by the .NET host on first run) for one runtime into a staging directory.
# Usage: scripts/publish.sh <rid> <version> <staging-dir>
# NativeAOT can't cross-compile between operating systems, so run this on the target OS.
set -euo pipefail

rid=${1:?runtime identifier, e.g. linux-x64}
version=${2:?version, e.g. 1.2.3}
out=${3:?staging directory}
root=$(cd "$(dirname "$0")/.." && pwd)
log=$(mktemp)

rm -rf "$out"
mkdir -p "$out"

publish() { # publish <project> <extra msbuild args...>
  local project=$1; shift
  echo "==> publishing $project ($rid, $version)"
  dotnet publish "$root/src/$project" -c Release -r "$rid" -p:Version="$version" -p:DebugType=none --nologo -o "$out" "$@" 2>&1 | tee -a "$log"
}

# The CLI is on other tools' hot path: NativeAOT gives a ~3 MB file that starts in milliseconds.
publish Keyturn.Cli -p:PublishAot=true

# NativeAOT can't embed native libraries (they'd land beside the exe), so the app uses a self-contained single
# file instead. Compression is on: it halves the size at ~100 ms per launch, irrelevant for a human-facing prompt.
publish Keyturn.App -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true

# An AOT/trim warning compiles fine and then breaks at runtime (reflection-based JSON did exactly that).
if grep -E "warning IL[0-9]+" "$log" >/dev/null; then
  echo "error: AOT/trim warnings found; refusing to package:" >&2
  grep -E "warning IL[0-9]+" "$log" | sort -u >&2
  exit 1
fi

# Debug symbols are tens of MB and not for end users.
find "$out" \( -name '*.pdb' -o -name '*.dbg' -o -name '*.dSYM' \) -prune -exec rm -rf {} +

cp "$root/README.md" "$out/README.md"
{
  echo "Keyturn bundles code from the following projects."
  echo
  echo "== Git Credential Manager (native interop declarations) =="
  cat "$root/src/Keyturn.Core/ThirdParty/GitCredentialManager/LICENSE"
} > "$out/THIRD-PARTY-NOTICES.txt"

echo "==> staged in $out:"
ls -la "$out"
