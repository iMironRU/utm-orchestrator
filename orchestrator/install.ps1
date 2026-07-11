#Requires -RunAsAdministrator
<#
  Установка УТМ:Оркестратор на компьютер.
  Запускать ОТ АДМИНИСТРАТОРА из папки, где лежат exe + wwwroot (содержимое dist/):
    powershell -ExecutionPolicy Bypass -File install.ps1
  Данные (data\) и appsettings.json при повторной установке НЕ затираются.
#>
param([string]$Dst = 'C:\UtmOrchestrator')
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
# Вывод в UTF-8, чтобы кириллица в логе установщика (Setup.exe читает stdout как UTF-8)
# не превращалась в кракозябры.
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

Write-Host "Установка УТМ:Оркестратор → $Dst" -ForegroundColor Cyan

# 1) остановить, если уже стоит
$svc = Get-Service UtmOrchestrator -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service UtmOrchestrator -Force }
Get-Process UtmOrchestrator.Tray -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2

# 2) скопировать файлы (data и appsettings.json не трогаем)
New-Item -ItemType Directory -Path $Dst -Force | Out-Null
robocopy $src $Dst /E /XD "$Dst\data" /XF appsettings.json install.ps1 uninstall.ps1 /NFL /NDL /NJH /NJS /NC /NS /R:1 /W:1 | Out-Null
Write-Host "  файлы скопированы"

# 3) служба (Automatic + авто-рестарт при сбое)
if (-not (Get-Service UtmOrchestrator -ErrorAction SilentlyContinue)) {
  New-Service -Name UtmOrchestrator -BinaryPathName "`"$Dst\UtmOrchestrator.Service.exe`"" `
    -DisplayName 'УТМ:Оркестратор' -StartupType Automatic `
    -Description 'Панель управления и авто-подъём УТМ ЕГАИС.' | Out-Null
  sc.exe failure UtmOrchestrator reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
  Write-Host "  служба UtmOrchestrator зарегистрирована (Automatic)"
} else { Write-Host "  служба уже есть" }

# 4) SCardSvr — Automatic (нужен тёплым для работы с токенами)
Set-Service SCardSvr -StartupType Automatic
Start-Service SCardSvr -ErrorAction SilentlyContinue

# 5) трей в автозагрузку текущего пользователя — ДВА механизма для надёжности
#    (Run-ключ иногда «не срабатывает» после перезагрузки; трей single-instance,
#     поэтому оба одновременно безопасны — задвоения не будет).
$trayExe = "$Dst\UtmOrchestrator.Tray.exe"
# 5a) Run-ключ (быстрый путь)
New-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' `
  -Name UtmOrchestratorTray -Value "`"$trayExe`"" -PropertyType String -Force | Out-Null
# 5b) задача планировщика «при входе» + задержка 15с (надёжный путь)
$who = "$env:USERDOMAIN\$env:USERNAME"
$act = New-ScheduledTaskAction -Execute $trayExe
$trg = New-ScheduledTaskTrigger -AtLogOn -User $who
$trg.Delay = 'PT15S'
$prn = New-ScheduledTaskPrincipal -UserId $who -LogonType Interactive -RunLevel Limited
$set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 2 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName 'UtmOrchestrator-Tray' -Action $act -Trigger $trg `
  -Principal $prn -Settings $set -Force | Out-Null
Write-Host "  трей в автозагрузке (Run-ключ + задача «при входе»)"

# 6) запустить
Start-Service UtmOrchestrator
Start-Process "$Dst\UtmOrchestrator.Tray.exe"

Write-Host ""
Write-Host "Готово. Откройте панель: http://localhost:8090" -ForegroundColor Green
Write-Host "Первый запуск: раздел «Установка» → «Сканировать токены» → «Подхватить существующие УТМ»."
