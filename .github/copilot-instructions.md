# Copilot Instructions

## Project overview

Personal website at `brandonchastain.com` — a plain static site (HTML/CSS/JS, no build step, no framework, no package manager). Hosted on Azure Static Web Apps.

## Build / test / lint

There is no build, test, or lint tooling. Changes are validated by opening the HTML files in a browser (or via the SWA preview deployment created on PRs to `master`).

## Deployment

- Pushes and PRs to `master` trigger the workflows in `.github/workflows/azure-static-web-apps-*.yml`, which deploy the repo root to Azure Static Web Apps (`app_location: "/"`, `output_location: "."`).
- Three SWA workflows exist (delightful-pebble, thankful-field, wonderful-smoke) — each targets a separate SWA instance. Keep them in sync when editing workflow steps.
- Routing/headers/fallback are controlled by `staticwebapp.config.json` at the repo root.

## Structure & conventions

- `index.html` is the landing page; `projects.html` and `tripreports.html` are top-level hubs that link into per-page subdirectories (`projects/*.html`, `tripreports/<year>/*.html`, `blogs/projects/*.html`).
- Every page is hand-written HTML. New pages should:
  - Link `css/style.css` (and `css/dracula.css` for pages with code samples).
  - Include `<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1">`.
  - Wrap content in `<div class="content col-6">` and start sub-pages with a `Home` / `Back to ...` link, matching the existing pages.
  - Use relative paths (`../css/...`, `../js/...`) from subdirectories.
- Images live in `img/` and are served with a long `cache-control` header configured in `staticwebapp.config.json` — prefer adding new images there rather than alongside HTML.
- `js/site.js` provides click-to-zoom for `<img>` tags on pages that include it (see `projects/rssreader.html`). Include it via `<script src="../js/site.js"></script>` on any page that should support zoomable screenshots.
- When adding a new project/trip report, link it from the appropriate hub page (`projects.html` or `tripreports.html`) — there is no index generator.
