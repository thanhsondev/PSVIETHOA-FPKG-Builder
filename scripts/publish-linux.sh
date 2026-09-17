#!/usr/bin/env bash
# Package the Linux app + CLI as a portable .tar.gz and an AppImage.
#
# Usage:  scripts/publish-linux.sh [linux-x64|linux-arm64 ...]
#         SKIP_APPIMAGE=1 scripts/publish-linux.sh      # tarball only
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP_NAME="PSVIETHOA FPKG Builder"
EXECUTABLE="PsViethoa.FpkgBuilder.App"
VERSION=$(grep -oE '<Version>[^<]+' "$ROOT/Directory.Build.props" | sed 's/<Version>//')
CACHE="$ROOT/.cache"

RIDS=("$@")
if [ ${#RIDS[@]} -eq 0 ]; then RIDS=(linux-x64); fi

command -v dotnet >/dev/null || { echo "dotnet not found — install the .NET 10 SDK." >&2; exit 1; }

# appimagetool is fetched on demand into .cache/ (already gitignored).
# NB: each 'local' is a separate statement — bash expands every word of a single
# 'local a=... b=$a' before binding a, so $a would be unbound under 'set -u'.
ensure_appimagetool() {
  local arch
  local tool
  local url
  arch="$1"
  tool="$CACHE/appimagetool-$arch.AppImage"
  if [ -x "$tool" ]; then echo "$tool"; return 0; fi
  mkdir -p "$CACHE"
  url="https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$arch.AppImage"
  echo "==> Downloading appimagetool ($arch)" >&2
  if command -v curl >/dev/null; then curl -fsSL "$url" -o "$tool"
  elif command -v wget >/dev/null; then wget -qO "$tool" "$url"
  else return 1; fi
  chmod +x "$tool"; echo "$tool"
}

for RID in "${RIDS[@]}"; do
  case "$RID" in
    linux-x64)   APPIMAGE_ARCH=x86_64 ;;
    linux-arm64) APPIMAGE_ARCH=aarch64 ;;
    *) echo "Unsupported RID: $RID" >&2; exit 1 ;;
  esac

  OUT="$ROOT/dist/$RID"
  echo "==> Publish $RID -> $OUT"
  rm -rf "$OUT"; mkdir -p "$OUT"

  dotnet publish "$ROOT/src/PsViethoa.FpkgBuilder.App/PsViethoa.FpkgBuilder.App.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishReadyToRun=true -p:DebugType=none -p:UseAppHost=true \
    -o "$OUT/publish-app"

  dotnet publish "$ROOT/src/PsViethoa.FpkgBuilder.Cli/PsViethoa.FpkgBuilder.Cli.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishReadyToRun=true -p:DebugType=none \
    -o "$OUT/publish-cli"

  find "$OUT" \( -name '*.pdb' -o -name 'LibProsperoPkg.xml' \) -delete

  # ---------- portable tarball ----------
  STAGE="$OUT/psviethoa-fpkg-builder-$VERSION-$RID"
  mkdir -p "$STAGE/bin" "$STAGE/lib/fpkg-cli" "$STAGE/share/icons"
  cp -R "$OUT/publish-app/." "$STAGE/bin/"
  # Separate self-contained publish — needs its own dll/deps/runtimeconfig beside
  # the apphost, so it cannot share a directory with the app.
  cp -R "$OUT/publish-cli/." "$STAGE/lib/fpkg-cli/"
  for size in 16 32 48 64 128 256 512; do
    src="$ROOT/src/PsViethoa.FpkgBuilder.App/Assets/icon-$size.png"
    [ -f "$src" ] && cp "$src" "$STAGE/share/icons/icon-$size.png"
  done

  cat > "$STAGE/psviethoa-fpkg-builder" <<'LAUNCH'
#!/usr/bin/env sh
DIR="$(cd "$(dirname "$0")" && pwd)"
exec "$DIR/bin/PsViethoa.FpkgBuilder.App" "$@"
LAUNCH
  cat > "$STAGE/fpkg-cli" <<'LAUNCH'
#!/usr/bin/env sh
DIR="$(cd "$(dirname "$0")" && pwd)"
exec "$DIR/lib/fpkg-cli/fpkg-cli" "$@"
LAUNCH
  chmod +x "$STAGE/psviethoa-fpkg-builder" "$STAGE/fpkg-cli"

  cat > "$STAGE/install.sh" <<'INSTALL'
#!/usr/bin/env bash
# Register desktop entry + icons for the current user (optional; the app runs without this).
set -euo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICONS="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor"
mkdir -p "$APPS"
for size in 16 32 48 64 128 256 512; do
  src="$DIR/share/icons/icon-$size.png"
  [ -f "$src" ] || continue
  mkdir -p "$ICONS/${size}x${size}/apps"
  cp "$src" "$ICONS/${size}x${size}/apps/psviethoa-fpkg-builder.png"
