#!/usr/bin/env bash
# Wraps the published macOS app into Keyturn.app: the Dock and Finder only take an icon from a bundle.
# Usage: scripts/make-macos-app.sh <staging-dir> <version>
#   expects <staging-dir>/Keyturn.App (from `dotnet publish`), replaces it with <staging-dir>/Keyturn.app
# On a Mac the bundle is ad-hoc signed (free, no Apple account). Apple silicon refuses unsigned code, and the
# signature also seals Info.plist and the icon. Notarization (needs a paid Developer ID) is not done.
set -euo pipefail

stage=${1:?staging directory}
version=${2:?version, e.g. 1.2.3}
root=$(cd "$(dirname "$0")/.." && pwd)

exe="$stage/Keyturn.App"
bundle="$stage/Keyturn.app"
[ -f "$exe" ] || { echo "error: $exe not found (publish the app first)" >&2; exit 1; }
[ -f "$root/assets/keyturn.icns" ] || { echo "error: assets/keyturn.icns missing (run scripts/make-icons.sh)" >&2; exit 1; }

# macOS volumes are case-insensitive by default, so "Keyturn.App" (the executable) and "Keyturn.app" (the bundle
# folder) are the SAME name there. Move the executable aside before the bundle path is touched at all: removing or
# creating "Keyturn.app" would otherwise delete or shadow the very file being moved.
aside="$stage/.keyturn-app.tmp"
mv "$exe" "$aside"
rm -rf "$bundle"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
mv "$aside" "$bundle/Contents/MacOS/Keyturn.App"
chmod 755 "$bundle/Contents/MacOS/Keyturn.App"
cp "$root/assets/keyturn.icns" "$bundle/Contents/Resources/keyturn.icns"

# CFBundle version fields must be numeric (up to three integers): drop any -rc.1 style suffix.
short=${version%%-*}

# A normal Dock app (no LSUIElement): the prompt asks for a password, so it should bounce, show in Cmd-Tab and be
# findable if covered. The hourly silent check exits before any UI starts, so it never shows a Dock icon.
cat > "$bundle/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleExecutable</key><string>Keyturn.App</string>
  <key>CFBundleIconFile</key><string>keyturn</string>
  <key>CFBundleIdentifier</key><string>com.antoinepoulin.keyturn</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>Keyturn</string>
  <key>CFBundleDisplayName</key><string>Keyturn</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$short</string>
  <key>CFBundleVersion</key><string>$short</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
  <key>NSHumanReadableCopyright</key><string>Copyright (c) 2026 Antoine Poulin</string>
</dict>
</plist>
PLIST

# Validate the plist: plutil on a Mac, Python's parser elsewhere.
if command -v plutil >/dev/null 2>&1; then
  plutil -lint "$bundle/Contents/Info.plist" >/dev/null
else
  python3 -c "import plistlib,sys; plistlib.load(open(sys.argv[1],'rb'))" "$bundle/Contents/Info.plist"
fi

if command -v codesign >/dev/null 2>&1; then
  codesign --force --sign - "$bundle"
  codesign --verify --strict --verbose=2 "$bundle"
  echo "==> signed (ad-hoc) and verified: $bundle"
else
  echo "==> codesign not found: $bundle left unsigned (fine for structure tests; not for distribution)"
fi
