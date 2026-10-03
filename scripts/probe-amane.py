"""开放 API 实时探针；适用于 Windows/WSL。合成数据和真实库采样分别保存。"""
import argparse
import json
import os
from pathlib import Path
import urllib.parse
import urllib.request


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('query', nargs='?', default='AMANE-TEST-001')
    parser.add_argument('--actor', default='Amane Test Actor')
    parser.add_argument('--output', type=Path, default=Path('Amane/current'))
    args = parser.parse_args()
    base = os.environ.get('AMANE_URL', 'http://127.0.0.1:18000').rstrip('/')
    token = os.environ.get('AMANE_TOKEN', '')
    headers = {'Authorization': 'Bearer ' + token} if token else {}
    args.output.mkdir(parents=True, exist_ok=True)

    def get(path, name):
        with urllib.request.urlopen(urllib.request.Request(base + path, headers=headers), timeout=30) as response:
            assert response.status == 200
            payload = json.load(response)
        (args.output / (name + '.sample.json')).write_text(json.dumps(payload, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        print(name, 'captured')
        return payload

    health = get('/api/health', 'health')
    assert health['status'] == 'ok' and isinstance(health['version'], str)
    schema = get('/openapi.json', 'openapi')
    assert '/api/metadata' in schema['paths'] and '/api/resources/proxy' in schema['paths']
    metadata = get('/api/metadata?' + urllib.parse.urlencode({'search': args.query, 'limit': 1}), 'metadata')
    actors = get('/api/actors?' + urllib.parse.urlencode({'search': args.actor, 'limit': 1}), 'actors')
    assert metadata['items'], '没有影片样本，不能证明契约通过'
    assert actors['items'], '没有演员样本，不能证明契约通过'
    item = metadata['items'][0]
    for key in ('actors', 'directors', 'tags', 'extrafanart'):
        assert isinstance(item[key], list) and all(isinstance(value, str) for value in item[key]), key
    for key in ('poster_urls', 'thumb_urls'):
        if key in item:
            assert isinstance(item[key], list) and all(isinstance(value, str) for value in item[key]), key
    detail = get('/api/metadata/' + str(item['id']), 'detail')
    assert detail['metadata']['id'] == item['id']
    actor = get('/api/actors/' + str(actors['items'][0]['id']), 'actor-detail')
    assert actor['id'] == actors['items'][0]['id'] and 'name' in actor
    poster = item.get('poster_url')
    if poster:
        url = base + poster if poster.startswith('/') else base + '/api/resources/proxy?' + urllib.parse.urlencode({'url': poster})
        with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=60) as response:
            assert response.status == 200 and response.headers.get_content_type().startswith('image/')
        print('image proxy passed')
    print('开放 API 契约断言全部通过; Amane', health['version'])


if __name__ == '__main__':
    main()
