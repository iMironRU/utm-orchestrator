#Requires -RunAsAdministrator
<#
  Установка УТМ:Оркестратор (раскладка bin/data/utms/cache/transfer).
  Запускать ОТ АДМИНИСТРАТОРА из папки пейлоада (где лежат bin\ + *.ps1):
    powershell -ExecutionPolicy Bypass -File install.ps1
  data\ / utms\ / cache\ / transfer\ и appsettings.json при повторной установке НЕ затираются.
  Запуск через приватный рантайм: bin\runtime\dotnet.exe bin\app\<exe>.dll (машинный .NET не нужен).
#>
param([string]$Dst = 'C:\UtmOrchestrator')
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$dotnet  = "$Dst\bin\runtime\dotnet.exe"
$svcDll  = "$Dst\bin\app\UtmOrchestrator.Service.dll"
$trayDll = "$Dst\bin\app\UtmOrchestrator.Tray.dll"
$trayExe = "$Dst\bin\app\UtmOrchestrator.Tray.exe"   # apphost (GUI, без консоли)
$rt      = "$Dst\bin\runtime"

function Stop-Tray {
  # и apphost (UtmOrchestrator.Tray.exe), и старый муксер-вариант (dotnet.exe Tray.dll)
  Get-Process UtmOrchestrator.Tray -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
  Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -EA SilentlyContinue |
    Where-Object { $_.CommandLine -like '*UtmOrchestrator.Tray.dll*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue }
}

Write-Host "Установка УТМ:Оркестратор → $Dst" -ForegroundColor Cyan

# 1) остановить, если уже стоит
$svc = Get-Service UtmOrchestrator -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service UtmOrchestrator -Force }
Stop-Tray
Start-Sleep 2

# 2) скопировать пейлоад (bin\ + runtime.key + uninstall.ps1); данные не трогаем
New-Item -ItemType Directory -Path $Dst -Force | Out-Null
robocopy "$src\bin" "$Dst\bin" /E /NFL /NDL /NJH /NJS /NC /NS /R:1 /W:1 | Out-Null
Copy-Item "$src\runtime.key"   "$Dst\runtime.key"   -Force -ErrorAction SilentlyContinue
Copy-Item "$src\uninstall.ps1" "$Dst\uninstall.ps1" -Force -ErrorAction SilentlyContinue
Copy-Item "$src\update.ps1"    "$Dst\update.ps1"    -Force -ErrorAction SilentlyContinue
Write-Host "  файлы скопированы (bin\app + bin\runtime)"

# 3) служба через муксер (Automatic + авто-рестарт при сбое)
$binPath = "`"$dotnet`" `"$svcDll`""
if (-not (Get-Service UtmOrchestrator -ErrorAction SilentlyContinue)) {
  New-Service -Name UtmOrchestrator -BinaryPathName $binPath `
    -DisplayName 'УТМ:Оркестратор' -StartupType Automatic `
    -Description 'Панель управления и авто-подъём УТМ ЕГАИС.' | Out-Null
  sc.exe failure UtmOrchestrator reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
  Write-Host "  служба UtmOrchestrator зарегистрирована (через приватный рантайм)"
} else {
  # обновим путь запуска на случай смены раскладки
  sc.exe config UtmOrchestrator binPath= $binPath | Out-Null
  Write-Host "  служба уже есть — путь запуска обновлён"
}

# 4) SCardSvr — Automatic (нужен тёплым для работы с токенами)
Set-Service SCardSvr -StartupType Automatic
Start-Service SCardSvr -ErrorAction SilentlyContinue

# 5) трей в автозагрузку (Run-ключ + задача «при входе») — apphost Tray.exe, БЕЗ консоли.
#    Муксер `dotnet.exe Tray.dll` показывал бы чёрное окно консоли. apphost находит
#    приватный рантайм через DOTNET_ROOT (ставим пользователю, чтобы автозапуск его видел).
[Environment]::SetEnvironmentVariable('DOTNET_ROOT', $rt, 'User')
$env:DOTNET_ROOT = $rt
New-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' `
  -Name UtmOrchestratorTray -Value "`"$trayExe`"" -PropertyType String -Force | Out-Null
$who = (Get-CimInstance Win32_ComputerSystem -EA SilentlyContinue).UserName
if (-not $who) { $who = "$env:USERDOMAIN\$env:USERNAME" }
$act = New-ScheduledTaskAction -Execute $trayExe
$trg = New-ScheduledTaskTrigger -AtLogOn -User $who
$trg.Delay = 'PT15S'
$prn = New-ScheduledTaskPrincipal -UserId $who -LogonType Interactive -RunLevel Limited
$set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 2 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName 'UtmOrchestrator-Tray' -Action $act -Trigger $trg `
  -Principal $prn -Settings $set -Force | Out-Null
Write-Host "  трей в автозагрузке (Run-ключ + задача «при входе»)"

# 6) регистрация в «Установка и удаление программ»
$ver = (Get-Item $svcDll -EA SilentlyContinue).VersionInfo.FileVersion
if (-not $ver) { $ver = '0.0.0' }
$unKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\UtmOrchestrator'
$unCmd = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$Dst\uninstall.ps1`" -Purge"
New-Item -Path $unKey -Force | Out-Null
Set-ItemProperty $unKey DisplayName 'УТМ:Оркестратор'
Set-ItemProperty $unKey DisplayVersion $ver
Set-ItemProperty $unKey Publisher 'iMironRU'
Set-ItemProperty $unKey InstallLocation $Dst
Set-ItemProperty $unKey DisplayIcon "$Dst\bin\app\UtmOrchestrator.Tray.exe"
Set-ItemProperty $unKey UninstallString $unCmd
Set-ItemProperty $unKey QuietUninstallString $unCmd
Set-ItemProperty $unKey NoModify 1 -Type DWord
Set-ItemProperty $unKey NoRepair 1 -Type DWord
Write-Host "  зарегистрирован в «Установка и удаление программ»"

# 7) запустить
Start-Service UtmOrchestrator
Start-Process -FilePath $trayExe   # apphost, без консоли ($env:DOTNET_ROOT уже задан)

Write-Host ""
Write-Host "Готово. Откройте панель: http://localhost:8090" -ForegroundColor Green
