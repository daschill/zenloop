#!/usr/bin/env python3
"""Generate branded MSIX placeholder PNGs into scripts/msix-assets/."""
from __future__ import annotations

import os
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.join(os.path.dirname(__file__), "msix-assets")
BG = (11, 13, 16, 255)
ACCENT = (255, 106, 42, 255)
CYAN = (62, 197, 255, 255)
TEXT = (232, 238, 245, 255)


def make_square(size: int, path: str) -> None:
    img = Image.new("RGBA", (size, size), BG)
    d = ImageDraw.Draw(img)
    bar_w = max(2, size // 20)
    d.rectangle([0, 0, bar_w, size], fill=ACCENT)
    margin = size // 5
    stroke = max(2, size // 12)
    d.line([(margin, margin), (size - margin, margin)], fill=CYAN, width=stroke)
    d.line([(size - margin, margin), (margin, size - margin)], fill=ACCENT, width=stroke)
    d.line([(margin, size - margin), (size - margin, size - margin)], fill=CYAN, width=stroke)
    img.save(path, "PNG")


def make_wide(w: int, h: int, path: str) -> None:
    img = Image.new("RGBA", (w, h), BG)
    d = ImageDraw.Draw(img)
    bar_w = max(3, h // 12)
    d.rectangle([0, 0, bar_w, h], fill=ACCENT)
    side = h
    margin = side // 5
    stroke = max(2, side // 12)
    d.line([(margin + bar_w, margin), (side - margin, margin)], fill=CYAN, width=stroke)
    d.line([(side - margin, margin), (margin + bar_w, side - margin)], fill=ACCENT, width=stroke)
    d.line([(margin + bar_w, side - margin), (side - margin, side - margin)], fill=CYAN, width=stroke)
    try:
        font = ImageFont.truetype(
            "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", max(18, h // 3)
        )
    except OSError:
        font = ImageFont.load_default()
    text = "ZENLOOP"
    bbox = d.textbbox((0, 0), text, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    x = side + (w - side - tw) // 2
    y = (h - th) // 2 - 2
    d.text((x, y), text, fill=TEXT, font=font)
    img.save(path, "PNG")


def main() -> None:
    os.makedirs(OUT, exist_ok=True)
    make_square(50, os.path.join(OUT, "StoreLogo.png"))
    make_square(150, os.path.join(OUT, "Square150x150Logo.png"))
    make_square(44, os.path.join(OUT, "Square44x44Logo.png"))
    make_wide(310, 150, os.path.join(OUT, "Wide310x150Logo.png"))
    print("Wrote assets to", OUT)


if __name__ == "__main__":
    main()
