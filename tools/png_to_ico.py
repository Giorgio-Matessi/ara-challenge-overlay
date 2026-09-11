#!/usr/bin/env python3
"""
Convert a PNG into a Windows .ico for the overlay.

    python3 tools/png_to_ico.py logo.png src/AraOverlay/AraOverlay.ico
    python3 tools/png_to_ico.py logo.png out.ico --bg 10181C   # on a rounded tile

Non-square art is trimmed to its visible bounds and centred on a square canvas,
never squashed.

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


def square(rows, w, h, margin=0.06):
    """Trim to the visible bounds, then centre on a square canvas with a little air."""
    xs = [x for y in range(h) for x in range(w) if rows[y][x][3] > 8]
    ys = [y for y in range(h) for x in range(w) if rows[y][x][3] > 8]
    if not xs:
        return rows, w, h
    x0, x1, y0, y1 = min(xs), max(xs) + 1, min(ys), max(ys) + 1

    side = int(max(x1 - x0, y1 - y0) * (1 + margin * 2))
    ox, oy = (side - (x1 - x0)) // 2, (side - (y1 - y0)) // 2

    out = [[(0, 0, 0, 0)] * side for _ in range(side)]
    for y in range(y0, y1):
        for x in range(x0, x1):
            out[oy + y - y0][ox + x - x0] = rows[y][x]
    return out, side, side


def tile(rows, n, rgb):
    """Composite onto an opaque rounded square, so the icon reads on a light taskbar too."""
    r = n * 0.22
    for y in range(n):
        for x in range(n):
            cx, cy = min(x + 0.5, n - x - 0.5), min(y + 0.5, n - y - 0.5)
            if cx < r and cy < r and (r - cx) ** 2 + (r - cy) ** 2 > r * r:
                rows[y][x] = (0, 0, 0, 0)
                continue
            fg = rows[y][x]
            a = fg[3] / 255
            rows[y][x] = tuple(int(fg[i] * a + rgb[i] * (1 - a)) for i in range(3)) + (255,)
    return rows


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


def main(src, dst, bg=None):
    w, h, rows = read_png(src)
    rows, w, h = square(rows, w, h)

    def frame(n):
        px = resize(rows, w, h, n)
        return tile(px, n, bg) if bg else px

    frames = [(s, dib(frame(s))) for s in (16, 24, 32, 48, 64)]
    frames.append((256, png(frame(256))))

    out = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    for s, body in frames:
        out += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(body), offset)
        offset += len(body)
    out += b"".join(b for _, b in frames)

    open(dst, "wb").write(out)
    print(f"{dst}: {len(out)} bytes, frames {[s for s, _ in frames]}")


if __name__ == "__main__":
    args = sys.argv[1:]
    bg = None
    if "--bg" in args:
        i = args.index("--bg")
        hexed = args[i + 1].lstrip("#")
        bg = tuple(int(hexed[j:j + 2], 16) for j in (0, 2, 4))
        del args[i:i + 2]
    if len(args) != 2:
        raise SystemExit(__doc__)
    main(args[0], args[1], bg)
