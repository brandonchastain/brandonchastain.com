"""Generate a trip-report HTML page from a Gaia GPS export folder.

See ../SKILL.md for usage. Python is local-only; the produced HTML is plain
static markup that Azure Static Web Apps serves directly.
"""

from __future__ import annotations

import argparse
import json
import math
import re
import shutil
import sys
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from datetime import timedelta, timezone
from pathlib import Path
from zoneinfo import ZoneInfo

import gpxpy
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from jinja2 import Environment, FileSystemLoader, select_autoescape

REPO_ROOT = Path(__file__).resolve().parents[3].parent  # .../skills/tripreport/scripts -> repo
# Walk up until we find a directory that contains tripreports/ (handles either layout).
def _find_repo_root(start: Path) -> Path:
    for p in [start, *start.parents]:
        if (p / "tripreports.html").is_file():
            return p
    raise SystemExit("Could not locate repo root (no tripreports.html found upward).")

REPO_ROOT = _find_repo_root(Path(__file__).resolve())
TEMPLATE_DIR = Path(__file__).resolve().parent.parent / "templates"

EARTH_RADIUS_M = 6_378_137.0
M_TO_MI = 0.000621371
M_TO_FT = 3.28084
IMG_EXTS = {".jpg", ".jpeg", ".png"}


# ---------- GPX parsing & stats ----------

@dataclass
class TrackStats:
    distance_m: float
    gain_m: float
    duration: timedelta | None
    moving: timedelta | None
    stopped: timedelta | None
    date_iso: str
    year: int
    name_from_gpx: str | None
    start_utc: object | None
    end_utc: object | None
    lats: np.ndarray
    lons: np.ndarray
    eles: np.ndarray
    cum_dist_m: np.ndarray


def _haversine_m(lat1, lon1, lat2, lon2):
    lat1r, lat2r = math.radians(lat1), math.radians(lat2)
    dlat = math.radians(lat2 - lat1)
    dlon = math.radians(lon2 - lon1)
    a = math.sin(dlat / 2) ** 2 + math.cos(lat1r) * math.cos(lat2r) * math.sin(dlon / 2) ** 2
    return 2 * EARTH_RADIUS_M * math.asin(math.sqrt(a))


