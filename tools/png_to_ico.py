#!/usr/bin/env python3
"""
Convert a PNG into a Windows .ico for the overlay.

    python3 tools/png_to_ico.py logo.png src/AraOverlay/AraOverlay.ico

Writes the tray sizes as classic DIB frames, because System.Drawing.Icon reads
PNG-compressed frames unreliably, plus a 256px PNG frame for Explorer.
Stdlib only — nothing to install.
"""
import struct, sys, zlib


def read_png(path):
    """Returns (width, height, rows of RGBA tuples). 8-bit, non-interlaced."""
    data = open(path, "rb").read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise SystemExit(f"{path} is not a PNG.")

    idat, pos, palette, trns = b"", 8, None, None
    while pos < len(data):
        length, tag = struct.unpack(">I4s", data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + length]
        if tag == b"IHDR":
            w, h, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", body)
            if depth != 8:
                raise SystemExit(f"Need an 8-bit-per-channel PNG; this one is {depth}-bit.")
            if interlace:
                raise SystemExit("Interlaced PNGs aren't supported — re-save without interlacing.")
        elif tag == b"PLTE":
            palette = body
        elif tag == b"tRNS":
            trns = body
        elif tag == b"IDAT":
            idat += body
        elif tag == b"IEND":
            break
        pos += 12 + length

    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[color]
    raw = zlib.decompress(idat)
    stride = w * channels

    # Undo the per-scanline filters.
    out, prev = [], bytearray(stride)
    for y in range(h):
        start = y * (stride + 1)
        f, line = raw[start], bytearray(raw[start + 1:start + 1 + stride])
        for i in range(stride):
            a = line[i - channels] if i >= channels else 0
            b = prev[i]
            c = prev[i - channels] if i >= channels else 0
            if f == 1:   line[i] = (line[i] + a) & 0xFF
            elif f == 2: line[i] = (line[i] + b) & 0xFF
            elif f == 3: line[i] = (line[i] + (a + b) // 2) & 0xFF
            elif f == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 0xFF
        out.append(line)
        prev = line

    rows = []
    for line in out:
        row = []
        for x in range(w):
            p = line[x * channels:(x + 1) * channels]
            if color == 6:   row.append(tuple(p))
            elif color == 2: row.append((p[0], p[1], p[2], 255))
            elif color == 4: row.append((p[0], p[0], p[0], p[1]))
            elif color == 0: row.append((p[0], p[0], p[0], 255))
            else:
                i = p[0]
                alpha = trns[i] if trns and i < len(trns) else 255
                row.append((palette[i * 3], palette[i * 3 + 1], palette[i * 3 + 2], alpha))
        rows.append(row)
    return w, h, rows


def resize(rows, w, h, n):
    """Box-filter to n x n. Averaging keeps small sizes legible; nearest does not."""
    out = []
    for y in range(n):
        y0, y1 = y * h // n, max(y * h // n + 1, (y + 1) * h // n)
        row = []
        for x in range(n):
            x0, x1 = x * w // n, max(x * w // n + 1, (x + 1) * w // n)
            px = [rows[sy][sx] for sy in range(y0, y1) for sx in range(x0, x1)]
            # Weight colour by alpha so transparent edges don't drag in their RGB.
            a = sum(p[3] for p in px)
            if a == 0:
                row.append((0, 0, 0, 0))
            else:
                row.append(tuple(sum(p[i] * p[3] for p in px) // a for i in range(3)) + (a // len(px),))
        out.append(row)
    return out


def dib(px):
    n = len(px)
    head = struct.pack("<IiiHHIIiiII", 40, n, n * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    body = b"".join(b"".join(bytes((p[2], p[1], p[0], p[3])) for p in row) for row in reversed(px))
    return head + body + b"\x00" * (((n + 31) // 32) * 4 * n)


def png(px):
    n = len(px)
    raw = b"".join(b"\x00" + b"".join(bytes(p) for p in row) for row in px)
    def chunk(tag, body):
        c = tag + body
        return struct.pack(">I", len(body)) + c + struct.pack(">I", zlib.crc32(c))
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", n, n, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


def main(src, dst):
    w, h, rows = read_png(src)
    if w != h:
        print(f"note: {src} is {w}x{h}. Icons are square, so it will be squashed — "
              "crop it square first for a better result.")

    frames = [(s, dib(resize(rows, w, h, s))) for s in (16, 24, 32, 48, 64)]
    frames.append((256, png(resize(rows, w, h, 256))))

    out = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    for s, body in frames:
        out += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(body), offset)
        offset += len(body)
    out += b"".join(b for _, b in frames)

    open(dst, "wb").write(out)
    print(f"{dst}: {len(out)} bytes, frames {[s for s, _ in frames]}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    main(sys.argv[1], sys.argv[2])
