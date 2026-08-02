# Самообновление УТМ:Оркестратор (раскладка bin/data/utms/cache/transfer).
# Запускается DETACHED работающей службой на «применить обновление»: останавливает службу
# (своего родителя) + трей, копирует новый bin\ из скачанного пейлоада ($Src), стартует.
# Переживает остановку службы, т.к. отдельный процесс. ASCII-only (headless powershell -File).
param([string]$Src = $PSScriptRoot, [string]$Dst = 'C:\UtmOrchestrator')
$ErrorActionPreference = 'SilentlyContinue'

# --- Защита ПЛОСКИХ установок (старая раскладка exe в корне) ---
# Новый пейлоад — bin-формат (framework-dependent + приватный рантайм). Авто-миграция
# плоской установки в bin рискованна, поэтому НЕ делаем её на лету: оставляем старую
# версию работать. Переход на bin делается осознанно новым Setup.exe / migrate-to-bin.ps1.
if ((Test-Path "$Dst\UtmOrchestrator.Service.exe") -and -not (Test-Path "$Dst\bin\app")) {
  # Плоская установка: тихо выходим, служба продолжает работать на прежней версии.
  return
}

$dotnet  = "$Dst\bin\runtime\dotnet.exe"
$svcDll  = "$Dst\bin\app\UtmOrchestrator.Service.dll"
$trayDll = "$Dst\bin\app\UtmOrchestrator.Tray.dll"

Stop-Service UtmOrchestrator -Force
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
  Where-Object { $_.CommandLine -like '*UtmOrchestrator.Tray.dll*' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Start-Sleep 3

# Копируем новый bin\ (app всегда; runtime — только если был в пейлоаде). data\/utms\/cache\/
# transfer\ в КОРНЕ не трогаем. appsettings.json (правки пользователя) сохраняем.
robocopy "$Src\bin" "$Dst\bin" /E /XF appsettings.json /NFL /NDL /NJH /NJS /NC /NS /R:2 /W:1 | Out-Null
Copy-Item "$Src\runtime.key" "$Dst\runtime.key" -Force
Copy-Item "$Src\uninstall.ps1" "$Dst\uninstall.ps1" -Force

# Обновим путь запуска службы (на случай смены раскладки рантайма) и DisplayVersion.
sc.exe config UtmOrchestrator binPath= "`"$dotnet`" `"$svcDll`"" | Out-Null
$ver = (Get-Item $svcDll).VersionInfo.FileVersion
if ($ver) { Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\UtmOrchestrator' DisplayVersion $ver -EA SilentlyContinue }

Start-Service UtmOrchestrator
# Перезапустить трей в интерактивной сессии оператора (задача от интерактивного пользователя).
schtasks /run /tn "UtmOrchestrator-Tray" 2>$null | Out-Null
