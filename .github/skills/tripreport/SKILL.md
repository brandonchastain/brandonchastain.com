---
name: tripreport
description: |
  Generate a new trip report HTML page (under tripreports/<year>/) from a Gaia GPS export
  that lives under tripreports/data/<trip>/. Produces auto-computed stats (distance, gain,
  total/moving/stopped duration, avg speed, date+time window), a fetched historical
  weather summary for the trip window, an elevation profile PNG, and an interactive
  satellite+topo blended Leaflet map embedded in the page. Photos found in the data folder
  are copied to img/<trip>/ and scaffolded as placeholders for manual ordering.

  Use this when the user asks to:
    - "create a trip report" / "make a new trip report"
    - "generate a trip report from GPX"
    - reference a folder under tripreports/data/

  Python is LOCAL-ONLY (only runs on the author's machine before deploy). The generated
  pages are plain HTML/CSS/JS and rely only on the existing site stylesheet plus Leaflet
  CDN tags — there is no build step or runtime dependency on Python.
location: custom
---

# tripreport skill

## Inputs to gather from the user
- `--data` path: the folder under `tripreports/data/<trip>/` containing the `.gpx` export.
- `--name`: human-readable trip name (e.g. `"Snoqualmie Mountain"`). Filename will be `<Name_With_Underscores>.html`.
- `--region`: region label. Prefer the specific protected area containing the route — wilderness area, national park, or national forest — e.g. `"Alpine Lakes Wilderness, Mt. Baker–Snoqualmie National Forest, WA"` or `"North Cascades National Park, WA"`. Not in GPX; ask the user if unsure.
- `--tz` (optional): IANA timezone for local clock + weather, default `America/Los_Angeles`.
- `--no-weather` (optional): skip the Open-Meteo historical weather lookup.

## Setup (first run only)
```powershell
python -m venv .github\skills\tripreport\.venv
.github\skills\tripreport\.venv\Scripts\Activate.ps1
pip install -r .github\skills\tripreport\requirements.txt
```

## Workflow (Claude as orchestrator)

1. **Read the Gaia screenshot** in `tripreports/data/<trip>/` (typically named `PNG image.png`) using the `Read` tool. Extract the **distance (mi)**, **total ascent / elevation gain (ft)**, and **moving time** (Gaia displays these on its trip summary). These are the canonical values — GPX-derived numbers will not match.
2. **Run the script** with those values passed via `--gaia-*` flags. The script estimates from raw GPX, then overrides + calibrates the per-point series to the Gaia totals:

```powershell
python .github\skills\tripreport\scripts\generate.py `
  --data tripreports\data\<trip> `
  --name "<Trip Name>" `
  --region "<Region>" `
  --gaia-distance 3.09 `
  --gaia-gain 2984 `
  --gaia-moving "4h 16m"
```

If a Gaia screenshot is missing, skip the `--gaia-*` flags and the script falls back to raw GPX estimates (which will be inflated — see "Stat caveats" below).

## What it does
1. Parses `<data>/*.gpx` with `gpxpy`.
2. Computes stats:
   - distance (haversine sum)
   - elevation gain (15-point moving avg + positive-delta sum to suppress GPS noise)
   - total duration (last − first timestamp)
   - moving time (segments with instantaneous speed > 0.3 m/s)
   - stopped time (total − moving)
   - avg moving and avg overall mph
   - date and local time window (HH:MM–HH:MM TZ)
3. Fetches the Open-Meteo historical archive
   (`https://archive-api.open-meteo.com/v1/archive`) at the track's centroid lat/lon for
   the trip's local date, slices hourly readings to the trip window, and emits a
   one-line summary: sky (translated from WMO weather code), temp range, wind range +
   dominant compass direction, total precipitation.
4. Renders `img/<slug>/elevation.png` (matplotlib filled-area chart). The interactive
   Leaflet map replaces the old static `track.png` (no longer emitted).
5. Copies the `.gpx` to `img/<slug>/track.gpx` so Leaflet can fetch it.
6. Copies any photos from the data folder to `img/<slug>/`.
7. Writes `tripreports/<year>/<Trip_Name>.html` from `templates/tripreport.html.j2`.
8. Appends a `<li>` link to `tripreports.html` under the appropriate year.

`slug` = basename of the `--data` folder (e.g. `snoqualmie_mtn`).

## Map conventions (template)
- **Esri World Imagery** is the base layer (added first).
- **OpenTopoMap** is overlaid on top at 50% opacity by default.
- A custom Leaflet `BlendControl` (top-right) provides a Satellite↔Topo slider that
  drives `topo.setOpacity(slider.value/100)`. Order matters: base must be added before
  the overlay, otherwise the satellite hides the topo on initial load.
- `L.DomEvent.disableClickPropagation` on the control prevents the map from panning
  while dragging the slider.

## Stat caveats (Gaia divergence)
Numbers computed from raw GPX will **not** exactly match the Gaia app:
- **Distance** is inflated by GPS jitter (every micro-segment is summed). Gaia filters
  these out, so its distance is usually 10–20% lower.
- **Gain** is the noisiest channel — raw deltas can balloon to ~2× the real value. The
  15-point moving avg + 3 m threshold approach lands close to Gaia but rarely exact.
- **Moving time** depends entirely on the threshold. Our `> 0.3 m/s` cutoff drops
  slow uphill snow travel that Gaia counts as moving (Gaia uses a "not stopped for N
  seconds" rule instead), so our moving time is often ~½ of Gaia's.

The `--gaia-*` flags handle this end-to-end: the summary block uses Gaia values, and
the per-point elevation/distance series get **calibrated** to those totals
(`calibrate_track_to_totals` in `generate.py`) so the elevation profile chart's x-axis
and visible climb amplitude also match Gaia rather than the noisy raw GPX.

## Weather caveats
- Open-Meteo archive is a gridded reanalysis at the trailhead/route elevation, not the
  summit. Real summit conditions are colder and windier; we no longer print the
  reanalysis caveat in the page itself, but be aware when reviewing the output.
- The archive typically lags real time by ~5 days; for a same-week trip the lookup may
  return nulls. The script logs a warning and continues without a weather line.

## Conventions enforced
- Output HTML mirrors the latest trip reports: `<div class="content col-6">` wrapper,
  back-link, stats summary, satellite+topo blend slider map, elevation profile,
  narrative section, photo placeholders, `<script src="../../js/site.js">` for
  click-to-zoom.
- Leaflet + `leaflet-gpx` are pulled from `unpkg.com` (no bundler).
- `staticwebapp.config.json` must contain `".gpx": "application/gpx+xml"` so the
  browser can fetch the track — the skill adds this entry if missing.
- Units: miles and feet (matches existing reports).
- The "Map Preview" static-PNG section was removed (the interactive map renders it
  redundant).
