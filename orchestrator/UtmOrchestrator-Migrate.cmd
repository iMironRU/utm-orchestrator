@echo off
chcp 65001 >nul
setlocal
title UTM Orchestrator - migrate to bin layout
REM ============================================================================
REM  Double-click: migrate a FLAT UtmOrchestrator install to the new bin layout,
REM  downloading the LATEST release from GitHub. Elevates via UAC.
REM  Safe and REVERSIBLE: old files are kept; rollback command is printed at the end.
REM  On a machine already on bin, migrate-to-bin.ps1 detects it and does nothing.
REM  (This wrapper is ASCII-only on purpose; the rich Russian output comes from
REM   migrate-to-bin.ps1, which is UTF-8 with BOM and renders correctly.)
REM ============================================================================

REM --- self-elevate to administrator ---
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Requesting administrator rights...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

echo === UTM Orchestrator: migrate flat -^> bin (latest release) ===
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$ProgressPreference='SilentlyContinue';" ^
  "try { [Console]::OutputEncoding=[System.Text.Encoding]::UTF8 } catch {};" ^
  "$h=@{'User-Agent'='utmo'};" ^
  "$rel=Invoke-RestMethod 'https://api.github.com/repos/iMironRU/utm-orchestrator/releases/latest' -Headers $h -TimeoutSec 30;" ^
  "$a=$rel.assets | Where-Object { $_.name -like 'UtmOrchestrator-win-x64-*.zip' } | Select-Object -First 1;" ^
  "if(-not $a){ throw 'monolith UtmOrchestrator-win-x64-*.zip not found in latest release' };" ^
  "$w=Join-Path $env:TEMP ('utmo-'+$rel.tag_name);" ^
  "if(Test-Path $w){ [System.IO.Directory]::Delete($w,$true) };" ^
  "New-Item -ItemType Directory -Force $w | Out-Null;" ^
  "Write-Host ('Release '+$rel.tag_name+'. Downloading '+$a.name+' ('+[math]::Round($a.size/1MB)+' MB)...') -ForegroundColor Cyan;" ^
  "Invoke-WebRequest $a.browser_download_url -OutFile (Join-Path $w 'pkg.zip') -UseBasicParsing;" ^
  "Write-Host 'Extracting...' -ForegroundColor Cyan;" ^
  "Expand-Archive (Join-Path $w 'pkg.zip') (Join-Path $w 'x') -Force;" ^
  "& (Join-Path $w 'x\migrate-to-bin.ps1') -OfferCleanup"

echo.
echo Done. You can close this window.
pause
