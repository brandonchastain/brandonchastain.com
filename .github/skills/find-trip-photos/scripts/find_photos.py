"""Find photos taken during a trip's date+bbox and copy them into the data folder.

See ../SKILL.md for usage. Reads EXIF date + GPS, screens for sharpness, dedupes
near-duplicates by perceptual hash, and writes survivors into the data folder
where the tripreport generator's photo loop will pick them up.
"""

from __future__ import annotations

import argparse
import shutil
import sys
from dataclasses import dataclass
from datetime import date, timedelta
from pathlib import Path

import gpxpy
import numpy as np
from PIL import Image, ImageFilter

try:
    import pillow_heif
    pillow_heif.register_heif_opener()
except ImportError:
    pass  # HEIC support is optional

IMG_EXTS = {".jpg", ".jpeg", ".png", ".heic"}
VIDEO_EXTS = {".mp4", ".mov", ".m4v"}


# ---------- GPX parsing ----------

@dataclass
class TripWindow:
    trip_date: date | None  # local date of trip start; None for waypoint-only inference
    lat_min: float
    lat_max: float
    lon_min: float
    lon_max: float


def parse_trip_window(gpx_path: Path, buffer_km: float):
    """Return (TripWindow, is_waypoint_only).

    For waypoint-only GPX files (a single <wpt> with no track), the TripWindow's
    trip_date will be None and the bbox is centered on the waypoint with a wider
    default buffer so the photo scan can pick up the actual hike location.
    """
    with gpx_path.open("r", encoding="utf-8") as f:
        gpx = gpxpy.parse(f)
    pts = [p for trk in gpx.tracks for seg in trk.segments for p in seg.points]
    if pts:
        lats = [p.latitude for p in pts]
        lons = [p.longitude for p in pts]
        times = [p.time for p in pts if p.time is not None]
        if not times:
            raise SystemExit(f"No timestamps in {gpx_path}; can't determine trip date")
        trip_date = times[0].date()
        waypoint_only = False
    elif gpx.waypoints:
        wp = gpx.waypoints[0]
        lats = [wp.latitude]
        lons = [wp.longitude]
        trip_date = None
        waypoint_only = True
        # Pin timestamp is when user dropped the waypoint, not when they hiked.
        # Trip date will be inferred from the photo cluster.
    else:
        raise SystemExit(f"No track points or waypoints in {gpx_path}")
    # Convert km buffer to lat/lon degrees (rough; lat ~111km/deg, lon scales by cos(lat))
    mid_lat = (min(lats) + max(lats)) / 2
    deg_per_km_lat = 1 / 111.0
    deg_per_km_lon = 1 / (111.0 * max(0.1, np.cos(np.radians(mid_lat))))
    return TripWindow(
        trip_date=trip_date,
        lat_min=min(lats) - buffer_km * deg_per_km_lat,
        lat_max=max(lats) + buffer_km * deg_per_km_lat,
        lon_min=min(lons) - buffer_km * deg_per_km_lon,
        lon_max=max(lons) + buffer_km * deg_per_km_lon,
    ), waypoint_only


# ---------- EXIF ----------

def _rational_to_float(r):
    try:
        return float(r[0]) / float(r[1])
    except Exception:
        return float(r)


def _gps_to_decimal(ref, dms):
    deg = _rational_to_float(dms[0])
    minutes = _rational_to_float(dms[1])
    seconds = _rational_to_float(dms[2])
    val = deg + minutes / 60 + seconds / 3600
    if str(ref).upper() in ("S", "W"):
        val = -val
    return val


def read_exif(path: Path):
    """Return (datetime|None, (lat,lon)|None) or (None, None) on failure."""
    try:
        img = Image.open(path)
        exif = img.getexif()
        if not exif:
            return None, None
        # DateTimeOriginal lives in EXIF IFD
        ifd = exif.get_ifd(0x8769)  # ExifIFDPointer
        dt_raw = ifd.get(0x9003) or exif.get(0x0132)  # DateTimeOriginal | DateTime
        from datetime import datetime
        dt = None
        if dt_raw:
            try:
                dt = datetime.strptime(str(dt_raw).strip(), "%Y:%m:%d %H:%M:%S")
            except ValueError:
                dt = None
        gps_ifd = exif.get_ifd(0x8825)  # GPSInfo
        coords = None
        if gps_ifd and 1 in gps_ifd and 2 in gps_ifd and 3 in gps_ifd and 4 in gps_ifd:
            lat = _gps_to_decimal(gps_ifd[1], gps_ifd[2])
            lon = _gps_to_decimal(gps_ifd[3], gps_ifd[4])
            coords = (lat, lon)
        return dt, coords
    except Exception:
        return None, None


