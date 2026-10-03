#!/usr/bin/env bash
# 白名单打包：每个平台只分发一个插件 DLL，不包含宿主 SDK。
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
platform="${1:-all}"
mkdir -p dist
package() {
  local project="$1" output="$2" dll="$3" archive="$4"
  dotnet build "$project" -c Release
  test -f "$output/$dll"
  local files=("$dll")
  rm -f "dist/$archive"
  (cd "$output" && zip -j -X "$ROOT/dist/$archive" "${files[@]}")
  python3 - "dist/$archive" "$dll" <<'PY'
import hashlib, sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as z:
    expected = [sys.argv[2]]
    assert sorted(z.namelist()) == sorted(expected), z.namelist()
digest = hashlib.md5(open(sys.argv[1], 'rb').read()).hexdigest()
open(sys.argv[1] + '.md5', 'w').write(digest + '\n')
print(sys.argv[1], 'md5=' + digest)
PY
}
case "$platform" in jellyfin10|jellyfin12|emby|all) ;; *) echo '用法: build-release.sh [jellyfin10|jellyfin12|emby|all]' >&2; exit 1;; esac
if [[ "$platform" == jellyfin10 || "$platform" == all ]]; then
  package src/Amane.Jellyfin/Jellyfin.Plugin.Amane.csproj src/Amane.Jellyfin/bin/Release/net9.0 Jellyfin.Plugin.Amane.dll Jellyfin.Plugin.Amane.zip
fi
if [[ "$platform" == jellyfin12 || "$platform" == all ]]; then
  package src/Amane.Jellyfin12/Amane.Jellyfin12.csproj src/Amane.Jellyfin12/bin/Release/net10.0 Jellyfin.Plugin.Amane.dll Jellyfin.Plugin.Amane.12.zip
fi
if [[ "$platform" == emby || "$platform" == all ]]; then
  package src/Amane.Emby/Amane.Emby.csproj src/Amane.Emby/bin/Release/net8.0 Emby.Plugin.Amane.dll Emby.Plugin.Amane.zip
fi
