#Requires -Version 5
# One-command update / migrate of a machine to the LATEST UtmOrchestrator release.
# Run in a NORMAL user PowerShell (it uses the user's proxy - GitHub in RU is reachable only so):
#
#   irm https://github.com/iMironRU/utm-orchestrator/releases/latest/download/update-machine.ps1 | iex
#
# It elevates (UAC), downloads the latest release via the proxy, detects the layout
# (flat -> migrate-to-bin.ps1 -OfferCleanup, bin -> install.ps1) and runs it.
# Log: C:\UtmOrchestrator-Migrate.log. Data/bindings and UTM services (Transport*) are untouched.
#
# NOTE: this bootstrap is ASCII-only on purpose. `irm` on a GitHub release asset (octet-stream)
# decodes as Latin1 in PS 5.1 and would mangle Cyrillic; the rich Russian output comes from
# migrate-to-bin.ps1 / install.ps1, which are run as UTF-8-BOM files and render correctly.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$bootUrl = 'https://github.com/iMironRU/utm-orchestrator/releases/latest/download/update-machine.ps1'

# --- self-elevate: rerun the SAME command in an admin window (stays open) ---
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $admin) {
    Write-Host 'Requesting administrator rights...' -ForegroundColor Cyan
    Start-Process powershell -Verb RunAs -ArgumentList `
        '-NoProfile', '-NoExit', '-ExecutionPolicy', 'Bypass', '-Command', "irm '$bootUrl' | iex"
    return
}

# Allow running the .ps1 files we downloaded (bypass Restricted policy for THIS process).
try { Set-ExecutionPolicy -Scope Process Bypass -Force } catch {}

$log = 'C:\UtmOrchestrator-Migrate.log'
try { Start-Transcript -Path $log -Append | Out-Null } catch {}
Write-Host ("=== {0}  update-machine on {1} ===" -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $env:COMPUTERNAME)
try {
    $h = @{ 'User-Agent' = 'utmo' }
    Write-Host 'Getting latest release (via proxy)...' -ForegroundColor Cyan
    $rel = Invoke-RestMethod 'https://api.github.com/repos/iMironRU/utm-orchestrator/releases/latest' -Headers $h -TimeoutSec 30
    $asset = $rel.assets | Where-Object { $_.name -like 'UtmOrchestrator-win-x64-*.zip' } | Select-Object -First 1
    if (-not $asset) { throw 'monolith UtmOrchestrator-win-x64-*.zip not found in latest release' }

    $work = Join-Path $env:TEMP ('utmo-' + $rel.tag_name)
    if (Test-Path $work) { [System.IO.Directory]::Delete($work, $true) }
    New-Item -ItemType Directory -Force $work | Out-Null

    Write-Host ("Downloading {0} ({1} MB)..." -f $asset.name, [math]::Round($asset.size / 1MB)) -ForegroundColor Cyan
    Invoke-WebRequest $asset.browser_download_url -OutFile (Join-Path $work 'pkg.zip') -UseBasicParsing
    Write-Host 'Extracting...' -ForegroundColor Cyan
    Expand-Archive (Join-Path $work 'pkg.zip') (Join-Path $work 'x') -Force
    $src = Join-Path $work 'x'

    $bin = Test-Path 'C:\UtmOrchestrator\bin\app\UtmOrchestrator.Service.dll'
    if ($bin) {
        Write-Host "Layout: bin -> install.ps1`n" -ForegroundColor Cyan
        & (Join-Path $src 'install.ps1')
    } else {
        Write-Host "Layout: flat -> migrate-to-bin.ps1`n" -ForegroundColor Cyan
        & (Join-Path $src 'migrate-to-bin.ps1') -OfferCleanup
    }
    Write-Host ("`nDone. Panel: http://localhost:8090  (version {0})" -f $rel.tag_name) -ForegroundColor Green
} catch {
    Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    Write-Host 'If GitHub is blocked, ensure the proxy is on (the browser opens github).' -ForegroundColor Yellow
} finally {
    try { Stop-Transcript | Out-Null } catch {}
    Write-Host ("Log: {0}" -f $log) -ForegroundColor Cyan
}
