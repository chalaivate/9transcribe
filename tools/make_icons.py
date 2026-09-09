#!/usr/bin/env python3
"""Generate the 9Transcribe .ico assets.

Pure standard library: renders each icon with supersampling and writes a multi-size
32-bit BGRA .ico. The app icon carries 16 to 256 px (Explorer and Alt+Tab on
Windows 11 pick the 256 px image); the tray icons stop at 64 px. Re-run after
changing a colour or the glyph geometry:

    python3 tools/make_icons.py            # write the .ico files
    python3 tools/make_icons.py --preview  # also drop 256 px PNGs next to them

Output: src/9Transcribe/Assets/{app,tray-idle,tray-rec,tray-busy,tray-off}.ico
"""

from __future__ import annotations

import math
import os
import struct
import sys
import zlib

APP_SIZES = (16, 20, 24, 32, 48, 64, 128, 256)
TRAY_SIZES = (16, 20, 24, 32, 48, 64)

OUT_DIR = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "src", "9Transcribe", "Assets",
)

# Each variant is a diagonal three-stop gradient (top-left → centre → bottom-right)
# plus the glyph colour. The idle look is the same vivid purple→pink→orange as the app
# icon; recording goes red, transcribing goes gold, and disabled goes grey, so the tray
# tells the state apart at a glance even at 16 px.
VARIANTS: dict[str, tuple[tuple[tuple[int, int, int], ...], tuple[int, int, int]]] = {
    "app":       (((0x7C, 0x3A, 0xED), (0xEC, 0x48, 0x99), (0xF9, 0x73, 0x16)), (0xFF, 0xFF, 0xFF)),
    "tray-idle": (((0x7C, 0x3A, 0xED), (0xEC, 0x48, 0x99), (0xF9, 0x73, 0x16)), (0xFF, 0xFF, 0xFF)),
    "tray-rec":  (((0xB9, 0x1C, 0x1C), (0xEF, 0x44, 0x44), (0xFB, 0x92, 0x3C)), (0xFF, 0xFF, 0xFF)),
    "tray-busy": (((0xB4, 0x53, 0x09), (0xF5, 0x9E, 0x0B), (0xFD, 0xE0, 0x47)), (0xFF, 0xFF, 0xFF)),
    "tray-off":  (((0x4B, 0x55, 0x63), (0x6B, 0x72, 0x80), (0x9C, 0xA3, 0xAF)), (0xE5, 0xE7, 0xEB)),
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


def sparkle(px: float, py: float, cx: float, cy: float, r: float) -> bool:
    """A four-point star: the shape every AI product uses to say 'assistant'."""
    x = abs(px - cx) / r
    y = abs(py - cy) / r
    if x > 1.0 or y > 1.0:
        return False
    # Astroid-style curve; the exponent below 1 pulls the sides in to make thin points.
    return (x ** 0.55) + (y ** 0.55) <= 1.0


def gradient(stops: tuple[tuple[int, int, int], ...], t: float) -> tuple[float, float, float]:
    """Three-stop linear gradient sampled at t in [0, 1]."""
    t = min(max(t, 0.0), 1.0)
    if t <= 0.5:
        a, b, u = stops[0], stops[1], t / 0.5
    else:
        a, b, u = stops[1], stops[2], (t - 0.5) / 0.5
    return tuple(a[i] + (b[i] - a[i]) * u for i in range(3))  # type: ignore[return-value]


def render(size: int, stops: tuple[tuple[int, int, int], ...],
           fg: tuple[int, int, int]) -> bytes:
    """Render one icon as BGRA bytes, top-down, size x size."""
    ss = 4 if size <= 64 else 2
    n = size * ss
    s = float(n)

    # The microphone sits a touch left and low so the sparkle in the top-right corner
    # balances it. At 16 and 20 px the sparkle would be mush, so it is left out and the
    # microphone returns to the centre.
    with_sparkle = size >= 24
    mic_cx = s * (0.46 if with_sparkle else 0.5)
    mic_dy = s * (0.03 if with_sparkle else 0.0)

    cap_cx, cap_cy = mic_cx, s * 0.335 + mic_dy
    cap_hw, cap_hh = s * 0.115, s * 0.205
    arc_cx, arc_cy = mic_cx, s * 0.395 + mic_dy
    arc_r, arc_t = s * 0.250, s * 0.058
    stem_top, stem_bot = s * 0.645 + mic_dy, s * 0.760 + mic_dy
    stem_hw = s * 0.030
    base_cy, base_hw, base_hh = s * 0.782 + mic_dy, s * 0.140, s * 0.030
    spark_cx, spark_cy, spark_r = s * 0.775, s * 0.235, s * 0.115
    bg_r = s * 0.235

    cover_bg = [0] * (size * size)
    cover_fg = [0] * (size * size)
    # Background colour accumulates per output pixel so the gradient is antialiased too.
    sum_r = [0.0] * (size * size)
    sum_g = [0.0] * (size * size)
    sum_b = [0.0] * (size * size)

    for y in range(n):
        py = y + 0.5
        oy = y // ss
        row = oy * size
        for x in range(n):
            px = x + 0.5
            if not rounded_rect(px, py, s * 0.5, s * 0.5, s * 0.5, s * 0.5, bg_r):
                continue
            idx = row + x // ss
            cover_bg[idx] += 1

            r, g, b = gradient(stops, (px + py) / (2.0 * s))
            # A soft sheen towards the top edge gives the tile some depth.
            sheen = 0.16 * (1.0 - py / s) ** 2
            r += (255.0 - r) * sheen
            g += (255.0 - g) * sheen
            b += (255.0 - b) * sheen
            sum_r[idx] += r
            sum_g[idx] += g
            sum_b[idx] += b

            inside_fg = rounded_rect(px, py, cap_cx, cap_cy, cap_hw, cap_hh, cap_hw)
            if not inside_fg and py >= arc_cy:
                d = math.hypot(px - arc_cx, py - arc_cy)
                inside_fg = abs(d - arc_r) <= arc_t * 0.5
            if not inside_fg and stem_top <= py <= stem_bot:
                inside_fg = abs(px - mic_cx) <= stem_hw
            if not inside_fg:
                inside_fg = rounded_rect(px, py, mic_cx, base_cy, base_hw, base_hh, base_hh)
            if not inside_fg and with_sparkle:
                inside_fg = sparkle(px, py, spark_cx, spark_cy, spark_r)
            if inside_fg:
                cover_fg[idx] += 1

    per_px = ss * ss
    out = bytearray()
    for i in range(size * size):
        if cover_bg[i] == 0:
            out += b"\x00\x00\x00\x00"
            continue
        a_bg = cover_bg[i] / per_px
        a_fg = cover_fg[i] / per_px
        bg_r_avg = sum_r[i] / cover_bg[i]
        bg_g_avg = sum_g[i] / cover_bg[i]
        bg_b_avg = sum_b[i] / cover_bg[i]
        r = fg[0] * a_fg + bg_r_avg * (1.0 - a_fg)
        g = fg[1] * a_fg + bg_g_avg * (1.0 - a_fg)
        b = fg[2] * a_fg + bg_b_avg * (1.0 - a_fg)
        out += bytes((
            int(min(b, 255.0) + 0.5),
            int(min(g, 255.0) + 0.5),
            int(min(r, 255.0) + 0.5),
            int(a_bg * 255 + 0.5),
        ))
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


def write_png(path: str, size: int, bgra_topdown: bytes) -> None:
    """Minimal RGBA PNG writer, only used for previews."""
    raw = bytearray()
    stride = size * 4
    for y in range(size):
        raw += b"\x00"  # filter: none
        row = bgra_topdown[y * stride:(y + 1) * stride]
        for x in range(size):
            b, g, r, a = row[x * 4:x * 4 + 4]
            raw += bytes((r, g, b, a))

    def chunk(kind: bytes, data: bytes) -> bytes:
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 9))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as fh:
        fh.write(png)


def main(argv: list[str]) -> None:
    preview = "--preview" in argv
    os.makedirs(OUT_DIR, exist_ok=True)
    for name, (stops, fg) in VARIANTS.items():
        sizes = APP_SIZES if name == "app" else TRAY_SIZES
        rendered = [(size, render(size, stops, fg)) for size in sizes]
        path = os.path.join(OUT_DIR, f"{name}.ico")
        write_ico(path, [(size, bmp_payload(size, pixels)) for size, pixels in rendered])
        print(f"wrote {path} ({os.path.getsize(path)} bytes)")
        if preview:
            size, pixels = rendered[-1]
            png_path = os.path.join(OUT_DIR, f"{name}-preview.png")
            write_png(png_path, size, pixels)
            print(f"wrote {png_path}")


if __name__ == "__main__":
    main(sys.argv[1:])
