---
name: find-trip-photos
description: |
  Scan a photo library for images taken during a trip's date and GPS bounding box
  (derived from the trip's GPX track), screen for notable shots, and copy them
  into the trip's `tripreports/data/<slug>/` folder. Output feeds the existing
  tripreport skill so re-running the generator picks the photos up automatically.

  Use this when the user asks to:
    - "find photos from <trip>"
    - "pull photos for the <trip> trip report from cold storage"
    - "add photos to <slug>"

  Python is LOCAL-ONLY. The skill assumes the photo library is mounted at the
  path provided via --photos-root (default `\\cold-storage\Public\Photos_Videos`).
location: custom
---

# find-trip-photos skill

## Inputs
- `--data` (required): the trip data folder containing the `.gpx`, e.g.
  `tripreports/data/snoqualmie_mtn`. The trip date and GPS bbox are read from
  this GPX.
- `--photos-root` (optional): root of the photo library. Default:
  `\\cold-storage\Public\Photos_Videos`.
- `--buffer-km` (optional, default 3): bbox padding in km for the GPS filter —
  picks up shots at the trailhead, viewpoints just off-track, etc.
- `--min-sharpness` (optional, default 60): Laplacian-variance threshold;
  photos below this are dropped as blurry. Set to `0` to disable.
- `--max-photos` (optional, default 30): cap on how many candidates to copy
  even after screening.
- `--include-videos` (optional flag): also copy `.mp4` / `.mov` matches.
- `--dry-run` (optional flag): print the candidate list without copying.

## Setup (first run only)
```powershell
python -m venv .github\skills\find-trip-photos\.venv
.github\skills\find-trip-photos\.venv\Scripts\Activate.ps1
pip install -r .github\skills\find-trip-photos\requirements.txt
```

`pillow-heif` is included so iPhone `.HEIC` files are readable.

## Run
```powershell
python .github\skills\find-trip-photos\scripts\find_photos.py `
  --data tripreports\data\<trip>
```

Then re-run the tripreport skill for that trip — the copied photos land in the
data folder and get picked up automatically by `generate.py`'s photo loop.

## What it does
1. Parses `<data>/*.gpx` and extracts:
   - `trip_date` (UTC date from the first timestamp; also checks ±1 day to
     handle late-night photos and timezone slop)
   - bbox (min/max lat & lon) padded by `--buffer-km`
2. Walks `--photos-root` recursively for `.jpg / .jpeg / .png / .heic` (and
   videos if `--include-videos`). To stay fast on large libraries, the scan
   first probes `<root>/<YYYY>/` if it exists (common date-based layout).
3. For each candidate, reads EXIF:
   - `DateTimeOriginal` → must fall within `trip_date ± 1 day` (local time).
   - `GPSInfo` → must fall inside the padded bbox.
4. Screens the survivors:
   - Drops images with Laplacian variance below `--min-sharpness` (blurry).
   - Perceptual-hash dedupes near-duplicates (keeps the sharpest of each group).
5. Caps at `--max-photos` (kept in EXIF timestamp order so the photo
   sequence on the rendered page roughly matches the hike timeline).
6. Copies the survivors into `<data>/` with their original filenames. The
   tripreport generator copies them onward to `img/<slug>/` next time it runs.

## Caveats
- Photos with no GPS EXIF tag are skipped — there's no way to know they belong
  to the trip. iPhones write GPS for Camera-app photos by default; screenshots
  and forwarded images won't have it.
- The Gaia screenshot (`PNG image.png`) is intentionally not picked up by this
  skill because it's not in `--photos-root`; if a user drops one in by hand,
  the tripreport template already excludes it.
- Network share access (`\\cold-storage\...`) requires the share to be mounted
  and authenticated in the current Windows session.
