#!/bin/zsh
# Tải Wine cho macOS (bản dựng của Gcenx — https://github.com/Gcenx/macOS_Wine_builds, LGPL-2.1) vào .cache/ rồi cắt gọn
# còn phần cần để chạy prospero-pub-cmd.exe (SDK Sony, x86-64) và orbis-pub-cmd.exe (PS4 Fake PKG Tools, 32-bit).
# In ra đường dẫn thư mục "wine" đã sẵn sàng.
# Cách dùng:  scripts/fetch-wine.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WINE_VERSION="${WINE_VERSION:-11.17}"
WINE_SHA256="${WINE_SHA256:-c2b3a8274dbc594deaa64e40469b607cbc4aa8ef5656dec4c5f6f3dac0da770c}"
CACHE="$ROOT/.cache/wine-$WINE_VERSION"
ARCHIVE="$CACHE/wine-devel-$WINE_VERSION-osx64.tar.xz"
TARGET="$CACHE/wine"

# Bản cắt gọn cũ (trước 3.0.0) bỏ cả thư viện Windows 32-bit: phải giải nén lại để chạy được orbis-pub-cmd.exe của PS4.
if [[ -x "$TARGET/bin/wine" && -d "$TARGET/lib/wine/i386-windows" ]]; then
  echo "$TARGET"
  exit 0
fi

mkdir -p "$CACHE"
if [[ ! -f "$ARCHIVE" ]] || ! echo "$WINE_SHA256  $ARCHIVE" | shasum -a 256 -c --status; then
  echo "==> Tải Wine $WINE_VERSION (~180 MB)" >&2
  curl -fsSL -o "$ARCHIVE" "https://github.com/Gcenx/macOS_Wine_builds/releases/download/$WINE_VERSION/wine-devel-$WINE_VERSION-osx64.tar.xz"
  echo "$WINE_SHA256  $ARCHIVE" | shasum -a 256 -c --status || { echo "SHA-256 của Wine không khớp" >&2; exit 1; }
fi

rm -rf "$CACHE/unpack" "$TARGET"
mkdir -p "$CACHE/unpack"
tar -xf "$ARCHIVE" -C "$CACHE/unpack"
mv "$CACHE/unpack/Wine Devel.app/Contents/Resources/wine" "$TARGET"
rm -rf "$CACHE/unpack"

# SDK Sony (64-bit) và orbis-pub-cmd (32-bit, cần lib/wine/i386-windows) là chương trình dòng lệnh: không cần Gecko/Mono
# (trình duyệt, .NET), Vulkan/SDL hay các tiện ích giao diện. Còn lại ~400 MB thay vì ~720 MB.
rm -rf "$TARGET/share/wine/gecko" "$TARGET/share/wine/mono"
rm -f "$TARGET"/lib/libMoltenVK.dylib "$TARGET"/lib/libSDL2*
rm -f "$TARGET"/bin/{msidb,msiexec,notepad,regedit,regsvr32,winecfg,wineconsole,winedbg,winefile,winemine}
xattr -dr com.apple.quarantine "$TARGET" 2>/dev/null || true

cat > "$TARGET/README-PSVIETHOA.txt" <<TXT
Wine $WINE_VERSION for macOS (x86-64), unmodified binaries from https://github.com/Gcenx/macOS_Wine_builds
(Wine is licensed under the GNU LGPL 2.1 — https://www.winehq.org/license; source: https://gitlab.winehq.org/wine/wine).
Trimmed by PSVIETHOA FPKG Builder (Gecko, Mono, MoltenVK/SDL and GUI tools removed) and used only to run the bundled
command-line publishing tools: Sony Publishing Tools (prospero-pub-cmd.exe, PS5) and PS4 Fake PKG Tools (orbis-pub-cmd.exe).
Archive SHA-256: $WINE_SHA256
TXT

echo "$TARGET"
