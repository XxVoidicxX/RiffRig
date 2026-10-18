"""Draws assets/RiffRig.ico: an orange guitar pick with a tuner needle."""
from pathlib import Path
from PIL import Image, ImageDraw

S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# Pick outline: rounded top, pointed bottom.
pts = []
import math
for i in range(181):
    a = math.radians(180 + i)                       # top arc
    pts.append((S / 2 + math.cos(a) * 400, 430 + math.sin(a) * 330))
pts += [(S / 2 + 300, 640), (S / 2 + 40, 950), (S / 2 - 40, 950), (S / 2 - 300, 640)]
d.polygon(pts, fill=(255, 90, 31, 255))
inner = [((x - S / 2) * 0.86 + S / 2, (y - 520) * 0.86 + 520) for x, y in pts]
d.polygon(inner, fill=(220, 60, 15, 255))

# Tuner scale and needle.
for c in range(-4, 5):
    x = S / 2 + c * 62
    h = 70 if c % 2 == 0 else 40
    d.rounded_rectangle([x - 9, 330, x + 9, 330 + h], 6, fill=(255, 235, 220, 255))
d.rounded_rectangle([S / 2 - 22, 300, S / 2 + 22, 700], 18, fill=(255, 255, 255, 255))

out = Path(__file__).resolve().parent.parent / "assets" / "RiffRig.ico"
img.save(out, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
img.resize((256, 256), Image.LANCZOS).save(out.with_suffix(".png"))
print("wrote", out)
