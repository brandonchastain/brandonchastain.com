# shipped-log

The bot behind [brandonchastain.com/shipped](https://brandonchastain.com/shipped/): a weekly log of what I shipped
across my personal GitHub repos. Every Sunday evening the `Shipped log (weekly)` workflow collects the week's
commits, merged PRs and releases, asks Claude Haiku for a short outcome-focused recap, writes
`shipped/weeks/YYYY-Www.md`, regenerates `shipped/index.html`, commits to master (which deploys the site),
and pings my phone via ntfy.

## How it works

1. `GitHubClient` lists my repos pushed in the last 7 days and pulls commits on the default branch (authored by me), merged PRs, releases and deployments.
2. `NoiseFilter` drops merge commits, Dependabot/Renovate bumps, formatting-only commits, and squash-merge commits that duplicate a listed PR.
3. `ClaudeSummarizer` sends the remaining items, grouped by repo, to `claude-haiku-5-5` with the prompt rules in `Summarizer.cs`. If the call fails it falls back to a plain list.
4. `MarkdownWriter` renders the week file, then rebuilds the site page from every file in `shipped/weeks/` so the page is always the full archive, newest first. Quiet weeks still get a file so the log has no gaps, but no notification.

## Setup

Nothing to deploy: GitHub Actions is the runtime and the site deploy is the existing Static Web Apps workflow.
Store the secrets once with `.\setup.ps1` (prompts for each; nothing is written to the repo), or by hand:

| Secret | Purpose |
|---|---|
| `SHIPPED_LOG_TOKEN` | Fine-grained PAT over all personal repos: read-only Contents, Metadata, Pull requests, Deployments; plus **read and write Contents on brandonchastain.com** so the bot's push triggers the site deploy |
| `ANTHROPIC_API_KEY` | Claude API key (optional; falls back to a plain list without it) |
| `NTFY_TOPIC` | Private ntfy topic name, same approach as the cost watchdog (optional) |
| `NTFY_ACCESS_TOKEN` | ntfy bearer token, only if the topic is protected (optional) |

Run locally from the repo root:

```bash
dotnet run --project tools/shipped-log/src/ShippedLog -- --dry-run
```

Flags: `--dry-run` prints without writing or notifying, `--no-ai` skips the model, `--end YYYY-MM-DD` picks the window end,
`--out <dir>` sets the site root (default: current directory), `--fixture path.json` replaces GitHub with a JSON fixture
(see `tests/ShippedLog.Tests/fixtures`; the repo names in it are synthetic).

Tests:

```bash
dotnet test tools/shipped-log/ShippedLog.sln
```
