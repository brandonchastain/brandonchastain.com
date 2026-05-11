#!/usr/bin/env python3
"""Resize all JPEG photos under img/ to max 1600px wide at JPEG q90, in place.

Preserves original filenames and extensions (some HTML references .JPEG uppercase).
Originals for trip-report photos are also preserved under tripreports/data/.
Run from repo root: python tools/resize-images.py
"""
import sys
from pathlib import Path
from PIL import Image, ImageOps

MAX_WIDTH = 1600
QUALITY = 90
ROOT = Path(__file__).resolve().parent.parent / "img"
EXTS = {".jpg", ".jpeg"}

total_before = 0
total_after = 0
processed = 0
skipped = 0

for path in ROOT.rglob("*"):
    if not path.is_file() or path.suffix.lower() not in EXTS:
        continue
    size_before = path.stat().st_size
    total_before += size_before
    try:
        with Image.open(path) as im:
            im = ImageOps.exif_transpose(im)
            w, h = im.size
            if w <= MAX_WIDTH and size_before < 500_000:
                total_after += size_before
                skipped += 1
                continue
            if w > MAX_WIDTH:
                new_h = int(h * MAX_WIDTH / w)
                im = im.resize((MAX_WIDTH, new_h), Image.LANCZOS)
            if im.mode != "RGB":
                im = im.convert("RGB")
            im.save(path, "JPEG", quality=QUALITY, optimize=True, progressive=True)
        new_size = path.stat().st_size
        total_after += new_size
        processed += 1
        print(f"  {path.relative_to(ROOT.parent)}: {size_before//1024}KB -> {new_size//1024}KB")
    except Exception as e:
        print(f"  SKIP {path}: {e}", file=sys.stderr)
        total_after += size_before
        skipped += 1

print(f"\nProcessed: {processed}, skipped: {skipped}")
print(f"Total: {total_before/1024/1024:.1f} MB -> {total_after/1024/1024:.1f} MB")
