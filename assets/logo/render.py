#!/usr/bin/env python3
"""Render the Pickle logo SVGs to pickle.ico (16–256 px), pickle-256.png and pickle-terminal.png with headless Chromium.

Usage: assets/logo/render.py   (set CHROMIUM to a chrome/headless_shell binary if it isn't found automatically)
"""
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SMALL_MAX = 32  # sizes up to this use pickle-small.svg


def find_chromium():
    candidates = [os.environ.get("CHROMIUM"), "/opt/pw-browsers/chromium_headless_shell-1194/chrome-linux/headless_shell",
                  shutil.which("chromium"), shutil.which("chromium-browser"), shutil.which("google-chrome"), "/opt/pw-browsers/chromium"]
    for c in candidates:
        if c and os.path.exists(c):
            return c
    sys.exit("render.py: no Chromium found; set CHROMIUM=/path/to/chrome")


def render(chromium, svg_text, size, out, work):
    src = os.path.join(work, f"{size}.svg")
    with open(src, "w", encoding="utf-8") as f:
        f.write(svg_text.replace('width="256" height="256"', f'width="{size}" height="{size}"', 1))
    subprocess.run([chromium, "--headless", "--no-sandbox", "--disable-gpu", "--hide-scrollbars",
                    "--default-background-color=00000000", f"--window-size={size},{size}",
                    f"--screenshot={out}", "file://" + src], check=True, capture_output=True)


def decode_png(data):
    """RGBA rows of an 8-bit RGBA, non-interlaced PNG (what Chromium writes)."""
    assert data[:8] == b"\x89PNG\r\n\x1a\n"
    pos, idat, width, height = 8, b"", 0, 0
    while pos < len(data):
        length, kind = struct.unpack(">I4s", data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + length]
        if kind == b"IHDR":
            width, height, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", body)
            assert depth == 8 and color == 6 and interlace == 0, "expected 8-bit RGBA"
        elif kind == b"IDAT":
            idat += body
        pos += 12 + length
    raw, stride, rows, prev = zlib.decompress(idat), width * 4, [], bytearray(width * 4)
    for y in range(height):
        kind, line = raw[y * (stride + 1)], bytearray(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
        for i in range(stride):
            a = line[i - 4] if i >= 4 else 0
            b = prev[i]
            c = prev[i - 4] if i >= 4 else 0
            if kind == 1:
                line[i] = (line[i] + a) & 255
            elif kind == 2:
                line[i] = (line[i] + b) & 255
            elif kind == 3:
                line[i] = (line[i] + (a + b) // 2) & 255
            elif kind == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(bytes(line))
        prev = line
    return width, height, rows


def dib(width, height, rows):
    """32-bit BGRA DIB (bottom-up) plus the 1-bit AND mask, as stored in .ico files."""
    header = struct.pack("<IiiHHIIiiII", 40, width, height * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    pixels = b"".join(bytes(b for px in range(width) for b in (r[px * 4 + 2], r[px * 4 + 1], r[px * 4], r[px * 4 + 3])) for r in reversed(rows))
    mask_stride = ((width + 31) // 32) * 4
    mask = b""
    for r in reversed(rows):
        bits = bytearray(mask_stride)
        for px in range(width):
            if r[px * 4 + 3] == 0:
                bits[px // 8] |= 0x80 >> (px % 8)
        mask += bytes(bits)
    return header + pixels + mask


def write_ico(path, images):
    """images: [(size, png_bytes)]; 256 px stays PNG-compressed, smaller sizes are DIBs for older loaders."""
    entries, blobs = [], []
    offset = 6 + 16 * len(images)
    for size, png in images:
        blob = png if size >= 256 else dib(*decode_png(png))
        entries.append(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(blob), offset))
        blobs.append(blob)
        offset += len(blob)
    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(images)) + b"".join(entries) + b"".join(blobs))


def main():
    chromium = find_chromium()
    full = open(os.path.join(HERE, "pickle.svg"), encoding="utf-8").read()
    small = open(os.path.join(HERE, "pickle-small.svg"), encoding="utf-8").read()
    with tempfile.TemporaryDirectory() as work:
        images = []
        for size in ICO_SIZES:
            out = os.path.join(work, f"{size}.png")
            render(chromium, small if size <= SMALL_MAX else full, size, out, work)
            images.append((size, open(out, "rb").read()))
        write_ico(os.path.join(HERE, "pickle.ico"), images)
        shutil.copy(os.path.join(work, "256.png"), os.path.join(HERE, "pickle-256.png"))
        # Windows Terminal draws profile icons at 16–32 px; the small variant stays crisp there.
        render(chromium, small, 48, os.path.join(HERE, "pickle-terminal.png"), work)
    print("wrote pickle.ico, pickle-256.png, pickle-terminal.png")


if __name__ == "__main__":
    main()
