#!/usr/bin/env bash
# Builds Keyturn from this checkout and installs it for the current user (no root needed).
#
#   ./install.sh                  build and install into ~/.local
#   ./install.sh --schedule       ...and have the prompt appear at login and hourly (systemd user timer)
#   ./install.sh --uninstall      remove what a previous install put on this machine
#
# Linux:  `keyturn` and `Keyturn.App` go into <prefix>/bin, plus a launcher (menu entry + icons) in the XDG data dir.
# macOS:  `keyturn` goes into <prefix>/bin and `Keyturn.app` (ad-hoc signed, with its icon) into ~/Applications, where
#         Spotlight and Launchpad find it.
# Your secrets and config are never touched.
# Prefer a prebuilt binary? Download an archive from the Releases page instead.
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: ./install.sh [options]

  --prefix DIR     install the binaries into DIR/bin (default: ~/.local)
  --schedule       also run `keyturn schedule install` (prompt at login and hourly)
  --no-launcher    don't create the application-menu entry and icons (macOS: keep Keyturn.app beside the CLI)
  --app-dir DIR    macOS only: where Keyturn.app goes (default: ~/Applications)
  --jit            build the CLI without NativeAOT (needs only the .NET SDK; larger and slower to start)
  --uninstall      remove everything a previous install created
  -h, --help       show this help

On Linux the launcher files go to $XDG_DATA_HOME (default ~/.local/share) whatever --prefix is.
EOF
}

prefix="$HOME/.local"
mode=install
launcher=1
schedule=0
force_jit=0
app_dir="$HOME/Applications"

while [ $# -gt 0 ]; do
  case "$1" in
    --prefix) [ $# -ge 2 ] || { echo "error: --prefix needs a directory" >&2; exit 2; }; prefix=$2; shift 2 ;;
    --schedule) schedule=1; shift ;;
    --no-launcher) launcher=0; shift ;;
    --app-dir) [ $# -ge 2 ] || { echo "error: --app-dir needs a directory" >&2; exit 2; }; app_dir=$2; shift 2 ;;
    --jit) force_jit=1; shift ;;
    --uninstall) mode=uninstall; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "error: unknown option '$1'" >&2; usage >&2; exit 2 ;;
  esac
done

root=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
data_home=${XDG_DATA_HOME:-$HOME/.local/share}
bin_dir=$prefix/bin
manifest=$data_home/keyturn/install-manifest
tab=$'\t'

say()  { printf '%s\n' "$*"; }
warn() { printf 'warning: %s\n' "$*" >&2; }
die()  { printf 'error: %s\n' "$*" >&2; exit 1; }

case "$(uname -s)" in
  Linux)  os=linux ;;
  Darwin) os=macos ;;
  *) die "unsupported OS '$(uname -s)'. On Windows use install.ps1." ;;
esac

# ---- uninstall -----------------------------------------------------------------------------------
refresh_caches() {
  [ "$os" = linux ] || return 0
  [ -d "$data_home/icons/hicolor" ] && command -v gtk-update-icon-cache >/dev/null 2>&1 &&
    gtk-update-icon-cache -q -t -f "$data_home/icons/hicolor" 2>/dev/null || true
  [ -d "$data_home/applications" ] && command -v update-desktop-database >/dev/null 2>&1 &&
    update-desktop-database -q "$data_home/applications" 2>/dev/null || true
}

if [ "$mode" = uninstall ]; then
  [ -f "$manifest" ] || die "no install record at $manifest. Nothing to uninstall (or it was installed another way)."

  # Remove the scheduled prompt first, while the CLI that knows how still exists.
  if grep -q "^schedule${tab}1\$" "$manifest" && [ -x "$bin_dir/keyturn" ]; then
    "$bin_dir/keyturn" schedule uninstall || warn "could not remove the scheduled prompt; run 'keyturn schedule uninstall' yourself"
  fi

  while IFS=$'\t' read -r kind path; do
    case "$kind" in
      file)
        rm -f -- "$path" && say "removed $path"
        rmdir -p -- "$(dirname "$path")" 2>/dev/null || true   # tidy directories this install left empty
        ;;
      dir)
        # Only ever remove a bundle this installer created, whatever a damaged record says.
        case "$path" in
          */Keyturn.app) rm -rf -- "$path" && say "removed $path" ;;
          *) warn "refusing to remove unexpected directory '$path'" ;;
        esac
        ;;
    esac
  done < "$manifest"
  rm -f -- "$manifest"
  rmdir "$data_home/keyturn" 2>/dev/null || true
  refresh_caches
  say "Uninstalled. Your secrets and config are untouched (config: ${XDG_CONFIG_HOME:-$HOME/.config}/keyturn)."
  exit 0
