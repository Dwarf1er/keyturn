#!/usr/bin/env bash
# Publishes the CLI (NativeAOT) and the app (trimmed self-contained single file) for one runtime into a staging
# directory, using each project's `release` publish profile (see src/*/Properties/PublishProfiles).
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

publish() { # publish <project>; the flags live in each project's Properties/PublishProfiles/release.pubxml
  local project=$1
  echo "==> publishing $project ($rid, $version)"
  dotnet publish "$root/src/$project" -r "$rid" -p:PublishProfile=release -p:Version="$version" --nologo -o "$out" 2>&1 | tee -a "$log"
}

publish Keyturn.Cli
publish Keyturn.App

# An AOT/trim warning compiles fine and then breaks at runtime (reflection-based JSON did exactly that).
if grep -E "warning IL[0-9]+" "$log" >/dev/null; then
  echo "error: AOT/trim warnings found; refusing to package:" >&2
  grep -E "warning IL[0-9]+" "$log" | sort -u >&2
  exit 1
fi

# macOS: the Dock and Finder only take an icon from an .app bundle, so wrap the app (and ad-hoc sign it on a Mac).
case "$rid" in
  osx-*) "$root/scripts/make-macos-app.sh" "$out" "$version" ;;
esac

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