# ---------- Screening ----------

def laplacian_variance(path: Path) -> float:
    """Sharpness proxy: variance of Laplacian on a small grayscale thumbnail."""
    try:
        img = Image.open(path).convert("L")
        img.thumbnail((512, 512))
        lap = img.filter(ImageFilter.FIND_EDGES)
        return float(np.array(lap).var())
    except Exception:
        return 0.0


def dhash(path: Path, size: int = 8) -> int:
    """Difference-hash for near-duplicate detection."""
    try:
        img = Image.open(path).convert("L").resize((size + 1, size), Image.LANCZOS)
        arr = np.array(img)
        diff = arr[:, 1:] > arr[:, :-1]
        bits = 0
        for b in diff.flatten():
            bits = (bits << 1) | int(b)
        return bits
    except Exception:
        return 0


def hamming(a: int, b: int) -> int:
    return bin(a ^ b).count("1")


# ---------- Walker ----------

def iter_candidates(root: Path, year: int, include_videos: bool):
    """Yield image paths under root, biasing toward year folder if present."""
    exts = IMG_EXTS | (VIDEO_EXTS if include_videos else set())
    year_dir = root / str(year)
    bases = [year_dir] if year_dir.is_dir() else [root]
    for base in bases:
        for p in base.rglob("*"):
            if p.is_file() and p.suffix.lower() in exts:
                yield p


# ---------- Orchestrator ----------