fi

# ---- prerequisites -------------------------------------------------------------------------------
command -v dotnet >/dev/null 2>&1 || die ".NET SDK not found. Install the .NET 10 SDK: https://dotnet.microsoft.com/download"
sdk=$(dotnet --version 2>/dev/null || true)
[ "${sdk%%.*}" -ge 10 ] 2>/dev/null || die ".NET SDK $sdk is too old; Keyturn needs 10 or newer."

if [ "$os" = linux ] && ldd --version 2>&1 | grep -qi musl; then
  die "musl-based systems (such as Alpine) aren't supported by this installer yet."
fi
case "$os/$(uname -m)" in
  linux/x86_64|linux/amd64)   rid=linux-x64 ;;
  linux/aarch64|linux/arm64)  rid=linux-arm64 ;;
  macos/arm64)                rid=osx-arm64 ;;
  macos/x86_64)               rid=osx-x64 ;;
  *) die "unsupported CPU architecture '$(uname -m)'." ;;
esac

# The NativeAOT CLI needs a C toolchain. Without one, fall back to the self-contained build instead of failing.
distro_hint() {
  if [ "$os" = macos ]; then echo "xcode-select --install"; return; fi
  local id=""
  # shellcheck disable=SC1091
  [ -r /etc/os-release ] && id=$(. /etc/os-release && printf '%s %s' "${ID:-}" "${ID_LIKE:-}")
  case " $id " in
    *" arch "*)                            echo "sudo pacman -S clang zlib binutils" ;;
    *" debian "*|*" ubuntu "*)             echo "sudo apt install clang zlib1g-dev binutils" ;;
    *" fedora "*|*" rhel "*|*" centos "*)  echo "sudo dnf install clang zlib-devel binutils" ;;
    *" suse "*|*" opensuse"*)              echo "sudo zypper install clang zlib-devel binutils" ;;
    *)                                     echo "install clang, the zlib development headers and binutils" ;;
  esac
}

missing=()
if [ "$force_jit" -eq 0 ]; then
  if [ "$os" = macos ]; then
    # clang is a stub on a Mac without the command line tools (it pops up an install dialog), so ask xcode-select.
    xcode-select -p >/dev/null 2>&1 || missing+=("Xcode command line tools")
  else
    command -v clang >/dev/null 2>&1 || missing+=("clang")
    command -v objcopy >/dev/null 2>&1 || missing+=("objcopy (binutils)")
    if command -v clang >/dev/null 2>&1 && ! printf '#include <zlib.h>\n' | clang -E -x c - >/dev/null 2>&1; then
      missing+=("zlib headers")
    fi
  fi
fi

cli_profile=release
if [ "$force_jit" -eq 1 ]; then
  cli_profile=jit
elif [ "${#missing[@]}" -gt 0 ]; then
  cli_profile=jit
  warn "missing for the NativeAOT build: ${missing[*]}"
  warn "falling back to a self-contained CLI (~15 MB, ~70 ms startup instead of ~3 MB and ~3 ms)."
  warn "for the small, fast build: $(distro_hint), then run this again."
fi

