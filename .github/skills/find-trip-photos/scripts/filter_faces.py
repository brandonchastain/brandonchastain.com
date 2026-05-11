"""Remove gallery photos that contain any face that isn't the reference person.

Privacy-strict: a photo is kept only if (a) it has no detected faces, or (b)
every detected face matches the reference. Any non-matching face triggers
deletion of the photo and its photos.json entry.

Usage:
  python filter_faces.py --slug <slug> --reference <path/to/ref.jpg> [--threshold 0.65]

Operates on img/<slug>/*.jpg and img/<slug>/photos.json (canonical render
location), not the data folder.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

# Quiet TF logs
os.environ.setdefault("TF_CPP_MIN_LOG_LEVEL", "3")

from deepface import DeepFace
import numpy as np


REPO_ROOT = Path(__file__).resolve().parents[4]
MODEL = "Facenet512"  # 512-d embedding, robust
DETECTOR = "retinaface"


def encode(path: Path):
    """Return list of 512-d face embeddings detected in the image."""
    try:
        reps = DeepFace.represent(
            img_path=str(path),
            model_name=MODEL,
            detector_backend=DETECTOR,
            enforce_detection=False,
            align=True,
        )
        return [np.array(r["embedding"], dtype=np.float32) for r in reps if r.get("face_confidence", 1.0) > 0.5]
    except Exception as exc:
        msg = str(exc).encode("ascii", "ignore").decode()
        print(f"[warn] {path.name}: {msg[:200]}")
        return []


def cosine(a, b):
    return float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b) + 1e-9))


def filter_slug(slug, ref_emb, threshold, dry_run):
    img_dir = REPO_ROOT / "img" / slug
    if not img_dir.is_dir():
        print(f"[info] no img dir for {slug}")
        return
    meta_path = img_dir / "photos.json"
    if not meta_path.is_file():
        print(f"[info] no photos.json for {slug}")
        return
    meta = json.loads(meta_path.read_text(encoding="utf-8"))
    kept_meta = []
    removed = []
    for entry in meta:
        photo = img_dir / entry["file"]
        if not photo.is_file():
            continue
        embs = encode(photo)
        if not embs:
            kept_meta.append(entry)
            continue
        sims = [cosine(e, ref_emb) for e in embs]
        max_sim = max(sims)
        n_match = sum(s >= threshold for s in sims)
        if n_match == len(embs):
            kept_meta.append(entry)
        else:
            removed.append(photo)
    print(f"[{slug}] {len(kept_meta)} kept, {len(removed)} dropped")
    if dry_run:
        return
    for p in removed:
        p.unlink(missing_ok=True)
    meta_path.write_text(json.dumps(kept_meta, indent=2), encoding="utf-8")


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--slug", action="append", default=[], help="Repeatable; or --all-slugs")
    p.add_argument("--all-slugs", action="store_true",
                   help="Filter every img/<slug>/ that has a photos.json")
    p.add_argument("--reference", required=True)
    p.add_argument("--threshold", type=float, default=0.55,
                   help="Min cosine similarity to count as a match (higher = stricter)")
    p.add_argument("--dry-run", action="store_true")
    args = p.parse_args(argv)

    print(f"[info] encoding reference {args.reference}")
    ref_embs = encode(Path(args.reference))
    if not ref_embs:
        raise SystemExit("No face found in reference image")
    ref_emb = ref_embs[0]
    print(f"[info] reference encoded; using face 0 of {len(ref_embs)}")

    slugs = args.slug
    if args.all_slugs:
        img_root = REPO_ROOT / "img"
        slugs = [d.name for d in img_root.iterdir() if d.is_dir() and (d / "photos.json").is_file()]
        slugs.sort()
    if not slugs:
        raise SystemExit("No slugs to process; pass --slug X or --all-slugs")

    for slug in slugs:
        filter_slug(slug, ref_emb, args.threshold, args.dry_run)
    return 0


if __name__ == "__main__":
    sys.exit(main())
