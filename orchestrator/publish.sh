#!/usr/bin/env bash
# Собирает оркестратор self-contained (рантайм зашит, .NET на машине НЕ нужен), но
# раскладывает результат на ДВА набора:
#   dist/app/      — наш код + wwwroot + скрипты + runtime.key (~5 МБ, меняется каждый релиз)
#   dist/runtime/  — общие файлы .NET-рантайма (~65 МБ, меняются только при апгрейде .NET)
# Самообновление качает app; рантайм — только если ключ (dist/runtime-key.txt) сменился.
# Класс файлов определяем эталонной framework-dependent сборкой: что в неё попало — «наш
# код» (app), чего в ней нет, а в self-contained есть — рантайм.
# Использование:  [VERSION=0.1.N] ./publish.sh
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
APP="$OUT/app"        # bin/app/* (наш код) + скрипты + runtime.key  (~несколько МБ, каждый релиз)
RT="$OUT/runtime"     # bin/runtime/* (приватный .NET)               (~70 МБ, меняется при апгрейде .NET)
rm -rf "$OUT"; mkdir -p "$APP/bin/app" "$RT/bin/runtime"

VERARG=()
if [ -n "${VERSION:-}" ]; then VERARG+=(-p:Version="$VERSION"); fi

PROJECTS=(Service Tray Cli)

echo "=== наш код (framework-dependent, без рантайма) → $APP/bin/app ==="
for proj in "${PROJECTS[@]}"; do
  "$DOTNET" publish "src/UtmOrchestrator.$proj/UtmOrchestrator.$proj.csproj" \
    -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false \
    "${VERARG[@]}" -o "$APP/bin/app" -v q
done

echo "=== чистка мусора публикации (локализации/pdb/web.config) ==="
for loc in cs de es fr it ja ko pl pt-BR ru tr zh-Hans zh-Hant; do rm -rf "$APP/bin/app/$loc"; done
find "$APP/bin/app" -maxdepth 1 -name '*.pdb' -delete
rm -f "$APP/bin/app/web.config"

echo "=== приватный .NET-рантайм (dotnet + host + shared, только net8.0) → $RT/bin/runtime ==="
if [ -n "${DOTNET_ROOT:-}" ]; then DOTNET_DIR="$DOTNET_ROOT"; else DOTNET_DIR=$(dirname "$DOTNET"); fi
RTB="$RT/bin/runtime"
cp "$DOTNET_DIR/dotnet.exe" "$RTB/"
cp -r "$DOTNET_DIR/host" "$RTB/"
mkdir -p "$RTB/shared"
# Кладём ТОЛЬКО целевой мажор net8.0: последнюю 8.0.x каждого фреймворка.
#  - WindowsDesktop.App обязателен: трей на WinForms без него падает (No frameworks were found).
#  - На CI-раннере в shared лежат 8/9/10 — без фильтра рантайм раздувается в разы (~400 МБ).
TARGET_MAJOR="8.0"
for fw in Microsoft.NETCore.App Microsoft.AspNetCore.App Microsoft.WindowsDesktop.App; do
  srcfw="$DOTNET_DIR/shared/$fw"
  if [ ! -d "$srcfw" ]; then echo "ОШИБКА: нет $fw в $DOTNET_DIR/shared"; exit 1; fi
  ver=$(ls -1 "$srcfw" | grep -E "^${TARGET_MAJOR}\.[0-9]+$" | sort -V | tail -1)
  if [ -z "$ver" ]; then echo "ОШИБКА: нет ${TARGET_MAJOR}.x для $fw (есть: $(ls -1 "$srcfw" | tr '\n' ' '))"; exit 1; fi
  mkdir -p "$RTB/shared/$fw"
  cp -r "$srcfw/$ver" "$RTB/shared/$fw/"
  echo "  $fw → $ver"
done

# Скрипты установки/обновления/миграции + однокликовый .cmd — в КОРЕНЬ app-пейлоада.
cp install.ps1 uninstall.ps1 update.ps1 migrate-to-bin.ps1 update-machine.ps1 UtmOrchestrator-Migrate.cmd "$APP/" 2>/dev/null || true

# innoextract — рядом с нашим кодом (bin/app/tools).
if [ -d tools ]; then mkdir -p "$APP/bin/app/tools"; cp -r tools/. "$APP/bin/app/tools/"; fi

# Ключ рантайма = хэш списка файлов рантайма (путь:размер). Меняется только при апгрейде .NET.
KEY=$( ( cd "$RT" && find . -type f -printf '%P:%s\n' | LC_ALL=C sort | sha256sum ) | cut -c1-12 )
echo "$KEY" > "$APP/runtime.key"
echo "$KEY" > "$OUT/runtime-key.txt"

echo "=== готово ==="
echo "runtime key: $KEY"
echo "app    : $(du -sh "$APP" | cut -f1) ($(find "$APP" -type f | wc -l) файлов)"
echo "runtime: $(du -sh "$RT"  | cut -f1) ($(find "$RT"  -type f | wc -l) файлов)"
