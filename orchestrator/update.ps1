# Self-update of UTM:Orchestrator. Launched DETACHED by the running service on
# "apply update": stops the service (its own parent) + tray, replaces files from the
# freshly downloaded payload ($Src), restarts. Survives the service stop because it is
# a separate process. ASCII-only on purpose (runs headless via powershell -File).
param([string]$Src = $PSScriptRoot, [string]$Dst = 'C:\UtmOrchestrator')
$ErrorActionPreference = 'SilentlyContinue'

Stop-Service UtmOrchestrator -Force
Get-Process UtmOrchestrator.Tray -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 3

# Copy new files; keep data\, utm\ (our UTM folders) and appsettings.json; don't copy *.ps1 helpers.
# robocopy without /PURGE never deletes dest-only dirs, so utm\ survives anyway; /XD is explicit safety.
robocopy $Src $Dst /E /XD "$Dst\data" "$Dst\utm" /XF appsettings.json install.ps1 uninstall.ps1 update.ps1 /NFL /NDL /NJH /NJS /NC /NS /R:2 /W:1 | Out-Null

Start-Service UtmOrchestrator
# Relaunch tray in the operator's interactive session (task principal = interactive user).
schtasks /run /tn "UtmOrchestrator-Tray" 2>$null | Out-Null
