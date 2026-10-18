# setup.ps1
# One-time setup for the shipped log. Nothing to deploy: GitHub Actions runs it and the site deploy picks up the commit.
# This script only stores the secrets the workflow needs, prompting for each so none of them touch the repo.
#
# Usage: .\setup.ps1                    # prompt for every secret
#        .\setup.ps1 -SkipAnthropic     # no Claude key; the job writes the plain PR/commit list instead
#        .\setup.ps1 -SkipNtfy          # no phone notification
# Requires: gh CLI logged in as the repo owner. Values are also read from $env:SHIPPED_LOG_TOKEN,
# $env:ANTHROPIC_API_KEY, $env:NTFY_TOPIC and $env:NTFY_ACCESS_TOKEN when set.

[CmdletBinding()]
param(
    [string]$Repo = "brandonchastain/brandonchastain.com",
    [switch]$SkipAnthropic,
    [switch]$SkipNtfy
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

gh auth status 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error "gh is not logged in. Run 'gh auth login' and retry."; exit 1 }
Write-Host "Repo: $Repo" -ForegroundColor Cyan

function Read-Secret([string]$Prompt) {
    $secure = Read-Host -AsSecureString $Prompt
    return [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
}

function Set-RepoSecret([string]$Name, [string]$Value) {
    if (-not $Value) { Write-Error "$Name is required."; exit 1 }
    $Value | gh secret set $Name --repo $Repo
    if ($LASTEXITCODE -ne 0) { Write-Error "Failed to set $Name."; exit 1 }
    Write-Host "  set $Name" -ForegroundColor Green
}

# GitHub: fine-grained PAT, all personal repos, read-only Contents, Metadata, Pull requests, Deployments.
# The workflow's built-in GITHUB_TOKEN only sees this repo, so a PAT is needed to read the others.
$ghToken = $env:SHIPPED_LOG_TOKEN
if (-not $ghToken) { $ghToken = Read-Secret "GitHub fine-grained PAT (read on all repos, contents write on brandonchastain.com)" }
Set-RepoSecret "SHIPPED_LOG_TOKEN" $ghToken

if (-not $SkipAnthropic) {
    $key = $env:ANTHROPIC_API_KEY
    if (-not $key) { $key = Read-Secret "Anthropic API key (Console, pay-as-you-go; Enter to skip)" }
    if ($key) { Set-RepoSecret "ANTHROPIC_API_KEY" $key } else { Write-Host "  skipped ANTHROPIC_API_KEY (plain-list fallback)" }
}

if (-not $SkipNtfy) {
    $topic = $env:NTFY_TOPIC
    if (-not $topic) { $topic = Read-Secret "ntfy topic (same private topic as the cost watchdog; Enter to skip)" }
    if ($topic) {
        Set-RepoSecret "NTFY_TOPIC" $topic
        $token = $env:NTFY_ACCESS_TOKEN
        if (-not $token) { $token = Read-Secret "ntfy access token (Enter if the topic is open)" }
        if ($token) { Set-RepoSecret "NTFY_ACCESS_TOKEN" $token }
    }
    else { Write-Host "  skipped NTFY_TOPIC (no notification)" }
}

Write-Host "`nDone. Dry-run the workflow now with:" -ForegroundColor Green
Write-Host "  gh workflow run release-notes.yml --repo $Repo -f dry_run=true"
Write-Host "  gh run watch --repo $Repo"
