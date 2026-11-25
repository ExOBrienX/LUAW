"""Imagen principal de LUAW para flightsim.to (1920x1080).

Uso: python -I tools/branding/flightsim-main-image.py <repo> <captura de la app> <salida.png>
"""
import sys
from PIL import Image, ImageDraw, ImageFilter, ImageFont

repo, app_shot, out_path = sys.argv[1], sys.argv[2], sys.argv[3]
W, H = 1920, 1080
navy = (10, 18, 30)
blue = (46, 150, 245)
light = (230, 237, 245)
muted = (150, 168, 190)
fonts = "C:/Windows/Fonts/"


def font(name, size):
    return ImageFont.truetype(fonts + name, size)


# Fondo: Newark de noche en el simulador, cubriendo todo el cuadro.
bg = Image.open(repo + "/docs/readme/sim/night-overview.webp").convert("RGB")
scale = W / bg.width
bg = bg.resize((W, round(bg.height * scale)), Image.LANCZOS)
top = 40
bg = bg.crop((0, top, W, top + H)).convert("RGBA")

# Degradado oscuro desde la izquierda (texto) y abajo (ventana de la app).
shade = Image.new("RGBA", (W, H))
px = shade.load()
for x in range(W):
    # Lleno hasta x=620 y se desvanece suave hasta x=1400 (sin corte visible).
    u = min(1.0, max(0.0, (x - 620) / 780))
    a_left = 0.95 * (1 - u * u * (3 - 2 * u))
    for y in range(H):
        a_bottom = 0.55 * max(0.0, (y - 700) / 380) ** 1.5
        a_top = 0.35 * max(0.0, (140 - y) / 140)
        a = max(a_left, a_bottom, a_top)
        px[x, y] = (*navy, int(255 * a))
canvas = Image.alpha_composite(bg, shade)

# Ventana de la app (radar y radio), con borde, sombra y barra de título.
app = Image.open(app_shot).convert("RGBA")
aw = 860
ah = round(app.height * aw / app.width)
app = app.resize((aw, ah), Image.LANCZOS)
bar = 34
win_w, win_h = aw, ah + bar
wx, wy = W - win_w - 70, H - win_h - 60
shadow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
ImageDraw.Draw(shadow).rounded_rectangle((wx - 6, wy + 10, wx + win_w + 6, wy + win_h + 22), 18, fill=(0, 0, 0, 200))
shadow = shadow.filter(ImageFilter.GaussianBlur(26))
canvas = Image.alpha_composite(canvas, shadow)
win = Image.new("RGBA", (win_w, win_h), (18, 28, 42, 255))
wd = ImageDraw.Draw(win)
# Barra de título al estilo Windows: nombre a la izquierda, controles a la derecha.
wd.text((16, bar // 2), "LUAW", font=font("segoeuib.ttf", 15), fill=muted, anchor="lm")
cx = win_w - 22
wd.line((cx - 6, 11, cx + 6, 23), fill=muted, width=2)
wd.line((cx - 6, 23, cx + 6, 11), fill=muted, width=2)
cx -= 46
wd.rectangle((cx - 6, 11, cx + 6, 23), outline=muted, width=2)
cx -= 46
wd.line((cx - 6, 17, cx + 6, 17), fill=muted, width=2)
win.paste(app, (0, bar))
mask = Image.new("L", (win_w, win_h), 0)
ImageDraw.Draw(mask).rounded_rectangle((0, 0, win_w - 1, win_h - 1), 14, fill=255)
canvas.paste(win, (wx, wy), mask)
ImageDraw.Draw(canvas).rounded_rectangle((wx, wy, wx + win_w - 1, wy + win_h - 1), 14, outline=(70, 110, 160, 255), width=2)

d = ImageDraw.Draw(canvas)

# Logo: la ilustración de la torre y el avión (sin el texto viejo), sobre un disco claro para que se lea en lo oscuro.
ill = Image.open(repo + "/tools/branding/illustration.webp").convert("RGBA")
ill = ill.crop((90, 170, ill.width - 90, int(ill.height * 0.68)))
lh = 200
ill = ill.resize((round(ill.width * lh / ill.height), lh), Image.LANCZOS)
lx, ly = 110, 86
glow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
ImageDraw.Draw(glow).ellipse((lx - 30, ly - 20, lx + ill.width + 30, ly + lh + 30), fill=(46, 150, 245, 70))
glow = glow.filter(ImageFilter.GaussianBlur(40))
canvas = Image.alpha_composite(canvas, glow)
canvas.alpha_composite(ill, (lx, ly))
d = ImageDraw.Draw(canvas)

# Nombre y lema.
name_f = font("segoeuiz.ttf", 190)
y = ly + lh + 6
d.text((lx - 6, y), "LUAW", font=name_f, fill=blue, stroke_width=4, stroke_fill=(6, 20, 40), anchor="lt")
nb = d.textbbox((lx - 6, y), "LUAW", font=name_f, anchor="lt")
y = nb[3] + 26
d.text((lx, y), "Line Up And Wait", font=font("segoeuisl.ttf", 34), fill=muted, anchor="lt")
y += 58
d.text((lx, y), "ATC and AI traffic for", font=font("segoeuib.ttf", 46), fill=light, anchor="lt")
y += 58
d.text((lx, y), "Microsoft Flight Simulator 2024", font=font("segoeuib.ttf", 46), fill=light, anchor="lt")
y += 96

# Funciones principales.
feat_f = font("segoeui.ttf", 31)
features = [
    "Full IFR ATC, gate to gate",
    "Voice, text or automatic copilot",
    "Real airline traffic with your FSLTL and AIG models",
    "Living airports: pushback, taxi, queues, holds",
]
for f in features:
    d.rounded_rectangle((lx, y + 12, lx + 14, y + 26), 4, fill=blue)
    d.text((lx + 32, y), f, font=feat_f, fill=light, anchor="lt")
    y += 52

# Sello inferior.
y += 26
tag_f = font("segoeuib.ttf", 24)
for i, (txt, fill, fg) in enumerate([("FREE", blue, (255, 255, 255)), ("OPEN SOURCE", None, light), ("BETA", None, (245, 165, 36))]):
    b = d.textbbox((0, 0), txt, font=tag_f)
    w = b[2] - b[0] + 40
    x = lx if i == 0 else x_next
    d.rounded_rectangle((x, y, x + w, y + 48), 24, fill=fill, outline=blue if fill is None else None, width=2)
    d.text((x + w / 2, y + 24), txt, font=tag_f, fill=fg, anchor="mm")
    x_next = x + w + 14

canvas.convert("RGB").save(out_path, quality=95)
print(out_path)
