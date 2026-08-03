#Requires -RunAsAdministrator
<#
  Удаление УТМ:Оркестратор. От администратора:
    powershell -ExecutionPolicy Bypass -File uninstall.ps1          # оставит папку/данные
    powershell -ExecutionPolicy Bypass -File uninstall.ps1 -Purge   # снесёт папку целиком
  ВАЖНО: сами УТМ (службы Transport*) НЕ трогаются — оркестратор ими только управляет.
#>
param([string]$Dst = 'C:\UtmOrchestrator', [switch]$Purge)
$ErrorActionPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

Write-Host "Удаление УТМ:Оркестратор" -ForegroundColor Cyan

# служба
if (Get-Service UtmOrchestrator -ErrorAction SilentlyContinue) {
  Stop-Service UtmOrchestrator -Force
  sc.exe delete UtmOrchestrator | Out-Null
  Write-Host "  служба удалена"
}
# трей: и старый exe, и новый запуск через муксер (dotnet.exe с Tray.dll)
Get-Process UtmOrchestrator.Tray -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -EA SilentlyContinue |
  Where-Object { $_.CommandLine -like '*UtmOrchestrator.Tray.dll*' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue }
Remove-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name UtmOrchestratorTray -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName 'UtmOrchestrator-Tray' -Confirm:$false -ErrorAction SilentlyContinue
# убрать DOTNET_ROOT пользователя, если он указывал на наш приватный рантайм
$dr = [Environment]::GetEnvironmentVariable('DOTNET_ROOT','User')
if ($dr -and $dr -like "$Dst*") { [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $null, 'User') }
Write-Host "  трей убран из автозагрузки"

# из «Установка и удаление программ»
Remove-Item 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\UtmOrchestrator' -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "  запись в «Программах и компонентах» удалена"

if ($Purge) {
  Remove-Item $Dst -Recurse -Force
  Write-Host "  папка $Dst удалена (данные тоже)" -ForegroundColor Yellow
} else {
  Write-Host "  файлы/данные в $Dst оставлены (для полного удаления: -Purge)"
}
Write-Host "Готово. Службы УТМ (Transport*) не тронуты." -ForegroundColor Green
