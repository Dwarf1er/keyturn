#!/usr/bin/env bash
# Regenerates the application icons from assets/keyturn-mark.svg.
#   assets/icons/keyturn-<size>.png   Linux hicolor theme sizes, and the window icon (256)
#   assets/keyturn.ico                Windows executable / shortcut icon (16-256, PNG-compressed at 256)
#   assets/keyturn.icns               macOS app bundle icon (16-1024, as the OS's iconset layout)
# Needs rsvg-convert and python3. Only run this when the logo changes; the outputs are committed.
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
svg="$root/assets/keyturn-mark.svg"
out="$root/assets/icons"
sizes=(16 24 32 48 64 128 256 512 1024)

command -v rsvg-convert >/dev/null || { echo "error: rsvg-convert not found (librsvg)" >&2; exit 1; }
command -v python3 >/dev/null || { echo "error: python3 not found" >&2; exit 1; }

mkdir -p "$out"
for s in "${sizes[@]}"; do
  rsvg-convert -w "$s" -h "$s" "$svg" -o "$out/keyturn-$s.png"
done

# An .ico is a small header plus an image directory. PNG-compressed frames (Windows Vista and later) are about a
# quarter the size of the BMP frames ImageMagick writes, so assemble it directly from the PNGs.
python3 - "$out" "$root/assets/keyturn.ico" <<'PY'
import struct, sys
out, dest = sys.argv[1], sys.argv[2]
sizes = [16, 24, 32, 48, 64, 128, 256]
frames = [(s, open(f"{out}/keyturn-{s}.png", "rb").read()) for s in sizes]
data = struct.pack("<HHH", 0, 1, len(frames))
offset = 6 + 16 * len(frames)
for s, png in frames:
    data += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(png), offset)  # 256 is stored as 0
    offset += len(png)
open(dest, "wb").write(data + b"".join(png for _, png in frames))
PY

# An .icns is a header plus typed chunks, each holding a PNG. These are the chunk types macOS's own `iconutil`
# writes for an iconset (16, 16@2x, 32, 32@2x, 128, 128@2x, 256, 256@2x, 512, 512@2x).
python3 - "$out" "$root/assets/keyturn.icns" <<'PY'
import struct, sys
out, dest = sys.argv[1], sys.argv[2]
chunks = [("icp4", 16), ("ic11", 32), ("icp5", 32), ("ic12", 64), ("ic07", 128), ("ic13", 256),
          ("ic08", 256), ("ic14", 512), ("ic09", 512), ("ic10", 1024)]
body = b""
for kind, px in chunks:
    png = open(f"{out}/keyturn-{px}.png", "rb").read()
    body += kind.encode() + struct.pack(">I", 8 + len(png)) + png
open(dest, "wb").write(b"icns" + struct.pack(">I", 8 + len(body)) + body)
PY

echo "wrote ${#sizes[@]} PNGs to assets/icons/, assets/keyturn.ico and assets/keyturn.icns"
