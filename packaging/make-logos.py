#!/usr/bin/env python3
"""Generate the MSIX logo set.

The mark is a pressure wave down a duct: the one thing every part of this tool
is ultimately drawing. Generated rather than drawn so the set is reproducible
from source and so a new size never has to be traced by hand.

    python packaging/make-logos.py

Writes PNGs into packaging/assets/. Deterministic: the same script produces
byte-identical files, which keeps them out of every diff that does not intend
to change them.
"""

import math
import os
import struct
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HERE, 'assets')

# The app's own tokens: Brush.Canvas inverted for a tile, and Brush.Accent.
BACKGROUND = (0x10, 0x1B, 0x2A)
WAVE = (0x4D, 0x9B, 0xE8)
CREST = (0xF2, 0xF6, 0xFB)


def png(width, height, pixels):
    """RGBA rows to a PNG byte string."""
    raw = b''.join(b'\x00' + bytes(row) for row in pixels)

    def chunk(tag, data):
        body = tag + data
        return struct.pack('>I', len(data)) + body + struct.pack('>I', zlib.crc32(body))

    return (b'\x89PNG\r\n\x1a\n'
            + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(raw, 9))
            + chunk(b'IEND', b''))


def render(width, height):
    """A damped travelling wave across the tile, on the app's canvas colour.

    Inset on both sides so the wave BEGINS and ENDS inside the tile. Drawn edge
    to edge it was clipped at both ends, and the bright crest — the part that
    carries the idea of a pulse launching — was the half cut off, which read as
    a rendering accident rather than a mark.
    """
    rows = []
    margin = width * 0.14
    span = max(1.0, width - (2 * margin))
    amplitude = height * 0.20
    wavelength = span / 1.5
    thickness = max(1.4, height / 24.0)

    for y in range(height):
        row = bytearray()
        for x in range(width):
            # Outside the inset the wave has not started or has died away.
            u = (x + 0.5 - margin) / span
            if u < 0.0 or u > 1.0:
                row += bytes(BACKGROUND)
                row.append(255)
                continue

            # Decays left to right, the way a pulse does down a pipe, and
            # fades out at the very end rather than stopping mid-stroke.
            decay = math.exp(-2.0 * u)
            fade = min(1.0, (1.0 - u) * 6.0)
            centre = (height / 2.0) + (amplitude * decay
                                       * math.sin(2.0 * math.pi * (x + 0.5 - margin) / wavelength))
            distance = abs(y + 0.5 - centre)

            if distance <= thickness:
                # Antialias the edge over one pixel so small tiles stay legible.
                t = min(1.0, max(0.0, thickness - distance)) * fade
                colour = CREST if u < 0.16 else WAVE
                row += bytes(
                    int(BACKGROUND[i] + ((colour[i] - BACKGROUND[i]) * t)) for i in range(3))
                row.append(255)
            else:
                row += bytes(BACKGROUND)
                row.append(255)

        rows.append(row)

    return rows


SIZES = {
    'Square44x44Logo.png': (44, 44),
    'Square150x150Logo.png': (150, 150),
    'Square310x310Logo.png': (310, 310),
    'Wide310x150Logo.png': (310, 150),
    'StoreLogo.png': (50, 50),
    'SplashScreen.png': (620, 300),
}


def main():
    os.makedirs(ASSETS, exist_ok=True)
    for name, (w, h) in sorted(SIZES.items()):
        data = png(w, h, render(w, h))
        with open(os.path.join(ASSETS, name), 'wb') as f:
            f.write(data)
        print('%-24s %4d x %-4d %7d bytes' % (name, w, h, len(data)))


if __name__ == '__main__':
    main()
