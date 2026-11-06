"""montage.py OUT.png SHOTDIR PREFIX MS... : a row of the burst shots nearest the given ms, labeled"""
import re, sys
from pathlib import Path
from PIL import Image, ImageDraw

out, folder, prefix, *times = sys.argv[1:]
shots = {int(re.search(r"([-+]\d+)ms", p.name).group(1)): p for p in Path(folder).glob(prefix + "-*.png")}
tiles = []
for t in map(int, times):
    ms = min(shots, key=lambda k: abs(k - t))
    im = Image.open(shots[ms]).convert("RGB")
    w, h = im.size
    # The filaments' part of the frame, between the tool (below) and the slab
    im = im.crop((int(0.2 * w), int(0.15 * h), int(0.7 * w), int(0.95 * h))).resize((400, 384))
    d = ImageDraw.Draw(im)
    d.rectangle((0, 0, 92, 26), fill=(0, 0, 0))
    d.text((6, 6), f"{ms / 1000:+.1f} s", fill=(255, 255, 255))
    tiles.append(im)
cols = min(len(tiles), 5)
rows = (len(tiles) + cols - 1) // cols
sheet = Image.new("RGB", (cols * 404, rows * 388), (255, 255, 255))
for i, im in enumerate(tiles):
    sheet.paste(im, ((i % cols) * 404, (i // cols) * 388))
sheet.save(out)
