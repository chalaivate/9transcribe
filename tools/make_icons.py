#!/usr/bin/env python3
"""Generate the 9Transcribe .ico assets.

Pure standard library: renders each icon with 4x supersampling and writes a
multi-size (16/20/24/32/48/64) 32-bit BGRA .ico. Re-run after changing a colour
or the glyph geometry:

    python3 tools/make_icons.py

Output: src/9Transcribe/Assets/{app,tray-idle,tray-rec,tray-busy,tray-off}.ico
"""

from __future__ import annotations

import math
import os
import struct

SIZES = (16, 20, 24, 32, 48, 64)
SS = 4  # supersampling factor

OUT_DIR = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "src", "9Transcribe", "Assets",
)

# (background, glyph) as RGB tuples.
VARIANTS = {
    "app":       ((0x25, 0x63, 0xEB), (0xFF, 0xFF, 0xFF)),
    "tray-idle": ((0x25, 0x63, 0xEB), (0xFF, 0xFF, 0xFF)),
    "tray-rec":  ((0xDC, 0x26, 0x26), (0xFF, 0xFF, 0xFF)),
    "tray-busy": ((0xF5, 0x9E, 0x0B), (0xFF, 0xFF, 0xFF)),
    "tray-off":  ((0x6B, 0x72, 0x80), (0xE5, 0xE7, 0xEB)),
}


def rounded_rect(px: float, py: float, cx: float, cy: float,
                 hw: float, hh: float, r: float) -> bool:
    """True when (px, py) is inside a rounded rectangle."""
    r = min(r, hw, hh)
    dx = abs(px - cx) - (hw - r)
    dy = abs(py - cy) - (hh - r)
    if dx <= 0 and dy <= 0:
        return True
    dx = max(dx, 0.0)
    dy = max(dy, 0.0)
    return math.hypot(dx, dy) <= r


def render(size: int, bg: tuple[int, int, int], fg: tuple[int, int, int]) -> bytes:
    """Render one icon as BGRA bytes, top-down, size x size."""
    n = size * SS
    s = float(n)

    # Glyph geometry, all relative to the icon edge length.
    cap_cx, cap_cy = s * 0.5, s * 0.335
    cap_hw, cap_hh = s * 0.115, s * 0.205
    arc_cx, arc_cy = s * 0.5, s * 0.395
    arc_r, arc_t = s * 0.250, s * 0.058
    stem_top, stem_bot = s * 0.645, s * 0.760
    stem_hw = s * 0.030
    base_cy, base_hw, base_hh = s * 0.782, s * 0.140, s * 0.030
    bg_r = s * 0.235

    # Accumulate supersampled coverage per output pixel.
    cover_bg = [0] * (size * size)
    cover_fg = [0] * (size * size)

    for y in range(n):
        py = y + 0.5
        oy = y // SS
        row = oy * size
        for x in range(n):
            px = x + 0.5
            if not rounded_rect(px, py, s * 0.5, s * 0.5, s * 0.5, s * 0.5, bg_r):
                continue
            idx = row + x // SS
            cover_bg[idx] += 1

            inside_fg = rounded_rect(px, py, cap_cx, cap_cy, cap_hw, cap_hh, cap_hw)
            if not inside_fg and py >= arc_cy:
                d = math.hypot(px - arc_cx, py - arc_cy)
                inside_fg = abs(d - arc_r) <= arc_t * 0.5
            if not inside_fg and stem_top <= py <= stem_bot:
                inside_fg = abs(px - s * 0.5) <= stem_hw
            if not inside_fg:
                inside_fg = rounded_rect(px, py, s * 0.5, base_cy, base_hw, base_hh, base_hh)
            if inside_fg:
                cover_fg[idx] += 1

    per_px = SS * SS
    out = bytearray()
    for i in range(size * size):
        a_bg = cover_bg[i] / per_px
        a_fg = cover_fg[i] / per_px
        if a_bg <= 0.0:
            out += b"\x00\x00\x00\x00"
            continue
        # Glyph is painted over the background, then the whole thing is
        # alpha-composited against transparency by a_bg.
        r = fg[0] * a_fg + bg[0] * (1.0 - a_fg)
        g = fg[1] * a_fg + bg[1] * (1.0 - a_fg)
        b = fg[2] * a_fg + bg[2] * (1.0 - a_fg)
        out += bytes((int(b + 0.5), int(g + 0.5), int(r + 0.5), int(a_bg * 255 + 0.5)))
    return bytes(out)


def bmp_payload(size: int, bgra_topdown: bytes) -> bytes:
    """BITMAPINFOHEADER + bottom-up BGRA + (zeroed) AND mask."""
    header = struct.pack(
        "<IiiHHIIiiII",
        40,            # biSize
        size,          # biWidth
        size * 2,      # biHeight (XOR + AND)
        1,             # biPlanes
        32,            # biBitCount
        0,             # biCompression = BI_RGB
        size * size * 4,
        0, 0, 0, 0,
    )
    stride = size * 4
    rows = [bgra_topdown[y * stride:(y + 1) * stride] for y in range(size)]
    xor = b"".join(reversed(rows))
    mask_stride = ((size + 31) // 32) * 4
    and_mask = b"\x00" * (mask_stride * size)
    return header + xor + and_mask


def write_ico(path: str, images: list[tuple[int, bytes]]) -> None:
    count = len(images)
    entries = bytearray()
    blobs = bytearray()
    offset = 6 + 16 * count
    for size, payload in images:
        entries += struct.pack(
            "<BBBBHHII",
            size if size < 256 else 0,
            size if size < 256 else 0,
            0, 0, 1, 32,
            len(payload), offset,
        )
        blobs += payload
        offset += len(payload)
    with open(path, "wb") as fh:
        fh.write(struct.pack("<HHH", 0, 1, count))
        fh.write(bytes(entries))
        fh.write(bytes(blobs))


def main() -> None:
    os.makedirs(OUT_DIR, exist_ok=True)
    for name, (bg, fg) in VARIANTS.items():
        images = [(size, bmp_payload(size, render(size, bg, fg))) for size in SIZES]
        path = os.path.join(OUT_DIR, f"{name}.ico")
        write_ico(path, images)
        print(f"wrote {path} ({os.path.getsize(path)} bytes)")


if __name__ == "__main__":
    main()
