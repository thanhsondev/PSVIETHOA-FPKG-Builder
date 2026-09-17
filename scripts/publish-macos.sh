#!/bin/zsh
# Đóng gói ứng dụng macOS (.app) + CLI cho Apple Silicon và Intel.
# Cách dùng:  scripts/publish-macos.sh [osx-arm64|osx-x64 ...]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP_NAME="PSVIETHOA FPKG Builder"
BUNDLE_ID="vn.psviethoa.fpkgbuilder"
EXECUTABLE="PsViethoa.FpkgBuilder.App"
VERSION=$(grep -oE '<Version>[^<]+' "$ROOT/Directory.Build.props" | sed 's/<Version>//')
RIDS=("$@")
if [ ${#RIDS[@]} -eq 0 ]; then RIDS=(osx-arm64 osx-x64); fi

for RID in "${RIDS[@]}"; do
  OUT="$ROOT/dist/$RID"
  echo "==> Publish $RID -> $OUT"
  rm -rf "$OUT"
  mkdir -p "$OUT"

  dotnet publish "$ROOT/src/PsViethoa.FpkgBuilder.App/PsViethoa.FpkgBuilder.App.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishReadyToRun=true -p:DebugType=none -p:UseAppHost=true \
    -o "$OUT/publish-app"

  dotnet publish "$ROOT/src/PsViethoa.FpkgBuilder.Cli/PsViethoa.FpkgBuilder.Cli.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishReadyToRun=true -p:DebugType=none \
    -o "$OUT/fpkg-cli"

  APP="$OUT/$APP_NAME.app"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp -R "$OUT/publish-app/." "$APP/Contents/MacOS/"
  cp "$ROOT/src/PsViethoa.FpkgBuilder.App/Assets/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"

  # SDK Sony (Publishing Tools 2.79 đã vá, bộ công cụ sdk-fpkg729-fix) + Wine x86-64 đã cắt gọn để chạy nó trên macOS.
  # fpkg-cli nằm cạnh .app nên tìm thấy cả hai trong Contents/Resources, không phải chép hai lần.
  cp -R "$ROOT/libs/sony-sdk" "$APP/Contents/Resources/sony-sdk"
  WINE_DIR="$("$ROOT/scripts/fetch-wine.sh")"
  cp -R "$WINE_DIR" "$APP/Contents/Resources/wine"

  cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>$APP_NAME</string>
  <key>CFBundleDisplayName</key><string>$APP_NAME</string>
  <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>$EXECUTABLE</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>© 2026 PSVIETHOA — Nguyễn Thanh Sơn &amp; Ngô Phi Phương · Main project: Drakmor (LibProsperoPkg)</string>
</dict>
</plist>
PLIST

  # Dọn tệp thừa TRƯỚC khi ký: ký xong mới xoá thì chữ ký hỏng vì thiếu tài nguyên đã niêm phong.
  find "$APP/Contents/MacOS" "$OUT/fpkg-cli" \( -name "*.pdb" -o -name "LibProsperoPkg.xml" \) -delete

  # Ký ad-hoc để macOS cho phép chạy (người dùng vẫn cần chuột phải > Mở lần đầu nếu tải qua mạng).
  codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || echo "   (bỏ qua codesign)"
  rm -rf "$OUT/publish-app"

  (cd "$OUT" && rm -f "$APP_NAME-$VERSION-$RID.zip" && zip -qry "$APP_NAME-$VERSION-$RID.zip" "$APP_NAME.app" fpkg-cli)
  echo "   -> $OUT/$APP_NAME-$VERSION-$RID.zip"
done

echo "Xong."
