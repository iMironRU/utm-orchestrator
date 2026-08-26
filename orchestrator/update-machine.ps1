#Requires -Version 5
<#
  Одной командой: обновить/перевести машину на ПОСЛЕДНЮЮ версию УТМ:Оркестратора.
  Запуск в ОБЫЧНОМ PowerShell пользователя (берёт его прокси — GitHub в РФ доступен только так):

    irm https://github.com/iMironRU/utm-orchestrator/releases/latest/download/update-machine.ps1 | iex

  Скрипт сам: запросит администратора (UAC) → скачает последний релиз через прокси →
  определит раскладку (плоская/bin) → запустит migrate-to-bin.ps1 или install.ps1.
  Лог: C:\UtmOrchestrator-Migrate.log. Данные/привязки и службы УТМ (Transport*) не трогаются.
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$bootUrl = 'https://github.com/iMironRU/utm-orchestrator/releases/latest/download/update-machine.ps1'

# --- само-повышение прав: перезапускаем ТУ ЖЕ команду в админ-окне (остаётся открытым) ---
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $admin) {
    Write-Host 'Запрашиваю права администратора...' -ForegroundColor Cyan
    Start-Process powershell -Verb RunAs -ArgumentList `
        '-NoProfile', '-NoExit', '-ExecutionPolicy', 'Bypass', '-Command', "irm '$bootUrl' | iex"
    return
}

$log = 'C:\UtmOrchestrator-Migrate.log'
try { Start-Transcript -Path $log -Append | Out-Null } catch {}
Write-Host ("=== {0}  update-machine на {1} ===" -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $env:COMPUTERNAME)
try {
    $h = @{ 'User-Agent' = 'utmo' }
    Write-Host 'Узнаю последний релиз (через прокси)...' -ForegroundColor Cyan
    $rel = Invoke-RestMethod 'https://api.github.com/repos/iMironRU/utm-orchestrator/releases/latest' -Headers $h -TimeoutSec 30
    $asset = $rel.assets | Where-Object { $_.name -like 'UtmOrchestrator-win-x64-*.zip' } | Select-Object -First 1
    if (-not $asset) { throw 'монолит UtmOrchestrator-win-x64-*.zip не найден в последнем релизе' }

    $work = Join-Path $env:TEMP ('utmo-' + $rel.tag_name)
    if (Test-Path $work) { [System.IO.Directory]::Delete($work, $true) }
    New-Item -ItemType Directory -Force $work | Out-Null

    Write-Host ("Качаю {0} ({1} МБ)..." -f $asset.name, [math]::Round($asset.size / 1MB)) -ForegroundColor Cyan
    Invoke-WebRequest $asset.browser_download_url -OutFile (Join-Path $work 'pkg.zip') -UseBasicParsing
    Write-Host 'Распаковываю...' -ForegroundColor Cyan
    Expand-Archive (Join-Path $work 'pkg.zip') (Join-Path $work 'x') -Force
    $src = Join-Path $work 'x'

    $bin = Test-Path 'C:\UtmOrchestrator\bin\app\UtmOrchestrator.Service.dll'
    if ($bin) {
        Write-Host "Раскладка bin — обновляю (install.ps1)`n" -ForegroundColor Cyan
        & (Join-Path $src 'install.ps1')
    } else {
        Write-Host "Раскладка плоская — перевожу в bin (migrate-to-bin.ps1)`n" -ForegroundColor Cyan
        & (Join-Path $src 'migrate-to-bin.ps1') -OfferCleanup
    }
    Write-Host "`nГотово. Проверь панель: http://localhost:8090 (версия $($rel.tag_name))" -ForegroundColor Green
} catch {
    Write-Host ('ОШИБКА: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    Write-Host 'Если это блокировка GitHub — убедись, что прокси/обход включён (браузер гит открывает).' -ForegroundColor Yellow
} finally {
    try { Stop-Transcript | Out-Null } catch {}
    Write-Host ("Лог: {0}" -f $log) -ForegroundColor Cyan
}