def parse_gpx(gpx_path: Path) -> TrackStats:
    with gpx_path.open("r", encoding="utf-8") as f:
        gpx = gpxpy.parse(f)

    pts = []
    for trk in gpx.tracks:
        for seg in trk.segments:
            pts.extend(seg.points)
    if not pts:
        # Waypoint-only GPX: synthesize a minimal "track" of one point so the
        # rest of the pipeline runs. Stats and date must come from --gaia-* flags
        # or from photos.json. Caller is responsible for the overrides.
        if not gpx.waypoints:
            raise SystemExit(f"No track points or waypoints in {gpx_path}")
        wp = gpx.waypoints[0]
        return TrackStats(
            distance_m=0.0, gain_m=0.0, duration=None, moving=None, stopped=None,
            date_iso="unknown", year=0, name_from_gpx=wp.name,
            start_utc=None, end_utc=None,
            lats=np.array([wp.latitude]), lons=np.array([wp.longitude]),
            eles=np.array([0.0]), cum_dist_m=np.array([0.0]),
        )

    lats = np.array([p.latitude for p in pts])
    lons = np.array([p.longitude for p in pts])
    eles = np.array([p.elevation if p.elevation is not None else np.nan for p in pts])

    # Cumulative distance (meters).
    cum = [0.0]
    for i in range(1, len(pts)):
        cum.append(cum[-1] + _haversine_m(lats[i - 1], lons[i - 1], lats[i], lons[i]))
    cum_dist_m = np.array(cum)

    # Elevation gain: smooth then sum positive deltas.
    if np.isnan(eles).any():
        # Forward-fill NaNs to keep length consistent.
        good = ~np.isnan(eles)
        if good.any():
            eles = np.interp(np.arange(len(eles)), np.where(good)[0], eles[good])
        else:
            eles = np.zeros_like(eles)
    window = 5
    if len(eles) >= window:
        kernel = np.ones(window) / window
        smoothed = np.convolve(eles, kernel, mode="same")
    else:
        smoothed = eles
    deltas = np.diff(smoothed)
    gain_m = float(np.sum(deltas[deltas > 0]))

    # Duration, moving time, date.
    times = [p.time for p in pts if p.time is not None]
    start_utc = end_utc = None
    moving = stopped = None
    if times:
        start_utc = times[0]
        end_utc = times[-1]
        duration = end_utc - start_utc
        date_iso = start_utc.date().isoformat()
        year = start_utc.year
        # Moving time: count segments where speed > 0.3 m/s.
        moving_s = 0.0
        for i in range(1, len(pts)):
            if pts[i].time is None or pts[i - 1].time is None:
                continue
            dt = (pts[i].time - pts[i - 1].time).total_seconds()
            if dt <= 0:
                continue
            d = _haversine_m(lats[i - 1], lons[i - 1], lats[i], lons[i])
            if d / dt > 0.3:
                moving_s += dt
        moving = timedelta(seconds=int(moving_s))
        stopped = duration - moving if duration else None
    else:
        duration = None
        date_iso = "unknown"
        year = 0

    name_from_gpx = None
    for trk in gpx.tracks:
        if trk.name:
            name_from_gpx = trk.name
            break

    return TrackStats(
        distance_m=float(cum_dist_m[-1]),
        gain_m=gain_m,
        duration=duration,
        moving=moving,
        stopped=stopped,
        date_iso=date_iso,
        year=year,
        name_from_gpx=name_from_gpx,
        start_utc=start_utc,
        end_utc=end_utc,
        lats=lats,
        lons=lons,
        eles=eles,
        cum_dist_m=cum_dist_m,
    )


# ---------- Weather (Open-Meteo historical archive) ----------

WMO_CODES = {
    0: "Clear", 1: "Mainly clear", 2: "Partly cloudy", 3: "Overcast",
    45: "Fog", 48: "Rime fog",
    51: "Light drizzle", 53: "Drizzle", 55: "Heavy drizzle",
    61: "Light rain", 63: "Rain", 65: "Heavy rain",
    71: "Light snow", 73: "Snow", 75: "Heavy snow",
    77: "Snow grains",
    80: "Rain showers", 81: "Rain showers", 82: "Heavy rain showers",
    85: "Snow showers", 86: "Heavy snow showers",
    95: "Thunderstorm", 96: "Thunderstorm w/ hail", 99: "Thunderstorm w/ hail",
}


def _deg_to_compass(deg: float) -> str:
    dirs = ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
            "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"]
    return dirs[int((deg / 22.5) + 0.5) % 16]


