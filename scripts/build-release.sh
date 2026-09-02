#!/usr/bin/env bash
# 本地打包发布：编译 Release 并打出 Jellyfin 可安装的 zip，输出 md5 校验值。
#
# 用法:
#   ./scripts/build-release.sh
# 产物:
#   dist/Jellyfin.Plugin.Amane.zip（含 md5 打印，供 manifest.json 使用）
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "${ROOT}"

dotnet build -c Release

mkdir -p dist
rm -f dist/Jellyfin.Plugin.Amane.zip
(cd bin/Release/net9.0 && zip -j -X "${ROOT}/dist/Jellyfin.Plugin.Amane.zip" Jellyfin.Plugin.Amane.dll)

echo "产物: dist/Jellyfin.Plugin.Amane.zip"
md5 -q dist/Jellyfin.Plugin.Amane.zip | sed 's/^/md5: /'
