#!/bin/sh
# Builds Keyturn from this checkout and installs it for the current user (no root needed).
#
#   ./install.sh       build and install, and start the background watcher
#   ./install.sh -u    uninstall
#
# Everything goes to fixed locations:
#   Linux   ~/.local/bin/keyturn and Keyturn.App, plus a menu entry and icons under ~/.local/share
#   macOS   ~/.local/bin/keyturn, and Keyturn.app (with its icon) in ~/Applications
# It also sets up the background watcher that pops the prompt up when a secret expires (a user service on Linux, a launch
# agent on macOS), and starts it. Remove it with `keyturn watch uninstall`, or everything with ./install.sh -u.
# Your secrets and config are never touched. Prefer a prebuilt binary? Download an archive from the Releases page.
set -eu

CONFIGURATION="Release"
UNINSTALL=0
VERSION="0.0.0-src"
BIN_DIR="$HOME/.local/bin"
DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}"
APPS_DIR="$HOME/Applications"
ICON_SIZES="16 24 32 48 64 128 256 512"

print_help() {
    echo "Usage: ./install.sh [options]"
    echo ""
    echo "Options:"
    echo "  -c <config>   Build configuration (default: Release)"
    echo "  -u            Uninstall"
    echo "  -h            Show this help message"
    echo ""
    echo "Examples:"
    echo "  ./install.sh"
    echo "  ./install.sh -u"
}

while getopts "c:uh" opt; do
    case $opt in
        c) CONFIGURATION="$OPTARG" ;;
        u) UNINSTALL=1 ;;
        h) print_help; exit 0 ;;
        ?) print_help; exit 1 ;;
    esac
done

case "$(uname -s)" in
    Linux)  OS=linux ;;
    Darwin) OS=macos ;;
    *) echo "error: unsupported OS (on Windows use install.ps1)" >&2; exit 1 ;;
esac
case "$OS/$(uname -m)" in
    linux/x86_64)              RID=linux-x64 ;;
    linux/aarch64|linux/arm64) RID=linux-arm64 ;;
    macos/arm64)               RID=osx-arm64 ;;
    macos/x86_64)              RID=osx-x64 ;;
    *) echo "error: unsupported CPU architecture '$(uname -m)'" >&2; exit 1 ;;
esac

refresh_caches() {
    command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t -f "$DATA_DIR/icons/hicolor" 2>/dev/null || true
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q "$DATA_DIR/applications" 2>/dev/null || true
}

if [ "$UNINSTALL" -eq 1 ]; then
    # Stop the watcher and remove it from the login items first, while the CLI that knows how still exists.
    [ -x "$BIN_DIR/keyturn" ] && "$BIN_DIR/keyturn" watch uninstall >/dev/null 2>&1 || true
    rm -f "$BIN_DIR/keyturn" "$BIN_DIR/Keyturn.App"
    if [ "$OS" = linux ]; then
        rm -f "$DATA_DIR/applications/keyturn.desktop" "$DATA_DIR/icons/hicolor/scalable/apps/keyturn.svg"
        for size in $ICON_SIZES; do
            rm -f "$DATA_DIR/icons/hicolor/${size}x${size}/apps/keyturn.png"
        done
        refresh_caches
    else
        rm -rf "$APPS_DIR/Keyturn.app"
    fi
    echo "Uninstalled Keyturn. Your secrets and config are untouched."
    exit 0
fi

command -v dotnet >/dev/null 2>&1 || { echo "error: the .NET 10 SDK is required: https://dotnet.microsoft.com/download" >&2; exit 1; }

ROOT=$(cd "$(dirname "$0")" && pwd)
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

publish() {
    echo "Building $1..."
    dotnet publish "$ROOT/src/$1/$1.csproj" \
        -c "$CONFIGURATION" \
        -r "$RID" \
        -p:PublishProfile=release \
        -p:Version="$VERSION" \
        --nologo -v q \
        -o "$2" >"$TMP/$1.log" 2>&1 || { cat "$TMP/$1.log" >&2; echo "error: building $1 failed" >&2; exit 1; }
}
publish Keyturn.Cli "$TMP/cli"
publish Keyturn.App "$TMP/app"