done
cat > "$APPS/psviethoa-fpkg-builder.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=PSVIETHOA FPKG Builder
Comment=Build PS5 FPKG packages
Exec=$DIR/psviethoa-fpkg-builder %f
Icon=psviethoa-fpkg-builder
Terminal=false
Categories=Development;Utility;
DESKTOP
command -v update-desktop-database >/dev/null && update-desktop-database "$APPS" 2>/dev/null || true
echo "Installed. Run '$DIR/psviethoa-fpkg-builder' or find it in your app menu."
INSTALL
  chmod +x "$STAGE/install.sh"

  cat > "$STAGE/README.txt" <<EOF
PSVIETHOA FPKG Builder $VERSION ($RID)

Run the GUI:   ./psviethoa-fpkg-builder
Run the CLI:   ./fpkg-cli --help
Menu entry:    ./install.sh   (optional)

Optional system packages:
  fuse3    - copy-free mounting of .exfat/.ffpfsc images (no root needed).
             Without it images are extracted to a temp folder instead: slower
             and needs free disk space, but the resulting package is identical.
  systemd  - keeps the machine awake while building (systemd-inhibit).

Check what was detected:  ./fpkg-cli info
EOF

  TARBALL="$OUT/psviethoa-fpkg-builder-$VERSION-$RID.tar.gz"
  tar -C "$OUT" -czf "$TARBALL" "$(basename "$STAGE")"
  echo "   -> $TARBALL"

  # ---------- AppImage ----------
  if [ "${SKIP_APPIMAGE:-0}" = "1" ]; then
    echo "   (skipping AppImage: SKIP_APPIMAGE=1)"
  else
    APPDIR="$OUT/AppDir"
    rm -rf "$APPDIR"
    mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/lib/fpkg-cli" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor"
    cp -R "$OUT/publish-app/." "$APPDIR/usr/bin/"
    # The CLI is a separate self-contained publish with its own .dll/.deps.json/
    # .runtimeconfig.json; dropping just its apphost next to the app's files makes
    # it resolve the APP's runtimeconfig and fail. Keep it in its own directory.
    cp -R "$OUT/publish-cli/." "$APPDIR/usr/lib/fpkg-cli/"

    for size in 16 32 48 64 128 256 512; do
      src="$ROOT/src/PsViethoa.FpkgBuilder.App/Assets/icon-$size.png"
      [ -f "$src" ] || continue
      mkdir -p "$APPDIR/usr/share/icons/hicolor/${size}x${size}/apps"
      cp "$src" "$APPDIR/usr/share/icons/hicolor/${size}x${size}/apps/psviethoa-fpkg-builder.png"
    done
    cp "$ROOT/src/PsViethoa.FpkgBuilder.App/Assets/icon-256.png" "$APPDIR/psviethoa-fpkg-builder.png"
    cp "$ROOT/src/PsViethoa.FpkgBuilder.App/Assets/icon-256.png" "$APPDIR/.DirIcon"

    cat > "$APPDIR/psviethoa-fpkg-builder.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=PSVIETHOA FPKG Builder
Comment=Build PS5 FPKG packages
Exec=psviethoa-fpkg-builder %f
Icon=psviethoa-fpkg-builder
Terminal=false
Categories=Development;Utility;
DESKTOP
    cp "$APPDIR/psviethoa-fpkg-builder.desktop" "$APPDIR/usr/share/applications/"

    # Invoking the AppImage as "fpkg-cli" (or with --cli) runs the CLI instead of the GUI.
    cat > "$APPDIR/AppRun" <<APPRUN
#!/usr/bin/env sh
HERE="\$(dirname "\$(readlink -f "\$0")")"
case "\$(basename "\${ARGV0:-\$0}")" in
  fpkg-cli*) exec "\$HERE/usr/lib/fpkg-cli/fpkg-cli" "\$@" ;;
esac
if [ "\${1:-}" = "--cli" ]; then shift; exec "\$HERE/usr/lib/fpkg-cli/fpkg-cli" "\$@"; fi
exec "\$HERE/usr/bin/$EXECUTABLE" "\$@"
APPRUN
    chmod +x "$APPDIR/AppRun"

    if TOOL=$(ensure_appimagetool "$APPIMAGE_ARCH"); then
      APPIMAGE="$OUT/${APP_NAME// /_}-$VERSION-$APPIMAGE_ARCH.AppImage"
      # appimagetool needs FUSE to run itself; --appimage-extract-and-run avoids that requirement.
      if ARCH="$APPIMAGE_ARCH" "$TOOL" --appimage-extract-and-run "$APPDIR" "$APPIMAGE" >/dev/null 2>&1 \
         || ARCH="$APPIMAGE_ARCH" "$TOOL" "$APPDIR" "$APPIMAGE"; then
        echo "   -> $APPIMAGE"
      else
        echo "   !! appimagetool failed — the tarball above is still valid." >&2
      fi
    else
      echo "   !! could not fetch appimagetool (no curl/wget or no network) — tarball only." >&2
    fi
  fi

  rm -rf "$OUT/publish-app" "$OUT/publish-cli" "$STAGE"
done

echo "Done."