def fetch_weather_summary(stats: TrackStats, tz_name: str) -> str | None:
    if stats.start_utc is None or stats.end_utc is None:
        return None
    tz = ZoneInfo(tz_name)
    start_local = stats.start_utc.astimezone(tz)
    end_local = stats.end_utc.astimezone(tz)
    # Use the GPX start point (trailhead) rather than centroid so the lookup
    # lands in the lower-elevation grid cell that matches what the hiker
    # experiences for most of the route; centroid biases toward summit cells.
    lat = float(stats.lats[0])
    lon = float(stats.lons[0])
    params = {
        "latitude": f"{lat:.4f}",
        "longitude": f"{lon:.4f}",
        "start_date": start_local.date().isoformat(),
        "end_date": end_local.date().isoformat(),
        "hourly": "temperature_2m,precipitation,cloudcover,windspeed_10m,winddirection_10m,weathercode",
        "temperature_unit": "fahrenheit",
        "windspeed_unit": "mph",
        "precipitation_unit": "inch",
        "timezone": tz_name,
    }
    url = "https://archive-api.open-meteo.com/v1/archive?" + urllib.parse.urlencode(params)
    try:
        with urllib.request.urlopen(url, timeout=15) as resp:
            data = json.loads(resp.read())
    except Exception as exc:
        print(f"[warn] weather fetch failed: {exc}")
        return None
    h = data.get("hourly", {})
    times = h.get("time", [])
    if not times:
        return None
    # Slice to trip window (inclusive of hours touched).
    sh = start_local.replace(minute=0, second=0, microsecond=0, tzinfo=None).isoformat(timespec="minutes")
    eh = end_local.replace(minute=0, second=0, microsecond=0, tzinfo=None).isoformat(timespec="minutes")
    idx = [i for i, t in enumerate(times) if sh <= t <= eh]
    if not idx:
        return None
    def pick(key): return [h[key][i] for i in idx if h[key][i] is not None]
    temps = pick("temperature_2m")
    clouds = pick("cloudcover")
    winds = pick("windspeed_10m")
    dirs = pick("winddirection_10m")
    precs = pick("precipitation")
    codes = pick("weathercode")
    if not temps:
        return None
    sky_start = WMO_CODES.get(int(codes[0]), "").lower()
    sky_end = WMO_CODES.get(int(codes[-1]), "").lower()
    if sky_start == sky_end:
        sky = sky_start.capitalize()
    else:
        sky = f"{sky_start.capitalize()} at start, {sky_end} by the descent"
    precip_total = sum(precs) if precs else 0
    precip_note = "no precipitation" if precip_total < 0.01 else f"{precip_total:.2f} in precipitation"
    wind_lo, wind_hi = min(winds), max(winds)
    wind_str = f"{wind_lo:.0f}&ndash;{wind_hi:.0f} mph" if wind_hi - wind_lo > 1 else f"~{wind_hi:.0f} mph"
    avg_dir = _deg_to_compass(float(np.mean(dirs))) if dirs else ""
    return (f"{sky}. Temps {min(temps):.0f}&ndash;{max(temps):.0f}&deg;F, "
            f"{avg_dir} winds {wind_str}, {precip_note}.")


# ---------- Rendering ----------

def render_elevation(stats: TrackStats, out_png: Path) -> None:
    dist_mi = stats.cum_dist_m * M_TO_MI
    ele_ft = stats.eles * M_TO_FT
    fig, ax = plt.subplots(figsize=(8, 3.5), dpi=120)
    ax.fill_between(dist_mi, ele_ft, ele_ft.min(), alpha=0.3, color="#b35a1f")
    ax.plot(dist_mi, ele_ft, color="#b35a1f", linewidth=1.8)
    ax.set_xlabel("Distance (miles)")
    ax.set_ylabel("Elevation (feet)")
    ax.grid(True, alpha=0.3)
    ax.margins(x=0)
    out_png.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(out_png, bbox_inches="tight")
    plt.close(fig)


# ---------- HTML / hub link ----------

def _filename_from_name(name: str) -> str:
    cleaned = re.sub(r"\s+", "_", name.strip())
    cleaned = re.sub(r"[^A-Za-z0-9_\-]", "", cleaned)
    return cleaned + ".html"


def _parse_hms(s: str) -> timedelta:
    """Accept '4h 16m', '4:16', '4:16:00', or plain minutes."""
    s = s.strip().lower()
    if not s:
        raise ValueError("empty")
    if "h" in s or "m" in s:
        h = m = 0
        mh = re.search(r"(\d+)\s*h", s)
        mm = re.search(r"(\d+)\s*m", s)
        if mh: h = int(mh.group(1))
        if mm: m = int(mm.group(1))
        return timedelta(hours=h, minutes=m)
    if ":" in s:
        parts = [int(p) for p in s.split(":")]
        while len(parts) < 3: parts.append(0)
        return timedelta(hours=parts[0], minutes=parts[1], seconds=parts[2])
    return timedelta(minutes=int(s))


