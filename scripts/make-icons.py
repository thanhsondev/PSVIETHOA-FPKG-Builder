#!/usr/bin/env python3
"""Tạo bộ icon ứng dụng từ logo PSVH (logo của psviethoa.com: biểu tượng PlayStation + chữ VH + cờ Việt Nam) bằng Pillow.

Cách dùng:  python3 scripts/make-icons.py [đường/dẫn/logo-light.png]
Nguồn:      docs/logo-light.png (chữ VH đen, nền trong suốt, 1024×569) — bản docs/logo-dark.png (chữ VH trắng) dùng trên nền tối
            trong app (Assets/logo-dark.png, thanh bên).
Kết quả:    src/PsViethoa.FpkgBuilder.App/Assets/icon-*.png, app.ico (Windows), AppIcon.iconset/ và AppIcon.icns (macOS, qua iconutil),
            Assets/logo-dark.png.

Logo nằm ngang nên icon là một ô vuông bo góc kiểu macOS nền trắng, logo đặt giữa, rộng 84 % ô.
"""
from __future__ import annotations

import shutil
import subprocess
import sys
from pathlib import Path

try:
    from PIL import Image, ImageDraw
except ImportError:  # pragma: no cover
    sys.exit("Cần Pillow: pip3 install pillow")

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "PsViethoa.FpkgBuilder.App" / "Assets"
ASSETS.mkdir(parents=True, exist_ok=True)

SIZE = 1024
LOGO_WIDTH = 0.84           # logo rộng 84 % ô icon
CORNER = 0.2237             # bán kính bo góc kiểu macOS (≈ 229 px ở 1024)
TILE = (255, 255, 255)
SIZES = [16, 32, 48, 64, 128, 256, 512, 1024]


def rounded_mask(size: int, radius: int) -> Image.Image:
    mask = Image.new("L", (size * 4, size * 4), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size * 4 - 1, size * 4 - 1), radius=radius * 4, fill=255)
    return mask.resize((size, size), Image.LANCZOS)


def build_icon(logo: Image.Image) -> Image.Image:
    logo = logo.crop(logo.getbbox())
    width = round(SIZE * LOGO_WIDTH)
    height = round(logo.height * width / logo.width)
    logo = logo.resize((width, height), Image.LANCZOS)
    tile = Image.new("RGBA", (SIZE, SIZE), TILE + (255,))
    tile.alpha_composite(logo, ((SIZE - width) // 2, (SIZE - height) // 2))
    icon = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    icon.paste(tile, (0, 0), rounded_mask(SIZE, round(SIZE * CORNER)))
    return icon


def main() -> None:
    logo_path = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "docs" / "logo-light.png"
    icon = build_icon(Image.open(logo_path).convert("RGBA"))

    for sz in SIZES:
        (icon if sz == SIZE else icon.resize((sz, sz), Image.LANCZOS)).save(ASSETS / f"icon-{sz}.png")

    # Windows .ico (nhiều cỡ, 256 nén PNG)
    icon.resize((256, 256), Image.LANCZOS).save(
        ASSETS / "app.ico",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )

    # macOS .iconset → .icns
    iconset = ASSETS / "AppIcon.iconset"
    iconset.mkdir(exist_ok=True)
    for old in iconset.glob("*.png"):
        old.unlink()
    for sz in (16, 32, 128, 256, 512):
        icon.resize((sz, sz), Image.LANCZOS).save(iconset / f"icon_{sz}x{sz}.png")
        icon.resize((sz * 2, sz * 2), Image.LANCZOS).save(iconset / f"icon_{sz}x{sz}@2x.png")

    if sys.platform == "darwin":
        subprocess.run(["iconutil", "-c", "icns", str(iconset), "-o", str(ASSETS / "AppIcon.icns")], check=True)
        print("AppIcon.icns ok")
    else:
        print("Chạy trên macOS để tạo AppIcon.icns: iconutil -c icns AppIcon.iconset")

    # Logo chữ trắng cho thanh bên tối.
    dark = logo_path.with_name("logo-dark.png")
    if dark.exists():
        shutil.copyfile(dark, ASSETS / "logo-dark.png")

    print(f"Icon từ {logo_path.name} → {ASSETS}")


if __name__ == "__main__":
    main()