# ---- version -------------------------------------------------------------------------------------
version="0.0.0-src"
if command -v git >/dev/null 2>&1 && git -C "$root" rev-parse --git-dir >/dev/null 2>&1; then
  if tag=$(git -C "$root" describe --tags --exact-match 2>/dev/null) && [[ "${tag#v}" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
    version=${tag#v}
  else
    version="0.0.0-src.$(git -C "$root" rev-parse --short HEAD 2>/dev/null || echo unknown)"
  fi
fi

# ---- build ---------------------------------------------------------------------------------------
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

say "Building Keyturn $version for $rid (CLI: $cli_profile)..."
dotnet publish "$root/src/Keyturn.Cli" -r "$rid" -p:PublishProfile="$cli_profile" -p:Version="$version" --nologo -v q -o "$tmp/cli" >"$tmp/cli.log" 2>&1 ||
  { cat "$tmp/cli.log" >&2; die "building the CLI failed."; }
dotnet publish "$root/src/Keyturn.App" -r "$rid" -p:PublishProfile=release -p:Version="$version" --nologo -v q -o "$tmp/app" >"$tmp/app.log" 2>&1 ||
  { cat "$tmp/app.log" >&2; die "building the app failed."; }
if [ "$os" = macos ]; then
  "$root/scripts/make-macos-app.sh" "$tmp/app" "$version" >"$tmp/bundle.log" 2>&1 ||
    { cat "$tmp/bundle.log" >&2; die "creating Keyturn.app failed."; }
fi

# ---- install -------------------------------------------------------------------------------------
installed=()

# Copy then rename, so replacing a running binary works instead of failing with "text file busy".
put() { # put <mode> <source> <destination>
  mkdir -p "$(dirname "$3")"
  cp -- "$2" "$3.new.$$"
  chmod "$1" "$3.new.$$"
  mv -f -- "$3.new.$$" "$3"
  installed+=("$3")
}

put 755 "$tmp/cli/keyturn" "$bin_dir/keyturn"

# Replace a directory (the macOS bundle) by renaming, so an already-running app keeps working.
put_dir() { # put_dir <source dir> <destination dir>
  mkdir -p "$(dirname "$2")"
  rm -rf -- "$2.new.$$"
  cp -R "$1" "$2.new.$$"
  if [ -e "$2" ]; then mv "$2" "$2.old.$$"; fi
  mv "$2.new.$$" "$2"
  rm -rf -- "$2.old.$$"
  installed_dirs+=("$2")
}
installed_dirs=()

if [ "$os" = macos ]; then
  # The bundle is the launcher (Spotlight, Launchpad, the Dock). Without a launcher, keep it beside the CLI,
  # where `keyturn` looks first.
  [ "$launcher" -eq 1 ] && bundle_dir="$app_dir" || bundle_dir="$bin_dir"
  put_dir "$tmp/app/Keyturn.app" "$bundle_dir/Keyturn.app"
else
  put 755 "$tmp/app/Keyturn.App" "$bin_dir/Keyturn.App"
fi

if [ "$os" = linux ] && [ "$launcher" -eq 1 ]; then
  for size in 16 24 32 48 64 128 256 512; do
    put 644 "$root/assets/icons/keyturn-$size.png" "$data_home/icons/hicolor/${size}x${size}/apps/keyturn.png"
  done
  put 644 "$root/assets/keyturn-mark.svg" "$data_home/icons/hicolor/scalable/apps/keyturn.svg"

  # Exec must quote a path containing spaces. StartupWMClass is the window class the app sets, which is how
  # the desktop connects the running window to this entry and its icon.
  exec_path=$bin_dir/Keyturn.App
  case "$exec_path" in *" "*) exec_path="\"$exec_path\"" ;; esac
  desktop=$tmp/keyturn.desktop
  cat > "$desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Keyturn
GenericName=Secret rotation
Comment=Rotating secrets in your OS credential store
Exec=$exec_path --manage
Icon=keyturn
Terminal=false
Categories=System;Security;
Keywords=secrets;passwords;credentials;rotation;
StartupWMClass=Keyturn.App
EOF
  command -v desktop-file-validate >/dev/null 2>&1 && { desktop-file-validate "$desktop" || warn "the launcher entry failed validation (it may still work)"; }
  put 644 "$desktop" "$data_home/applications/keyturn.desktop"
  refresh_caches
fi

# Record exactly what was installed, so --uninstall removes that and nothing else.
mkdir -p "$(dirname "$manifest")"
{
  for f in "${installed[@]}"; do printf 'file\t%s\n' "$f"; done
  for d in ${installed_dirs[@]+"${installed_dirs[@]}"}; do printf 'dir\t%s\n' "$d"; done
  if [ "$schedule" -eq 1 ]; then printf 'schedule\t1\n'; fi
} > "$manifest"

if [ "$schedule" -eq 1 ]; then
  "$bin_dir/keyturn" schedule install || warn "could not install the scheduled prompt; run 'keyturn schedule install' yourself"
fi

say ""
say "Installed Keyturn $version:"
say "  $bin_dir/keyturn"
if [ "$os" = macos ]; then
  say "  $bundle_dir/Keyturn.app"
  [ "$launcher" -eq 1 ] && say "  (find it with Spotlight or Launchpad; it opens the manage window)"
else
  say "  $bin_dir/Keyturn.App"
  [ "$launcher" -eq 1 ] && say "  application menu entry \"Keyturn\" (opens the manage window)"
fi
case ":$PATH:" in
  *":$bin_dir:"*) ;;
  *) warn "$bin_dir is not on your PATH. Add it, for example: export PATH=\"$bin_dir:\$PATH\"" ;;
esac
say ""
say "Next: keyturn add vpn daily@06:00 \"Corporate VPN password\"   (see the README's Quickstart)"
[ "$schedule" -eq 0 ] && say "      keyturn schedule install   to have the prompt appear when a secret comes due"
exit 0