def calibrate_track_to_totals(stats: TrackStats) -> None:
    """After Gaia overrides, scale per-point GPX series so the rendered
    elevation profile matches the corrected totals.

    - cum_dist_m is linearly rescaled so its max equals the corrected distance
      (fixes the elevation chart's x-axis).
    - Elevation series is smoothed with a window auto-selected so its
      positive-delta sum lands closest to the corrected gain (fixes the
      visible noise/amplitude of the climb in the chart).
    """
    raw_total = float(stats.cum_dist_m[-1]) if len(stats.cum_dist_m) else 0.0
    if raw_total > 0 and abs(stats.distance_m - raw_total) > 1.0:
        scale = stats.distance_m / raw_total
        stats.cum_dist_m = stats.cum_dist_m * scale

    if len(stats.eles) < 3:
        return
    target_gain_m = stats.gain_m
    best = None
    for w in (5, 11, 21, 41, 81, 161):
        if w > len(stats.eles):
            break
        pad = w // 2
        # Edge-pad with the first/last value so the rolling mean has real
        # data on both sides — avoids the zero-padding plunge that np.convolve's
        # mode="same" would otherwise introduce at the start/end of the track.
        padded = np.pad(stats.eles, pad, mode="edge")
        kernel = np.ones(w) / w
        smoothed = np.convolve(padded, kernel, mode="valid")[: len(stats.eles)]
        deltas = np.diff(smoothed)
        gain = float(np.sum(deltas[deltas > 0]))
        if best is None or abs(gain - target_gain_m) < abs(best[1] - target_gain_m):
            best = (w, gain, smoothed)
    if best is not None:
        win, gain, smoothed = best
        print(f"[calibrate] elevation smoothing window={win}, profile gain={gain * M_TO_FT:.0f} ft (target {target_gain_m * M_TO_FT:.0f})")
        stats.eles = smoothed


def apply_gaia_overrides(stats: TrackStats, *,
                         distance_mi: float | None,
                         gain_ft: float | None,
                         moving: str | None) -> None:
    """Replace GPX-estimated stats with Gaia-app values supplied via CLI flags.

    Mutates `stats` in place. Total duration is left alone (timestamps are
    authoritative); only distance / gain / moving time get overridden, with
    stopped recomputed as total - moving.
    """
    if distance_mi is not None:
        print(f"[gaia] distance: {stats.distance_m * M_TO_MI:.2f} mi -> {distance_mi:.2f} mi")
        stats.distance_m = distance_mi / M_TO_MI
    if gain_ft is not None:
        print(f"[gaia] gain: {int(round(stats.gain_m * M_TO_FT)):,} ft -> {int(gain_ft):,} ft")
        stats.gain_m = gain_ft / M_TO_FT
    if moving:
        td = _parse_hms(moving)
        print(f"[gaia] moving: {_format_duration(stats.moving)} -> {_format_duration(td)}")
        stats.moving = td
        if stats.duration:
            stats.stopped = stats.duration - td


