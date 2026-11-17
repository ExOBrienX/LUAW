"""Logo de la pantalla de carga de LUAW (ADR-154): la ilustración de la torre y el avión con el nombre debajo.

Uso: python -I tools/branding/splash-logo.py tools/branding/illustration.webp src/SimATC.App/Assets/luaw-logo.webp
La ilustración de entrada es el logo anterior (su texto se tapa): de ella se toma solo la parte de arriba.
"""
import sys
from PIL import Image, ImageDraw, ImageFont

src, dst = sys.argv[1], sys.argv[2]
img = Image.open(src).convert("RGBA")
w, h = img.size
cut = int(h * 0.68)  # el texto anterior empieza bajo el suelo azul (~0,69 de la altura)

out = Image.new("RGBA", (w, h), (255, 255, 255, 255))
out.alpha_composite(img.crop((0, 0, w, cut)), (0, 0))  # la ilustración tiene fondo transparente
d = ImageDraw.Draw(out)

fonts = "C:/Windows/Fonts/"
name_font = ImageFont.truetype(fonts + "segoeuiz.ttf", int(h * 0.19))   # Segoe UI Bold Italic
sub_font = ImageFont.truetype(fonts + "segoeuib.ttf", int(h * 0.040))  # Segoe UI Bold

blue, navy, gray = (46, 150, 245, 255), (10, 27, 46, 255), (70, 86, 104, 255)

# Nombre: azul con borde azul noche, centrado.
name = "LUAW"
box = d.textbbox((0, 0), name, font=name_font, stroke_width=8)
tw = box[2] - box[0]
y = int(h * 0.70)
d.text(((w - tw) / 2 - box[0], y - box[1]), name, font=name_font, fill=blue, stroke_width=8, stroke_fill=navy)
name_bottom = y + (box[3] - box[1])

# Subtítulo: "LINE UP AND WAIT" espaciado, en gris.
sub = "LINE UP AND WAIT"
spacing = int(h * 0.012)
widths = [d.textlength(c, font=sub_font) for c in sub]
total = sum(widths) + spacing * (len(sub) - 1)
x = (w - total) / 2
sy = name_bottom + int(h * 0.035)
for c, cw in zip(sub, widths):
    d.text((x, sy), c, font=sub_font, fill=gray)
    x += cw + spacing

out.convert("RGB").save(dst, "WEBP", quality=92)
print("listo", dst, out.size)
