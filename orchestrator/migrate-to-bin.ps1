#Requires -RunAsAdministrator
<#
  Перевод существующей ПЛОСКОЙ установки оркестратора в раскладку bin/.
  Запускать ОТ АДМИНИСТРАТОРА из папки НОВОГО пейлоада (где лежат bin\ + *.ps1):
    powershell -ExecutionPolicy Bypass -File migrate-to-bin.ps1

  БЕЗОПАСНО И ОБРАТИМО: по умолчанию старые плоские файлы НЕ удаляются — только
  добавляется bin\ и служба перенацеливается на муксер. Откат: -Rollback (вернёт
  путь службы на старый exe). Чистка старых файлов — отдельно: -Cleanup (после того,
  как убедились, что всё работает).

  data\/utms\/cache\/transfer\ и appsettings сохраняются. Службы УТМ (Transport*) не трогаются.
#>
param(
  [string]$Dst = 'C:\UtmOrchestrator',
  [string]$ServiceName = 'UtmOrchestrator',
  [switch]$Cleanup,      # удалить старые плоские файлы (после успешной проверки)
  [switch]$Rollback,     # вернуть службу на старый плоский exe
  [switch]$OfferCleanup  # после миграции спросить и по «да» сразу почистить старые файлы
)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$flatExe = "$Dst\UtmOrchestrator.Service.exe"
$dotnet  = "$Dst\bin\runtime\dotnet.exe"
$svcDll  = "$Dst\bin\app\UtmOrchestrator.Service.dll"
$trayDll = "$Dst\bin\app\UtmOrchestrator.Tray.dll"

function Stop-Tray {
  Get-Process UtmOrchestrator.Tray -EA SilentlyContinue | Stop-Process -Force
  Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -EA SilentlyContinue |
    Where-Object { $_.CommandLine -like '*UtmOrchestrator.Tray.dll*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue }
}

# ============ ОТКАТ ============
if ($Rollback) {
  Write-Host "ОТКАТ: возвращаю службу на плоский exe" -ForegroundColor Yellow
  if (-not (Test-Path $flatExe)) { throw "старый $flatExe не найден — откат невозможен (файлы уже удалены -Cleanup?)" }
  Stop-Service $ServiceName -Force -EA SilentlyContinue
  Stop-Tray
  sc.exe config $ServiceName binPath= "`"$flatExe`"" | Out-Null
  # автозапуск трея — на плоский exe
  New-ItemProperty 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name UtmOrchestratorTray -Value "`"$Dst\UtmOrchestrator.Tray.exe`"" -PropertyType String -Force | Out-Null
  Start-Service $ServiceName
  Write-Host "Откат готов: служба снова с $flatExe" -ForegroundColor Green
  return
}

# ============ ЧИСТКА старых плоских файлов (после успешной миграции) ============
if ($Cleanup) {
  if (-not (Test-Path $svcDll)) { throw "bin\app не найден — сначала выполните миграцию (без -Cleanup)" }
  # Чистить можно только когда служба УЖЕ на муксере: иначе плоский exe запущен и держит
  # свои рантайм-DLL (clrjit/coreclr/hostfxr) — удаление упадёт, а служба останется на flat.
  $curBin = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -EA SilentlyContinue).PathName
  if ($curBin -notlike '*\bin\runtime\dotnet.exe*') {
    throw "служба ещё на плоском exe (binPath=$curBin). Сначала завершите миграцию, затем -Cleanup."
  }
  Write-Host "ЧИСТКА старых плоских файлов в корне (bin/data/utms/cache/transfer сохраняю)" -ForegroundColor Yellow
  $failed = @()
  # Удаляем известный мусор плоской раскладки в КОРНЕ, не трогая новую структуру.
  foreach ($f in Get-ChildItem "$Dst\*" -File -EA SilentlyContinue) {
    if ($f.Name -in @('runtime.key') -or $f.Extension -eq '.ps1' -or $f.Name -eq 'appsettings.json') { continue }
    if ($f.Extension -in @('.dll','.exe','.pdb','.json','.config','.xml')) {
      try { [System.IO.File]::Delete($f.FullName) } catch { $failed += $f.Name }
    }
  }
  foreach ($d in Get-ChildItem "$Dst\*" -Directory -EA SilentlyContinue) {
    if ($d.Name -in @('bin','data','utms','cache','transfer')) { continue }
    try { [System.IO.Directory]::Delete($d.FullName, $true) } catch { $failed += $d.Name }   # wwwroot, cs/de/…, utm-dist, exports, imports (старые)
  }
  if ($failed) { Write-Host "  не удалось удалить (заняты?): $($failed -join ', ') — повторите -Cleanup позже" -ForegroundColor Yellow }
  Write-Host "Готово. В корне: $((Get-ChildItem $Dst -Directory | % Name) -join ', ')" -ForegroundColor Green
  return
}

# ============ МИГРАЦИЯ (обратимая: старые файлы НЕ трогаем) ============
# Идемпотентно: если bin\ уже есть, но служба на плоском exe (например после -Rollback),
# повторный запуск ДОЛЖЕН заново перенацелить её на муксер, а не выходить без изменений.
$curBin  = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -EA SilentlyContinue).PathName
$onMuxer = $curBin -like '*\bin\runtime\dotnet.exe*'
if ((Test-Path $svcDll) -and $onMuxer) { Write-Host "Уже bin-раскладка, служба на муксере — миграция не нужна."; return }
if (-not (Test-Path $svcDll) -and -not (Test-Path $flatExe)) { throw "не похоже на установку оркестратора (нет ни $flatExe, ни bin\app)" }

