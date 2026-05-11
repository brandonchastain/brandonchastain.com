"""Headless-Chromium check for broken <img> tags (static + JS-injected).

Loads each tripreports/*/*.html via file:// and flags images that finished
loading but have naturalWidth === 0. Writes broken-images.json next to this
script. Exit code 0 if all clean, 1 if any failures.
"""
from pathlib import Path
import glob, json, sys
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]
OUT = Path(__file__).resolve().parent / "broken-images.json"

def main():
    pages = sorted(glob.glob(str(ROOT / "tripreports" / "2*" / "*.html")))
    report = {}
    with sync_playwright() as p:
        browser = p.chromium.launch()
        ctx = browser.new_context()
        for page_path in pages:
            url = Path(page_path).as_uri()
            page = ctx.new_page()
            try:
                page.goto(url, wait_until="networkidle", timeout=30000)
            except Exception as exc:
                print(f"[warn] {page_path}: load timeout ({exc})")
            broken = page.evaluate("""
                () => Array.from(document.images)
                      .filter(i => i.complete && i.naturalWidth === 0
                                   && i.getAttribute('src')
                                   && !i.classList.contains('lightbox-img'))
                      .map(i => i.src)
            """)
            page.close()
            rel = str(Path(page_path).relative_to(ROOT)).replace("\\", "/")
            if broken:
                report[rel] = broken
                print(f"[bad] {rel}  {len(broken)} broken")
            else:
                print(f"[ok]  {rel}")
        browser.close()
    OUT.write_text(json.dumps(report, indent=2), encoding="utf-8")
    total = sum(len(v) for v in report.values())
    print(f"\nTotal: {total} broken images across {len(report)} page(s); report -> {OUT.relative_to(ROOT)}")
    sys.exit(1 if total else 0)

if __name__ == "__main__":
    main()
