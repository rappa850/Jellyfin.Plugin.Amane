#!/usr/bin/env bash
# Amane 元数据 API 调用探针：把真实返回 JSON 保存到 Amane/amane-response.sample.json，
# 用于校验插件 DTO 字段映射。
#
# 用法:
#   AMANE_TOKEN=xxx ./scripts/probe-amane.sh [查询词]
# 可选环境变量:
#   AMANE_URL   服务地址，默认 http://127.0.0.1:18000
set -euo pipefail

AMANE_URL="${AMANE_URL:-http://127.0.0.1:18000}"
QUERY="${1:-IPZZ-822}"
OUT="$(cd "$(dirname "$0")/.." && pwd)/Amane/amane-response.sample.json"

if [[ -z "${AMANE_TOKEN:-}" ]]; then
  echo "缺少 AMANE_TOKEN 环境变量" >&2
  exit 1
fi

echo "GET ${AMANE_URL}/api/metadata?search=${QUERY}&limit=1"
curl -sS -m 15 --get \
  -H "Authorization: Bearer ${AMANE_TOKEN}" \
  --data-urlencode "search=${QUERY}" \
  --data-urlencode "limit=1" \
  "${AMANE_URL}/api/metadata" \
  -o "${OUT}" -w 'HTTP %{http_code}\n'

python3 -m json.tool --no-ensure-ascii "${OUT}" > /dev/null
echo "已保存到 ${OUT}"

# 演员接口采样
ACTOR_QUERY="${ACTOR_QUERY:-林芽依}"
ACTOR_OUT="$(dirname "${OUT}")/amane-actor.sample.json"

echo "GET ${AMANE_URL}/api/actors?search=${ACTOR_QUERY}&limit=1"
curl -sS -m 15 --get \
  -H "Authorization: Bearer ${AMANE_TOKEN}" \
  --data-urlencode "search=${ACTOR_QUERY}" \
  --data-urlencode "limit=1" \
  "${AMANE_URL}/api/actors" \
  -o "${ACTOR_OUT}" -w 'HTTP %{http_code}\n'

python3 -m json.tool --no-ensure-ascii "${ACTOR_OUT}" > /dev/null
echo "已保存到 ${ACTOR_OUT}"

# 详情接口采样：取搜索结果首条的内部 id 直取（对应 "Amane 电影 Id" 绑定）
DETAIL_OUT="$(dirname "${OUT}")/amane-detail.sample.json"
DETAIL_ID=$(python3 -c "import json; d=json.load(open('${OUT}')); print(d['items'][0]['id'] if d['items'] else '')")

if [[ -n "${DETAIL_ID}" ]]; then
  echo "GET ${AMANE_URL}/api/metadata/${DETAIL_ID}"
  curl -sS -m 15 \
    -H "Authorization: Bearer ${AMANE_TOKEN}" \
    "${AMANE_URL}/api/metadata/${DETAIL_ID}" \
    -o "${DETAIL_OUT}" -w 'HTTP %{http_code}\n'
  python3 -m json.tool --no-ensure-ascii "${DETAIL_OUT}" > /dev/null
  echo "已保存到 ${DETAIL_OUT}"
fi

# T3 实时契约断言：字段缺失/类型漂移时非零退出
python3 - "${OUT}" "${ACTOR_OUT}" "${DETAIL_OUT}" <<'PY'
import json, sys, os

meta = json.load(open(sys.argv[1]))
actor = json.load(open(sys.argv[2]))

errors = []

def check(cond, msg):
    if not cond:
        errors.append(msg)

check(isinstance(meta.get('items'), list) and isinstance(meta.get('total'), int),
      'metadata 响应缺少 items/total')
if meta['items']:
    m = meta['items']
    m = m[0]
    for key, typ in [('number', str), ('title', str), ('plot', str), ('release', str),
                     ('studio', str), ('runtime', int), ('poster_url', str), ('thumb_url', str)]:
        check(m.get(key) is None or isinstance(m.get(key), typ),
              f'metadata.{key} 类型异常: {type(m.get(key)).__name__}')
    for key in ['actors', 'directors', 'tags', 'extrafanart']:
        check(isinstance(m.get(key), list), f'metadata.{key} 应为数组')
        if isinstance(m.get(key), list):
            check(all(isinstance(x, str) for x in m[key]), f'metadata.{key} 元素应为字符串')

check(isinstance(actor.get('items'), list), 'actors 响应缺少 items')
if actor['items']:
    a = actor['items'][0]
    check(isinstance(a.get('name'), str), 'actor.name 类型异常')
    check(isinstance(a.get('id'), int), 'actor.id 类型异常')
    check(isinstance(a.get('image_urls'), list), 'actor.image_urls 应为数组')

# 详情接口断言（若已采样）
detail_path = sys.argv[3]
if os.path.exists(detail_path) and os.path.getsize(detail_path) > 0:
    detail = json.load(open(detail_path))
    check(isinstance(detail.get('metadata'), dict), 'detail 响应缺少 metadata 对象')
    if isinstance(detail.get('metadata'), dict):
        check(isinstance(detail['metadata'].get('number'), str), 'detail.metadata.number 类型异常')
        check(isinstance(detail['metadata'].get('id'), int), 'detail.metadata.id 类型异常')

if errors:
    print('契约断言失败:')
    for e in errors:
        print(' -', e)
    sys.exit(1)
print('契约断言全部通过')
PY