Write-Host "Миграция плоской установки → bin: $Dst" -ForegroundColor Cyan

# 1) стоп службы + трея
Stop-Service $ServiceName -Force -EA SilentlyContinue
Stop-Tray
Start-Sleep 2

# 2) развернуть новый bin\ + скрипты (старые файлы в корне ОСТАЮТСЯ — для отката)
robocopy "$src\bin" "$Dst\bin" /E /NFL /NDL /NJH /NJS /NC /NS /R:1 /W:1 | Out-Null
Copy-Item "$src\uninstall.ps1" "$Dst\uninstall.ps1" -Force -EA SilentlyContinue
Copy-Item "$src\update.ps1"    "$Dst\update.ps1"    -Force -EA SilentlyContinue
Copy-Item "$src\runtime.key"   "$Dst\runtime.key"   -Force -EA SilentlyContinue
Write-Host "  bin\app + bin\runtime развёрнуты"

# 3) сохранить пользовательский appsettings.json (был в корне) → в bin\app
if (Test-Path "$Dst\appsettings.json") {
  Copy-Item "$Dst\appsettings.json" "$Dst\bin\app\appsettings.json" -Force -EA SilentlyContinue
  Write-Host "  appsettings.json перенесён"
}

# 4) сохранить старый кэш шаблона (utm-dist) → cache\ (чтобы не качать ~150МБ заново)
if ((Test-Path "$Dst\utm-dist") -and -not (Test-Path "$Dst\cache\utm-dist")) {
  New-Item -ItemType Directory -Path "$Dst\cache" -Force | Out-Null
  Copy-Item "$Dst\utm-dist" "$Dst\cache\utm-dist" -Recurse -Force -EA SilentlyContinue
  Write-Host "  кэш шаблона перенесён в cache\"
}
# data\ уже в корне и остаётся там (AppPaths.Root после миграции = $Dst → data = $Dst\data). Привязки целы.

# 5) перенацелить службу на муксер (data/state.json сохранены)
sc.exe config $ServiceName binPath= "`"$dotnet`" `"$svcDll`"" | Out-Null
Write-Host "  служба перенацелена на приватный рантайм"

# 6) автозапуск трея → муксер
$trayCmd = "`"$dotnet`" `"$trayDll`""
New-ItemProperty 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name UtmOrchestratorTray -Value $trayCmd -PropertyType String -Force | Out-Null
try {
  # Интерактивный оператор (а не тот, чьими правами повысились через UAC) — иначе
  # задача автозапуска трея зарегистрируется на чужого пользователя и не сработает.
  $who = (Get-CimInstance Win32_ComputerSystem -EA SilentlyContinue).UserName
  if (-not $who) { $who = "$env:USERDOMAIN\$env:USERNAME" }
  $act = New-ScheduledTaskAction -Execute $dotnet -Argument "`"$trayDll`""
  $trg = New-ScheduledTaskTrigger -AtLogOn -User $who; $trg.Delay = 'PT15S'
  $prn = New-ScheduledTaskPrincipal -UserId $who -LogonType Interactive -RunLevel Limited
  Register-ScheduledTask -TaskName 'UtmOrchestrator-Tray' -Action $act -Trigger $trg -Principal $prn -Force | Out-Null
} catch {}

# 7) обновить DisplayIcon в «Программах и компонентах» (если запись есть)
$unKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\UtmOrchestrator'
if (Test-Path $unKey) { Set-ItemProperty $unKey DisplayIcon "$Dst\bin\app\UtmOrchestrator.Tray.exe" -EA SilentlyContinue }

# 8) старт
Start-Service $ServiceName
Start-Process -FilePath $dotnet -ArgumentList "`"$trayDll`"" -EA SilentlyContinue

Write-Host ""
Write-Host "Миграция выполнена." -ForegroundColor Green

if ($OfferCleanup) {
  # Дать службе/трею подняться и показать состояние панели перед предложением чистки.
  Start-Sleep 6
  try {
    $s = Invoke-RestMethod 'http://127.0.0.1:8090/api/status' -TimeoutSec 20
    Write-Host ("Панель: {0}/{1} работают, версия {2}" -f $s.ok, $s.total, $s.orchestratorVersion) -ForegroundColor Green
  } catch {
    Write-Host "Панель ещё поднимается — откройте http://localhost:8090 через минуту." -ForegroundColor Yellow
  }
  Write-Host ""
  $ans = Read-Host "Удалить старые плоские файлы сейчас? После этого откат будет невозможен. (y = да, Enter = позже)"
  if ($ans -match '^[yYдД]') {
    & $PSCommandPath -Dst $Dst -ServiceName $ServiceName -Cleanup
  } else {
    Write-Host "Старые файлы оставлены. Откат: migrate-to-bin.ps1 -Rollback . Почистить позже: migrate-to-bin.ps1 -Cleanup" -ForegroundColor Cyan
  }
} else {
  Write-Host "Проверьте панель (http://localhost:8090) и статус УТМ." -ForegroundColor Green
  Write-Host "Старые плоские файлы оставлены (для отката). Если всё ок — почистить: migrate-to-bin.ps1 -Cleanup" -ForegroundColor Cyan
  Write-Host "Откат (если что): migrate-to-bin.ps1 -Rollback" -ForegroundColor Cyan
}
