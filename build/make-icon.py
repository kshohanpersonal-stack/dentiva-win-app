#!/usr/bin/env python3
"""Dentiva application icon pipeline.

Takes assets/icon-source.png (an opaque, roughly square render of the corporate mark)
and produces a transparent, evenly padded multi-resolution Windows icon plus the
individual PNG sizes used by documentation and the installer.

Re-runnable: every output is regenerated from the source each time.

Requires Pillow:  pip install --break-system-packages pillow
"""

from __future__ import annotations

import sys
from pathlib import Path

try:
    from PIL import Image, ImageChops
except ModuleNotFoundError:  # pragma: no cover - dependency guidance
    sys.exit("Pillow is required: pip install --break-system-packages pillow")

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "assets" / "icon-source.png"
ICO = ROOT / "assets" / "dentiva.ico"
LOGO = ROOT / "assets" / "dentiva-logo.png"
ICON_DIR = ROOT / "assets" / "icon"

# Windows shows the icon at these sizes across Explorer, the taskbar and Alt-Tab.
SIZES = [16, 24, 32, 48, 64, 128, 256]

# Fraction of the canvas left empty around the mark so it is not clipped by
# the circular/rounded masks some Windows surfaces apply.
PADDING = 0.085

# Pixels brighter than this in all channels are treated as background.
WHITE_CUTOFF = 246


def load_source() -> Image.Image:
    if not SOURCE.exists():
        sys.exit(f"missing source image: {SOURCE}")
    return Image.open(SOURCE).convert("RGBA")


def key_out_background(image: Image.Image) -> Image.Image:
    """Make the near-white background transparent, keeping the mark's own pixels."""
    pixels = image.load()
    width, height = image.size
    for y in range(height):
        for x in range(width):
            r, g, b, a = pixels[x, y]
            if r >= WHITE_CUTOFF and g >= WHITE_CUTOFF and b >= WHITE_CUTOFF:
                pixels[x, y] = (r, g, b, 0)
    return image


def trim(image: Image.Image) -> Image.Image:
    """Crop to the opaque bounding box so padding is measured from the real mark."""
    alpha = image.getchannel("A")
    bbox = alpha.getbbox()
    if bbox:
        return image.crop(bbox)

    # Fall back to a luminance-based trim if the image has no alpha content.
    grey = image.convert("L")
    diff = ImageChops.invert(grey)
    bbox = diff.getbbox()
    return image.crop(bbox) if bbox else image


def centre_on_square(image: Image.Image) -> Image.Image:
    """Place the trimmed mark on a transparent square canvas with even padding."""
    side = max(image.size)
    canvas_side = int(round(side / (1.0 - 2 * PADDING)))
    canvas = Image.new("RGBA", (canvas_side, canvas_side), (0, 0, 0, 0))
    canvas.paste(
        image,
        ((canvas_side - image.width) // 2, (canvas_side - image.height) // 2),
        image,
    )
    return canvas


def main() -> None:
    mark = centre_on_square(trim(key_out_background(load_source())))

    ICON_DIR.mkdir(parents=True, exist_ok=True)
    for size in SIZES:
        mark.resize((size, size), Image.LANCZOS).save(ICON_DIR / f"dentiva-{size}.png")

    # A single .ico carrying every size, so Windows never has to rescale.
    mark.resize((256, 256), Image.LANCZOS).save(
        ICO, format="ICO", sizes=[(s, s) for s in SIZES]
    )

    mark.resize((512, 512), Image.LANCZOS).save(LOGO)

    print(f"wrote {ICO.relative_to(ROOT)} ({', '.join(str(s) for s in SIZES)})")
    print(f"wrote {LOGO.relative_to(ROOT)} (512)")
    print(f"wrote {len(SIZES)} PNGs to {ICON_DIR.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
