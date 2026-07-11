#!/usr/bin/env bash
# Собирает все exe оркестратора САМОДОСТАТОЧНЫМИ single-file (рантайм зашит,
# никаких установок .NET не требуется) в папку dist/.
# Использование:  ./publish.sh
set -euo pipefail

# Найти dotnet: в PATH, иначе портативный в C:\dev-tools\dotnet
if command -v dotnet >/dev/null 2>&1; then
  DOTNET=dotnet
elif [ -x "/c/dev-tools/dotnet/dotnet.exe" ]; then
  export DOTNET_ROOT=/c/dev-tools/dotnet
  export PATH="/c/dev-tools/dotnet:$PATH"
  DOTNET=/c/dev-tools/dotnet/dotnet.exe
else
  echo "dotnet не найден (ни в PATH, ни в C:\\dev-tools\\dotnet)"; exit 1
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

cd "$(dirname "$0")"
OUT="dist"
rm -rf "$OUT"; mkdir -p "$OUT"

COMMON=(-c Release -r win-x64 --self-contained true
        -p:PublishSingleFile=true
        -p:IncludeNativeLibrariesForSelfExtract=true
        -p:EnableCompressionInSingleFile=true
        -o "$OUT")

for proj in Tray Service Cli; do
  echo "=== publish UtmOrchestrator.$proj ==="
  "$DOTNET" publish "src/UtmOrchestrator.$proj/UtmOrchestrator.$proj.csproj" "${COMMON[@]}"
done

# Скрипты установки/удаления — рядом с exe в dist (для развёртывания на компе)
cp install.ps1 uninstall.ps1 "$OUT/" 2>/dev/null || true

echo "=== готово, dist/ ==="
ls -lah "$OUT" | grep -iE '\.exe|\.ps1' || true