def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--data", required=True, help="tripreports/data/<trip> folder")
    p.add_argument("--photos-root", default=r"\\cold-storage\Public\Photos_Videos",
                   help="Root of the photo library")
    p.add_argument("--buffer-km", type=float, default=3.0,
                   help="GPS bbox padding in kilometers")
    p.add_argument("--min-sharpness", type=float, default=60.0,
                   help="Laplacian-variance threshold; 0 disables")
    p.add_argument("--max-photos", type=int, default=30,
                   help="Cap on copied photos")
    p.add_argument("--include-videos", action="store_true")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--waypoint-buffer-km", type=float, default=8.0,
                   help="Wider GPS bbox padding (km) used when the GPX has only a waypoint")
    p.add_argument("--auto-pick", action="store_true",
                   help="When inferring from a waypoint, auto-pick the largest date cluster")
    p.add_argument("--pick-date",
                   help="When inferring from a waypoint, force-use this YYYY-MM-DD cluster")
    args = p.parse_args(argv)

    data_dir = Path(args.data).resolve()
    if not data_dir.is_dir():
        raise SystemExit(f"Data folder not found: {data_dir}")
    gpx_files = sorted(data_dir.glob("*.gpx"))
    if not gpx_files:
        raise SystemExit(f"No .gpx in {data_dir}")
    win, waypoint_only = parse_trip_window(
        gpx_files[0], args.waypoint_buffer_km if not gpx_files[0].read_text().count("<trkpt") else args.buffer_km
    )
    if waypoint_only:
        print(f"[info] waypoint-only GPX; will infer trip date from photo clusters")
    print(f"[info] trip date {win.trip_date or 'inferred from photos'}, bbox "
          f"({win.lat_min:.4f},{win.lon_min:.4f}) -> ({win.lat_max:.4f},{win.lon_max:.4f})")

    photos_root = Path(args.photos_root)
    if not photos_root.exists():
        raise SystemExit(f"Photos root not accessible: {photos_root}")

    if waypoint_only:
        # Scan ALL year folders (no date filter), then cluster results by date.
        if args.pick_date:
            from datetime import datetime as _dt
            target_date = _dt.strptime(args.pick_date, "%Y-%m-%d").date()
            date_lo = target_date - timedelta(days=1)
            date_hi = target_date + timedelta(days=1)
        else:
            date_lo = date_hi = None  # no filter; collect all, cluster afterward
        year_dirs = [d for d in photos_root.iterdir() if d.is_dir() and d.name.isdigit()]
        candidate_iter = (p for yd in year_dirs for p in yd.rglob("*")
                          if p.is_file() and p.suffix.lower() in (IMG_EXTS | (VIDEO_EXTS if args.include_videos else set())))
    else:
        date_lo = win.trip_date - timedelta(days=1)
        date_hi = win.trip_date + timedelta(days=1)
        candidate_iter = iter_candidates(photos_root, win.trip_date.year, args.include_videos)

    # Phase 1: date+GPS filter (date filter optional in waypoint inference)
    matches = []
    scanned = 0
    for path in candidate_iter:
        scanned += 1
        if scanned % 1000 == 0:
            print(f"[info] scanned {scanned}, kept {len(matches)}...")
        dt, gps = read_exif(path)
        if dt is None or gps is None:
            continue
        if date_lo and not (date_lo <= dt.date() <= date_hi):
            continue
        lat, lon = gps
        if not (win.lat_min <= lat <= win.lat_max and win.lon_min <= lon <= win.lon_max):
            continue
        matches.append((path, dt))
    print(f"[info] scanned {scanned} files; {len(matches)} match bbox{'+date' if date_lo else ''}")

    # Waypoint inference: cluster by date, report, then pick one
    if waypoint_only and not date_lo:
        if not matches:
            print("[info] no photos found near waypoint; nothing to do.")
            return 0
        from collections import Counter
        clusters = Counter(dt.date() for _, dt in matches)
        print("\n[clusters] photos near waypoint, grouped by date:")
        for d, n in sorted(clusters.items(), reverse=True):
            day_pts = [(p, t) for p, t in matches if t.date() == d]
            first = min(t for _, t in day_pts); last = max(t for _, t in day_pts)
            print(f"  {d}  count={n:>3}  window={first:%H:%M}-{last:%H:%M}")
        if args.auto_pick:
            pick = max(clusters.items(), key=lambda kv: kv[1])[0]
            print(f"[info] --auto-pick selected {pick} ({clusters[pick]} photos)")
        else:
            print("\n[info] re-run with --pick-date YYYY-MM-DD (or --auto-pick) to copy a cluster.")
            return 0
        matches = [(p, t) for p, t in matches if t.date() == pick]
        win.trip_date = pick

    if not matches:
        print("[info] nothing to copy.")
        return 0

    # Phase 2: sharpness screen
    scored = []
    for path, dt in matches:
        if path.suffix.lower() in VIDEO_EXTS:
            scored.append((path, dt, float("inf"), 0))
            continue
        sharp = laplacian_variance(path) if args.min_sharpness > 0 else float("inf")
        if sharp < args.min_sharpness:
            continue
        scored.append((path, dt, sharp, dhash(path)))
    print(f"[info] {len(scored)} survive sharpness >= {args.min_sharpness}")

    # Phase 3: dHash dedupe (keep sharpest per cluster, threshold 6 bits)
    scored.sort(key=lambda r: r[2], reverse=True)  # sharpest first
    kept = []
    for row in scored:
        path, dt, sharp, h = row
        dup = False
        for kpath, kdt, ksharp, kh in kept:
            if h and kh and hamming(h, kh) <= 6:
                dup = True
                break
        if not dup:
            kept.append(row)
    print(f"[info] {len(kept)} survive dedup")

    # Phase 4: cap and reorder chronologically
    kept.sort(key=lambda r: r[1])
    if len(kept) > args.max_photos:
        # Evenly sample so we keep the trip's whole arc, not just the start
        step = len(kept) / args.max_photos
        kept = [kept[int(i * step)] for i in range(args.max_photos)]

    print(f"[info] copying {len(kept)} photo(s) -> {data_dir}")
    photos_meta = []
    for path, dt, sharp, _ in kept:
        # Convert HEIC to JPG since browsers can't render HEIC natively.
        if path.suffix.lower() == ".heic":
            target = data_dir / (path.stem + ".jpg")
            if target.exists():
                target = data_dir / f"{path.stem}__{dt:%H%M%S}.jpg"
        else:
            target = data_dir / path.name
            if target.exists():
                target = data_dir / f"{path.stem}__{dt:%H%M%S}{path.suffix}"
        print(f"  {dt:%Y-%m-%d %H:%M:%S}  sharp={sharp:.0f}  {path.name}")
        if not args.dry_run:
            if path.suffix.lower() == ".heic":
                # Re-encode as JPG; preserve EXIF where Pillow can.
                img = Image.open(path)
                exif_bytes = img.info.get("exif", b"")
                img.convert("RGB").save(target, "JPEG", quality=95, subsampling=0, exif=exif_bytes)
            else:
                shutil.copy2(path, target)
            _, gps = read_exif(target)
            photos_meta.append({
                "file": target.name,
                "time": dt.isoformat(),
                "lat": gps[0] if gps else None,
                "lon": gps[1] if gps else None,
            })

    if not args.dry_run and photos_meta:
        import json
        (data_dir / "photos.json").write_text(json.dumps(photos_meta, indent=2), encoding="utf-8")
        print(f"[info] wrote {data_dir / 'photos.json'} ({len(photos_meta)} entries)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
