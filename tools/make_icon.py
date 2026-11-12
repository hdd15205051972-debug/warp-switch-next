"""Draws the WARP 新开关 app icon (blue tile, white shield, blue check) and saves assets/AppIcon.ico.

Same design as the macOS version's icon. Run: python3 tools/make_icon.py
"""
import math
import sys
from pathlib import Path

from PIL import Image, ImageDraw

S = 1024
SS = 4  # supersampling for smooth edges


def bezier(p0, p1, p2, p3, steps=64):
    pts = []
    for i in range(steps + 1):
        t = i / steps
        u = 1 - t
        x = u**3 * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t**3 * p3[0]
        y = u**3 * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t**3 * p3[1]
        pts.append((x, y))
    return pts


def draw(size_px=S * SS):
    k = size_px / S
    img = Image.new("RGBA", (size_px, size_px), (0, 0, 0, 0))

    # Gradient tile: light blue (top) to deep blue (bottom), angled like the Mac icon.
    tile = Image.new("RGBA", (size_px, size_px))
    top, bottom = (36, 122, 255), (10, 48, 161)
    angle = math.radians(-65)
    dx, dy = math.cos(angle), -math.sin(angle)
    span = abs(dx) + abs(dy)
    px = tile.load()
    for y in range(size_px):
        for x in range(size_px):
            t = ((x / size_px - 0.5) * dx + (y / size_px - 0.5) * dy) / span + 0.5
            t = min(1, max(0, t))
            px[x, y] = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
    mask = Image.new("L", (size_px, size_px), 0)
    ImageDraw.Draw(mask).rounded_rectangle((38 * k, 38 * k, 986 * k, 986 * k), radius=215 * k, fill=255)
    img.paste(tile, (0, 0), mask)

    d = ImageDraw.Draw(img)
    P = lambda x, y: (x * k, y * k)
    shield = [P(512, 224), P(750, 320), P(750, 534)]
    shield += [P(*p) for p in bezier((750, 534), (750, 674), (630, 749), (512, 803))[1:]]
    shield += [P(*p) for p in bezier((512, 803), (395, 749), (274, 674), (274, 534))[1:]]
    shield += [P(274, 320)]
    d.polygon(shield, fill=(255, 255, 255, 255))

    check = [P(390, 514), P(485, 604), P(640, 404)]
    w = int(52 * k)
    blue = (20, 89, 222, 255)
    d.line(check, fill=blue, width=w, joint="curve")
    for cx, cy in (check[0], check[2]):
        d.ellipse((cx - w / 2, cy - w / 2, cx + w / 2, cy + w / 2), fill=blue)
    return img


def main():
    out = Path(sys.argv[1] if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent / "assets" / "AppIcon.ico")
    big = draw(512).resize((256, 256), Image.LANCZOS)
    sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
    out.parent.mkdir(parents=True, exist_ok=True)
    big.save(out, format="ICO", sizes=sizes)
    print("wrote", out)


if __name__ == "__main__":
    main()
