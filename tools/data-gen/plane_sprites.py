"""Recorta las siluetas de avión de los bocetos de Marco (ADR-173) y las guarda como máscaras blancas con transparencia.

El boceto trae seis filas (columna verde a la izquierda, azul a la derecha): avioneta, turbohélice, jet privado, fuselaje
estrecho, fuselaje ancho de 2 motores y de 4 motores. Se usa la columna verde: cada avión se recorta justo (punta de ala
a punta de ala, nariz a cola) y el color se pasa a transparencia: el fondo queda transparente y las líneas oscuras del
dibujo quedan semitransparentes (se ven como líneas). La app las pinta del color que toque (verde el piloto, azul los IA,
ámbar esperando).

Uso: python -I tools/data-gen/plane_sprites.py <boceto.jpg>
Escribe src/SimATC.App/Assets/Planes/{light,turboprop,bizjet,narrow,wide,quad}.png
"""
import os
import sys

import numpy as np
from PIL import Image

NAMES = ["light", "turboprop", "bizjet", "narrow", "wide", "quad"]


def groups_of(idx, gap):
    out, start = [], idx[0]
    for a, b in zip(idx, idx[1:]):
        if b - a > gap:
            out.append((start, a))
            start = b
    out.append((start, idx[-1]))
    return out


def near(mask, r):
    """Píxeles a r px o menos de la máscara (dilatación cuadrada)."""
    out = mask.copy()
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            out |= np.roll(np.roll(mask, dy, axis=0), dx, axis=1)
    return out


def main(src):
    img = np.asarray(Image.open(src).convert("RGB")).astype(np.float32)
    h, w, _ = img.shape
    left = img[:, : int(w * 0.45)]  # columna verde (los títulos son blancos: no cuentan)
    bg = np.median(left.reshape(-1, 3), axis=0)
    g = left[:, :, 1] - bg[1]
    green = (g > 40) & (left[:, :, 1] > left[:, :, 0] + 30) & (left[:, :, 1] > left[:, :, 2] + 20)
    rows = groups_of(np.where(green.sum(axis=1) > 2)[0], 15)
    rows = [r for r in rows if r[1] - r[0] > 40]
    assert len(rows) == len(NAMES), rows
    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "src", "SimATC.App", "Assets", "Planes")
    os.makedirs(out_dir, exist_ok=True)
    peak = np.percentile(g[green], 95)
    for name, (y0, y1) in zip(NAMES, rows):
        cols = np.where(green[y0 : y1 + 1].any(axis=0))[0]
        x0, x1 = cols[0], cols[-1]
        crop = g[y0 : y1 + 1, x0 : x1 + 1]
        alpha = np.clip(crop / peak, 0, 1)
        alpha[alpha < 0.08] = 0
        alpha[~near(green[y0 : y1 + 1, x0 : x1 + 1], 5)] = 0  # fuera del dibujo (los bordes del texto de los títulos)
        rgba = np.zeros(alpha.shape + (4,), dtype=np.uint8)
        rgba[:, :, :3] = 255
        rgba[:, :, 3] = (alpha * 255).astype(np.uint8)
        Image.fromarray(rgba, "RGBA").save(os.path.join(out_dir, name + ".png"), optimize=True)
        print(f"{name}: {x1 - x0 + 1} x {y1 - y0 + 1} px (ancho = envergadura, alto = largo)")


if __name__ == "__main__":
    main(sys.argv[1])
