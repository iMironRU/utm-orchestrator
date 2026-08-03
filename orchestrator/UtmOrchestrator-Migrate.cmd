@echo off
setlocal
title UTM:Orchestrator - переход на новую раскладку (bin)
REM ============================================================================
REM  Двойной клик: переводит ПЛОСКУЮ установку Оркестратора на новую bin-раскладку,
REM  скачивая ПОСЛЕДНИЙ релиз с GitHub. Запросит права администратора (UAC).
REM  БЕЗОПАСНО и ОБРАТИМО: старые файлы остаются, откат печатается в конце.
REM  На машине, которая уже на bin, ничего не ломает (миграция сама увидит и выйдет).
REM ============================================================================

REM --- само-повышение прав администратора ---
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Запрашиваю права администратора...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

echo === UTM:Orchestrator: перевод плоской установки -^> bin (последний релиз) ===
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$ProgressPreference='SilentlyContinue';" ^
  "try { [Console]::OutputEncoding=[System.Text.Encoding]::UTF8 } catch {};" ^
  "$h=@{'User-Agent'='utmo'};" ^
  "$rel=Invoke-RestMethod 'https://api.github.com/repos/iMironRU/utm-orchestrator/releases/latest' -Headers $h -TimeoutSec 30;" ^
  "$a=$rel.assets | Where-Object { $_.name -like 'UtmOrchestrator-win-x64-*.zip' } | Select-Object -First 1;" ^
  "if(-not $a){ throw 'в последнем релизе не найден монолит UtmOrchestrator-win-x64-*.zip' };" ^
  "$w=Join-Path $env:TEMP ('utmo-'+$rel.tag_name);" ^
  "if(Test-Path $w){ [System.IO.Directory]::Delete($w,$true) };" ^
  "New-Item -ItemType Directory -Force $w | Out-Null;" ^
  "Write-Host ('Релиз '+$rel.tag_name+'. Качаю '+$a.name+' ('+[math]::Round($a.size/1MB)+' МБ)...') -ForegroundColor Cyan;" ^
  "Invoke-WebRequest $a.browser_download_url -OutFile (Join-Path $w 'pkg.zip') -UseBasicParsing;" ^
  "Write-Host 'Распаковываю...' -ForegroundColor Cyan;" ^
  "Expand-Archive (Join-Path $w 'pkg.zip') (Join-Path $w 'x') -Force;" ^
  "& (Join-Path $w 'x\migrate-to-bin.ps1');" ^
  "Start-Sleep 6;" ^
  "try{ $s=Invoke-RestMethod 'http://127.0.0.1:8090/api/status' -TimeoutSec 20; Write-Host ('РЕЗУЛЬТАТ: '+$s.ok+'/'+$s.total+' работают, версия '+$s.orchestratorVersion) -ForegroundColor Green }catch{ Write-Host 'Панель ещё не поднялась - открой http://localhost:8090 через минуту' -ForegroundColor Yellow };" ^
  "Write-Host '';" ^
  "Write-Host ('Если что-то не так, откат: powershell -ExecutionPolicy Bypass -File \"'+(Join-Path $w 'x\migrate-to-bin.ps1')+'\" -Rollback') -ForegroundColor DarkGray"

echo.
echo Готово. Окно можно закрыть.
pause
