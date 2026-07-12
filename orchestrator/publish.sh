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
FULL="$OUT/_full"      # self-contained (наш код + рантайм) — источник файлов
FDREF="$OUT/_fdref"    # framework-dependent — эталон «что относится к нашему коду»
APP="$OUT/app"
RT="$OUT/runtime"
rm -rf "$OUT"; mkdir -p "$FULL" "$FDREF" "$APP" "$RT"

VERARG=()
if [ -n "${VERSION:-}" ]; then VERARG+=(-p:Version="$VERSION"); fi

PROJECTS=(Service Tray Cli)

echo "=== self-contained (общий рантайм, без single-file) → $FULL ==="
for proj in "${PROJECTS[@]}"; do
  "$DOTNET" publish "src/UtmOrchestrator.$proj/UtmOrchestrator.$proj.csproj" \
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false \
    "${VERARG[@]}" -o "$FULL" -v q
done

echo "=== framework-dependent (эталон классификации) → $FDREF ==="
for proj in "${PROJECTS[@]}"; do
  "$DOTNET" publish "src/UtmOrchestrator.$proj/UtmOrchestrator.$proj.csproj" \
    -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false \
    "${VERARG[@]}" -o "$FDREF" -v q
done

echo "=== раскладываю full → app/ + runtime/ ==="
# Файл относится к app, если такой же путь есть во framework-dependent сборке
# (наш код, host-exe, *.deps.json, *.runtimeconfig.json, NuGet-зависимости, wwwroot).
# Всё остальное из full — файлы рантайма.
( cd "$FULL" && find . -type f -print0 ) | while IFS= read -r -d '' f; do
  rel="${f#./}"
  if [ -e "$FDREF/$rel" ]; then dest="$APP/$rel"; else dest="$RT/$rel"; fi
  mkdir -p "$(dirname "$dest")"
  cp "$FULL/$rel" "$dest"
done

# Скрипты установки/обновления — в app
cp install.ps1 uninstall.ps1 update.ps1 "$APP/" 2>/dev/null || true

# Ключ рантайма = хэш списка файлов рантайма (путь:размер). Меняется только при
# смене состава/версии рантайма. Кладём и в app (runtime.key — что требуется), и в dist.
KEY=$( ( cd "$RT" && find . -type f -printf '%P:%s\n' | LC_ALL=C sort | sha256sum ) | cut -c1-12 )
echo "$KEY" > "$APP/runtime.key"
echo "$KEY" > "$OUT/runtime-key.txt"

# Чистим временные
rm -rf "$FULL" "$FDREF"

echo "=== готово ==="
echo "runtime key: $KEY"
echo "app    : $(du -sh "$APP" | cut -f1) ($(find "$APP" -type f | wc -l) файлов)"
echo "runtime: $(du -sh "$RT"  | cut -f1) ($(find "$RT"  -type f | wc -l) файлов)"
