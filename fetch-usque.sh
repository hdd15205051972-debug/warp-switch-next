#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
VERSION=4.2.1
BASE="https://github.com/Diniboy1123/usque/releases/download/v$VERSION"
mkdir -p build/vendor
for arch in arm64 amd64; do
  name="usque_${VERSION}_darwin_${arch}.zip"
  archive="build/vendor/$name"
  curl -fLsS --retry 2 --connect-timeout 15 --max-time 120 "$BASE/$name" -o "$archive"
  case "$arch" in
    arm64) expected=762e2dc875669566207a3c776a53dc6bb50770da25f90e1ab69fbc53e91f8da1 ;;
    amd64) expected=1eb41e34bf4cd0b81e06222ef844cea75eb56229a62fe29c07cf8e7bd253357d ;;
  esac
  actual=$(shasum -a 256 "$archive" | awk '{print $1}')
  [[ "$actual" == "$expected" ]] || { echo "usque SHA-256 verification failed: $arch" >&2; exit 1; }
  mkdir -p "build/vendor/$arch"
  ditto -x -k "$archive" "build/vendor/$arch"
done
lipo -create build/vendor/arm64/usque build/vendor/amd64/usque -output build/vendor/usque
chmod 755 build/vendor/usque
echo "Verified usque $VERSION (Apple Silicon + Intel)"
