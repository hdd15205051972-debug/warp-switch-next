#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
DEST="${1:-build/WARP 新开关.app}"
USQUE="${USQUE_BINARY:-$PWD/build/vendor/usque}"
if [[ ! -x "$USQUE" ]]; then bash fetch-usque.sh; fi
mkdir -p build "$DEST/Contents/MacOS" "$DEST/Contents/Helpers" "$DEST/Contents/Resources"
for arch in arm64 x86_64; do
  swiftc -O -swift-version 5 -module-cache-path "$PWD/build/module-cache" -target "$arch-apple-macos13" \
    Relay.swift Worker.swift App.swift main.swift -o "build/WarpSwitchNext-$arch"
done
lipo -create build/WarpSwitchNext-arm64 build/WarpSwitchNext-x86_64 -output "$DEST/Contents/MacOS/WarpSwitchNext"
cp "$USQUE" "$DEST/Contents/Helpers/usque"
cp LICENSE USQUE-LICENSE.txt README.md "$DEST/Contents/Resources/"
swiftc -swift-version 5 -module-cache-path "$PWD/build/module-cache" Icon.swift -o build/make-icon
build/make-icon build/icon.png
mkdir -p build/AppIcon.iconset
for s in 16 32 128 256 512; do
  sips -z "$s" "$s" build/icon.png --out "build/AppIcon.iconset/icon_${s}x${s}.png" >/dev/null
  d=$((s * 2))
  sips -z "$d" "$d" build/icon.png --out "build/AppIcon.iconset/icon_${s}x${s}@2x.png" >/dev/null
done
python3 - build/AppIcon.iconset "$DEST/Contents/Resources/AppIcon.icns" <<'PY'
import struct,sys
from pathlib import Path
root=Path(sys.argv[1])
chunks=[]
for kind,name in [('icp4','icon_16x16.png'),('icp5','icon_32x32.png'),('icp6','icon_32x32@2x.png'),('ic07','icon_128x128.png'),('ic08','icon_256x256.png'),('ic09','icon_512x512.png'),('ic10','icon_512x512@2x.png'),('ic11','icon_16x16@2x.png'),('ic12','icon_32x32@2x.png'),('ic13','icon_128x128@2x.png'),('ic14','icon_256x256@2x.png')]:
 data=(root/name).read_bytes(); chunks.append(kind.encode()+struct.pack('>I',len(data)+8)+data)
body=b''.join(chunks)
Path(sys.argv[2]).write_bytes(b'icns'+struct.pack('>I',len(body)+8)+body)
PY
cat > "$DEST/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>io.github.warp-switch-next</string>
<key>CFBundleName</key><string>WARP 新开关</string>
<key>CFBundleDisplayName</key><string>WARP 新开关</string>
<key>CFBundleExecutable</key><string>WarpSwitchNext</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleIconFile</key><string>AppIcon</string>
<key>CFBundleShortVersionString</key><string>1.1.0</string>
<key>CFBundleVersion</key><string>2</string>
<key>LSMinimumSystemVersion</key><string>13.0</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
codesign --force -s - "$DEST/Contents/Helpers/usque"
codesign --force -s - "$DEST"
codesign --verify --deep --strict "$DEST"
"$DEST/Contents/MacOS/WarpSwitchNext" --self-test