def _format_duration(td: timedelta | None) -> str:
    if td is None:
        return "unknown"
    total_minutes = int(td.total_seconds() // 60)
    hours, minutes = divmod(total_minutes, 60)
    if hours and minutes:
        return f"{hours}h {minutes}m"
    if hours:
        return f"{hours}h"
    return f"{minutes}m"


def render_html(stats: TrackStats, *, name: str, region: str, slug: str, photos: list[str],
                photos_meta: list, tz_name: str, weather: str | None, out_html: Path,
                waypoint_only: bool = False) -> None:
    env = Environment(
        loader=FileSystemLoader(str(TEMPLATE_DIR)),
        autoescape=select_autoescape(["html", "xml"]),
        trim_blocks=True,
        lstrip_blocks=True,
    )
    tpl = env.get_template("tripreport.html.j2")
    tz = ZoneInfo(tz_name)
    time_window = None
    if stats.start_utc and stats.end_utc:
        s = stats.start_utc.astimezone(tz).strftime("%H:%M")
        e = stats.end_utc.astimezone(tz).strftime("%H:%M")
        tz_abbr = stats.start_utc.astimezone(tz).strftime("%Z")
        time_window = f"{s}&ndash;{e} {tz_abbr}"
    total_h = stats.duration.total_seconds() / 3600 if stats.duration else None
    moving_h = stats.moving.total_seconds() / 3600 if stats.moving else None
    distance_mi = stats.distance_m * M_TO_MI
    avg_moving = distance_mi / moving_h if moving_h else None
    avg_overall = distance_mi / total_h if total_h else None
    rendered = tpl.render(
        name=name,
        region=region,
        slug=slug,
        date_str=stats.date_iso,
        time_window=time_window,
        distance_mi=distance_mi,
        gain_ft=int(round(stats.gain_m * M_TO_FT)),
        duration_str=_format_duration(stats.duration),
        moving_str=_format_duration(stats.moving),
        stopped_str=_format_duration(stats.stopped),
        avg_moving_mph=avg_moving,
        avg_overall_mph=avg_overall,
        weather=weather,
        photos=photos,
        photos_meta_json=json.dumps(photos_meta) if photos_meta else "[]",
        waypoint_only=waypoint_only,
        waypoint_lat=float(stats.lats[0]) if len(stats.lats) else 0,
        waypoint_lon=float(stats.lons[0]) if len(stats.lons) else 0,
    )
    out_html.parent.mkdir(parents=True, exist_ok=True)
    out_html.write_text(rendered, encoding="utf-8")


def append_hub_link(year: int, rel_href: str, name: str) -> None:
    hub = REPO_ROOT / "tripreports.html"
    text = hub.read_text(encoding="utf-8")
    link_line = f'            <a href="{rel_href}">{name}</a>'
    if rel_href in text:
        print(f"[info] tripreports.html already links {rel_href}; skipping")
        return
    # Insert a new <li> at the top of the existing <ul>, simplest stable edit.
    new_li = f"          <li>\n{link_line}\n          </li>\n"
    marker = "<ul>"
    idx = text.find(marker)
    if idx == -1:
        print("[warn] could not find <ul> in tripreports.html; appending nothing")
        return
    insert_at = idx + len(marker) + 1  # after "<ul>\n"
    text = text[:insert_at] + new_li + text[insert_at:]
    hub.write_text(text, encoding="utf-8")


def ensure_gpx_mime() -> None:
    cfg_path = REPO_ROOT / "staticwebapp.config.json"
    cfg = json.loads(cfg_path.read_text(encoding="utf-8"))
    mts = cfg.setdefault("mimeTypes", {})
    if mts.get(".gpx") == "application/gpx+xml":
        return
    mts[".gpx"] = "application/gpx+xml"
    cfg_path.write_text(json.dumps(cfg, indent=2) + "\n", encoding="utf-8")
    print("[info] added .gpx -> application/gpx+xml to staticwebapp.config.json")


# ---------- Orchestrator ----------

def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description="Generate a trip report from a Gaia GPS export.")
    p.add_argument("--data", required=True, help="Path to tripreports/data/<trip>/ folder")
    p.add_argument("--name", required=True, help="Trip display name, e.g. 'Snoqualmie Mountain'")
    p.add_argument("--region", required=True, help="Region label, e.g. 'Cascades, WA'")
    p.add_argument("--tz", default="America/Los_Angeles",
                   help="IANA timezone for the trip's local clock and weather lookup")
    p.add_argument("--no-weather", action="store_true", help="Skip Open-Meteo weather fetch")
    p.add_argument("--gaia-distance", type=float, default=None,
                   help="Distance in miles from the Gaia screenshot (overrides GPX estimate)")
    p.add_argument("--gaia-gain", type=float, default=None,
                   help="Elevation gain in feet from the Gaia screenshot")
    p.add_argument("--gaia-moving", default=None,
                   help='Moving time from Gaia, e.g. "4h 16m" or "4:15:44"')
    args = p.parse_args(argv)

    data_dir = Path(args.data).resolve()
    if not data_dir.is_dir():
        raise SystemExit(f"Data folder not found: {data_dir}")

    gpx_files = sorted(data_dir.glob("*.gpx"))
    if not gpx_files:
        raise SystemExit(f"No .gpx file in {data_dir}")
    gpx_path = gpx_files[0]
    print(f"[info] parsing {gpx_path.name}")
    stats = parse_gpx(gpx_path)
    print(
        f"[info] stats: {stats.distance_m * M_TO_MI:.2f} mi, "
        f"{int(round(stats.gain_m * M_TO_FT)):,} ft gain, "
        f"duration={_format_duration(stats.duration)}, date={stats.date_iso}"
    )

    slug = data_dir.name
    img_dir = REPO_ROOT / "img" / slug
    img_dir.mkdir(parents=True, exist_ok=True)

    shutil.copy2(gpx_path, img_dir / "track.gpx")

    # Copy photos (any image file in data_dir) and collect basenames in sort order.
    photos: list[str] = []
    for src in sorted(data_dir.iterdir()):
        if src.is_file() and src.suffix.lower() in IMG_EXTS:
            dst = img_dir / src.name
            shutil.copy2(src, dst)
            photos.append(src.name)
    if photos:
        print(f"[info] copied {len(photos)} photo(s) to {img_dir.relative_to(REPO_ROOT)}")

    # If find-trip-photos wrote a photos.json sidecar (filename + lat/lon + time),
    # copy it through so the page can render gallery dots on the map.
    photos_meta = []
    sidecar = data_dir / "photos.json"
    if sidecar.is_file():
        shutil.copy2(sidecar, img_dir / "photos.json")
        try:
            photos_meta = json.loads(sidecar.read_text(encoding="utf-8"))
        except Exception as exc:
            print(f"[warn] could not parse {sidecar}: {exc}")

    # For waypoint-only trips, infer date/year/duration from photos.json.
    waypoint_only = stats.year == 0 and len(stats.lats) == 1
    if waypoint_only and photos_meta:
        from datetime import datetime as _dt, timezone as _tz
        times = []
        for entry in photos_meta:
            t = entry.get("time")
            if not t:
                continue
            try:
                times.append(_dt.fromisoformat(t.replace("Z", "+00:00")))
            except ValueError:
                continue
        if times:
            times.sort()
            # Assume photo EXIF times are naive local; tag them with the configured tz.
            tz = ZoneInfo(args.tz)
            if times[0].tzinfo is None:
                times = [t.replace(tzinfo=tz) for t in times]
            stats.start_utc = times[0].astimezone(timezone.utc)
            stats.end_utc = times[-1].astimezone(timezone.utc)
            stats.duration = stats.end_utc - stats.start_utc
            stats.date_iso = times[0].astimezone(tz).date().isoformat()
            stats.year = times[0].astimezone(tz).year
    year = stats.year or 0
    if year == 0:
        raise SystemExit("Could not determine year from GPX timestamps or photos.json")
    filename = _filename_from_name(args.name)
    out_html = REPO_ROOT / "tripreports" / str(year) / filename
    apply_gaia_overrides(stats,
                        distance_mi=args.gaia_distance,
                        gain_ft=args.gaia_gain,
                        moving=args.gaia_moving)
    if not waypoint_only:
        calibrate_track_to_totals(stats)
        print("[info] rendering elevation profile PNG (post-calibration)")
        render_elevation(stats, img_dir / "elevation.png")
    else:
        print("[info] waypoint-only trip; skipping elevation profile")

    weather = None
    if not args.no_weather:
        print("[info] fetching historical weather from Open-Meteo")
        weather = fetch_weather_summary(stats, args.tz)
        if weather:
            print(f"[info] weather: {weather}")
    print(f"[info] writing {out_html.relative_to(REPO_ROOT)}")
    render_html(stats, name=args.name, region=args.region, slug=slug, photos=photos, photos_meta=photos_meta,
                waypoint_only=waypoint_only,
                tz_name=args.tz, weather=weather, out_html=out_html)

    rel_href = f"tripreports/{year}/{filename}"
    append_hub_link(year, rel_href, args.name)
    ensure_gpx_mime()

    print("\nDone. Preview: open " + str(out_html))
    return 0


if __name__ == "__main__":
    sys.exit(main())
