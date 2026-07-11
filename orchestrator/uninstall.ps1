#Requires -RunAsAdministrator
<#
  Удаление УТМ:Оркестратор. От администратора:
    powershell -ExecutionPolicy Bypass -File uninstall.ps1
  По умолчанию папку C:\UtmOrchestrator НЕ удаляет (данные сохраняются).
  Для полного удаления: uninstall.ps1 -Purge
  ВАЖНО: сами УТМ (службы Transport*) НЕ трогаются — оркестратор ими только управляет.
#>
param([string]$Dst = 'C:\UtmOrchestrator', [switch]$Purge)
$ErrorActionPreference = 'SilentlyContinue'

Write-Host "Удаление УТМ:Оркестратор" -ForegroundColor Cyan

# служба
if (Get-Service UtmOrchestrator -ErrorAction SilentlyContinue) {
  Stop-Service UtmOrchestrator -Force
  sc.exe delete UtmOrchestrator | Out-Null
  Write-Host "  служба удалена"
}
# трей
Get-Process UtmOrchestrator.Tray -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name UtmOrchestratorTray -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName 'UtmOrchestrator-Tray' -Confirm:$false -ErrorAction SilentlyContinue
Write-Host "  трей убран из автозагрузки (Run-ключ + задача)"

if ($Purge) {
  Remove-Item $Dst -Recurse -Force
  Write-Host "  папка $Dst удалена (данные тоже)" -ForegroundColor Yellow
} else {
  Write-Host "  файлы/данные в $Dst оставлены (для полного удаления: -Purge)"
}
Write-Host "Готово. Службы УТМ (Transport*) не тронуты." -ForegroundColor Green
