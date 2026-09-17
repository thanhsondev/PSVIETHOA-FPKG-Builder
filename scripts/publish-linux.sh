#!/bin/zsh
# Đóng gói bản Linux (x64 và arm64): thư mục app + fpkg-cli + sony-sdk trong một tệp .tar.gz, tự chứa .NET.
# Wine KHÔNG kèm theo (khác macOS): người dùng cài gói wine của distro, ứng dụng tự tìm (/usr/bin/wine, wine64, PATH, PSVIETHOA_WINE).
# Cách dùng:  scripts/publish-linux.sh [linux-x64|linux-arm64 ...]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP_NAME="PSVIETHOA FPKG Builder"
VERSION=$(grep -oE '<Version>[^<]+' "$ROOT/Directory.Build.props" | sed 's/<Version>//')
R2R="${R2R:-true}"   # đặt R2R=false nếu máy build không tải được crossgen2 cho RID Linux
RIDS=("$@")
if [ ${#RIDS[@]} -eq 0 ]; then RIDS=(linux-x64 linux-arm64); fi

for RID in "${RIDS[@]}"; do
  OUT="$ROOT/dist/$RID"
  echo "==> Publish $RID -> $OUT"
  rm -rf "$OUT"
  mkdir -p "$OUT/app" "$OUT/fpkg-cli"

  dotnet publish "$ROOT/src/PsViethoa.FpkgBuilder.App/PsViethoa.FpkgBuilder.App.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishReadyToRun=$R2R -p:DebugType=none -p:UseAppHost=true \
    -o "$OUT/app"

  dotnet publish "$ROOT/src/PsViethoa.FpkgBuilder.Cli/PsViethoa.FpkgBuilder.Cli.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishReadyToRun=$R2R -p:DebugType=none \
    -o "$OUT/fpkg-cli"

  # SDK Sony (Publishing Tools 2.79 đã vá, bộ sdk-fpkg279-fixdss3) cạnh app/ và fpkg-cli/: cả hai tìm ở thư mục cha.
  cp -R "$ROOT/libs/sony-sdk" "$OUT/sony-sdk"
  cp "$ROOT/src/PsViethoa.FpkgBuilder.App/Assets/icon-256.png" "$OUT/psviethoa-fpkg-builder.png"

  cat > "$OUT/install-desktop-entry.sh" <<'SH'
#!/bin/sh
# Thêm "PSVIETHOA FPKG Builder" vào menu ứng dụng của người dùng hiện tại (không cần root). Chạy lại sau khi di chuyển thư mục.
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$HOME/.local/share/applications"
cat > "$HOME/.local/share/applications/psviethoa-fpkg-builder.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=PSVIETHOA FPKG Builder
Comment=Build PS5 FPKG (FIH debug) packages
Exec="$HERE/app/PsViethoa.FpkgBuilder.App"
Icon=$HERE/psviethoa-fpkg-builder.png
Terminal=false
Categories=Utility;Development;
DESKTOP
chmod +x "$HERE/app/PsViethoa.FpkgBuilder.App" "$HERE/fpkg-cli/fpkg-cli" 2>/dev/null || true
echo "Đã thêm vào menu: ~/.local/share/applications/psviethoa-fpkg-builder.desktop"
SH

  cat > "$OUT/README-Linux.txt" <<TXT
PSVIETHOA FPKG Builder $VERSION — Linux ($RID)

Chạy:        ./app/PsViethoa.FpkgBuilder.App        (giao diện)      ./fpkg-cli/fpkg-cli --help   (dòng lệnh)
Menu:        ./install-desktop-entry.sh              (thêm vào menu ứng dụng, không cần root)
Sony SDK:    cần Wine của distro — Debian/Ubuntu: sudo apt install wine64 · Fedora: sudo dnf install wine · Arch: sudo pacman -S wine
             (hoặc PSVIETHOA_WINE=/đường/dẫn/wine). Lần tạo gói đầu tiên tạo WINEPREFIX riêng ở ~/.local/share/psviethoa-fpkg-builder/wine-prefix.
             Không có Wine thì ứng dụng dùng engine tích hợp (bỏ tích "Sony SDK").
Ảnh .exfat:  được giải nén bằng bộ đọc .NET (không gắn ổ); cần dung lượng trống bằng nội dung ảnh.
Chống ngủ:   dùng systemd-inhibit nếu có.
Yêu cầu:     glibc 2.31+ (Ubuntu 20.04+/Debian 11+/Fedora 34+), X11 hoặc XWayland, fontconfig.
TXT

  find "$OUT" \( -name "*.pdb" -o -name "LibProsperoPkg.xml" \) -delete
  chmod +x "$OUT/app/PsViethoa.FpkgBuilder.App" "$OUT/fpkg-cli/fpkg-cli" "$OUT/install-desktop-entry.sh"

  (cd "$OUT" && rm -f "$APP_NAME-$VERSION-$RID.tar.gz" && tar -czf "$APP_NAME-$VERSION-$RID.tar.gz" app fpkg-cli sony-sdk psviethoa-fpkg-builder.png install-desktop-entry.sh README-Linux.txt)
  echo "   -> $OUT/$APP_NAME-$VERSION-$RID.tar.gz"
done

echo "Xong."