# Copy then rename, so replacing a running binary works instead of failing with "text file busy".
install_file() {
    cp "$1" "$2.new" && chmod 755 "$2.new" && mv -f "$2.new" "$2"
}

mkdir -p "$BIN_DIR"

# A running watcher would keep running the old version after an upgrade, so stop it before replacing anything.
[ -x "$BIN_DIR/keyturn" ] && "$BIN_DIR/keyturn" watch stop >/dev/null 2>&1 || true

install_file "$TMP/cli/keyturn" "$BIN_DIR/keyturn"

if [ "$OS" = macos ]; then
    # The Dock and Finder only take an icon from a bundle, so wrap the app (and ad-hoc sign it).
    "$ROOT/scripts/make-macos-app.sh" "$TMP/app" "$VERSION" >"$TMP/bundle.log" 2>&1 || { cat "$TMP/bundle.log" >&2; echo "error: creating Keyturn.app failed" >&2; exit 1; }
    mkdir -p "$APPS_DIR"
    rm -rf "$APPS_DIR/Keyturn.app.new"
    cp -R "$TMP/app/Keyturn.app" "$APPS_DIR/Keyturn.app.new"
    rm -rf "$APPS_DIR/Keyturn.app"
    mv "$APPS_DIR/Keyturn.app.new" "$APPS_DIR/Keyturn.app"
else
    install_file "$TMP/app/Keyturn.App" "$BIN_DIR/Keyturn.App"

    # Menu entry and icons. StartupWMClass is the window class the app sets, which is how the desktop connects the
    # running window to this entry and its icon.
    for size in $ICON_SIZES; do
        mkdir -p "$DATA_DIR/icons/hicolor/${size}x${size}/apps"
        cp "$ROOT/assets/icons/keyturn-$size.png" "$DATA_DIR/icons/hicolor/${size}x${size}/apps/keyturn.png"
    done
    mkdir -p "$DATA_DIR/icons/hicolor/scalable/apps" "$DATA_DIR/applications"
    cp "$ROOT/assets/keyturn-mark.svg" "$DATA_DIR/icons/hicolor/scalable/apps/keyturn.svg"
    cat > "$DATA_DIR/applications/keyturn.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Keyturn
GenericName=Secret rotation
Comment=Rotating secrets in your OS credential store
Exec="$BIN_DIR/Keyturn.App" --manage
Icon=keyturn
Terminal=false
Categories=System;Security;
Keywords=secrets;passwords;credentials;rotation;
StartupWMClass=Keyturn.App
EOF
    refresh_caches
fi

# Set up the watcher (login item) and start it. Not fatal: Keyturn works without it, it just can't prompt by itself.
WATCHER=""
if "$BIN_DIR/keyturn" watch install >"$TMP/watch.log" 2>&1; then
    WATCHER=1
else
    cat "$TMP/watch.log" >&2
    echo "warning: couldn't set up the background watcher. Try again later with: keyturn watch install" >&2
fi

echo ""
echo "Keyturn installed:"
echo "  $BIN_DIR/keyturn"
if [ "$OS" = macos ]; then
    echo "  $APPS_DIR/Keyturn.app (find it with Spotlight or Launchpad)"
else
    echo "  $BIN_DIR/Keyturn.App (and a \"Keyturn\" entry in your application menu)"
fi
echo ""
[ -n "$WATCHER" ] && echo "The background watcher is set up: Keyturn will pop up as soon as a secret expires. See it with: keyturn watch status"
echo "Next: keyturn add vpn daily@06:00 \"Corporate VPN password\"   (see the README's Quickstart)"
case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *) echo "Make sure $BIN_DIR is on your PATH." ;;
esac
