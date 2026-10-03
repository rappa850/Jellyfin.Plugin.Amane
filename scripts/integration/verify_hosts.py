"""隔离测试宿主的开放 API 验证；凭据只保存在临时运行目录。"""
import json
import os
from pathlib import Path
import urllib.request
import urllib.error
import urllib.parse
import time

STATE = Path(os.environ.get('LOCALAPPDATA', '/tmp')) / 'Temp/AmaneCompatRuntime'


def request(base, path, token=None, data=None, method=None):
    headers = {'Content-Type': 'application/json', 'Authorization': 'MediaBrowser Client="AmaneCompatibility", Device="Integration", DeviceId="amane-integration", Version="1.0"'}
    if token:
        headers['X-Emby-Token'] = token
        headers['Authorization'] += ', Token="' + token + '"'
    payload = json.dumps(data).encode() if data is not None else None
    req = urllib.request.Request(base + path, payload, headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=60) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode()


def image_status(base, path, token):
    headers = {'Authorization': 'MediaBrowser Client="AmaneCompatibility", Device="Integration", DeviceId="amane-integration", Version="1.0", Token="' + token + '"'}
    try:
        with urllib.request.urlopen(urllib.request.Request(base + path, headers=headers), timeout=30) as response:
            body = response.read()
            return {'status': response.status, 'content_type': response.headers.get('Content-Type'), 'bytes': len(body)}
    except urllib.error.HTTPError as error:
        return {'status': error.code}


def verify(port, session_file, prefix=''):
    session = json.loads((STATE / session_file).read_text(encoding='utf-8-sig'))
    base = f'http://127.0.0.1:{port}' + prefix
    token = session['token']
    request(base, '/Amane/ClearCache', token, {})
    status, health = request(base, '/Amane/Health', token)
    assert status == 200 and health['Reachable'] and health['AuthStatus'] == 'ok', health
    status, listing = request(base, '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=ProviderIds,People,Overview', token)
    assert status == 200
    movie = next(item for item in listing['Items'] if item['ProviderIds'].get('Amane') == 'AMANE-TEST-001')
    movie_id = movie['Id']
    request(base, f'/Items/{movie_id}/Refresh?MetadataRefreshMode=FullRefresh&ImageRefreshMode=FullRefresh&ReplaceAllMetadata=true&ReplaceAllImages=true', token, {})
    time.sleep(2)
    assert movie['ProviderIds']['AmaneId'] == '1'
    assert 'Primary' in movie['ImageTags'] and movie['BackdropImageTags']
    person_id = movie['People'][0]['Id']
    request(base, f'/Items/{person_id}/Refresh?MetadataRefreshMode=FullRefresh&ImageRefreshMode=FullRefresh&ReplaceAllMetadata=true', token, {})
    time.sleep(2)
    for attempt in range(20):
        status, actor = request(base, f'/Users/{session["user_id"]}/Items/{person_id}', token)
        if status == 200 and actor.get('Overview') == 'Amane actor profile fixture':
            break
        time.sleep(0.5)
    assert actor['ProviderIds']['Amane'] == '1' and actor.get('Overview') == 'Amane actor profile fixture', actor
    actor_image = image_status(base, f'/Items/{person_id}/Images/Primary', token)
    assert actor_image['status'] == 200, actor_image
    status, search = request(base, '/Items/RemoteSearch/Movie', token, {'SearchInfo': {'Name': 'AMANE-TEST-001', 'ProviderIds': {'Amane': '1'}}, 'ItemId': movie_id, 'SearchProviderName': 'Amane', 'IncludeDisabledProviders': True})
    assert status == 200 and len(search) == 1
    status, images = request(base, f'/Items/{movie_id}/RemoteImages?ProviderName=Amane', token)
    assert status == 200
    primary = next(image for image in images['Images'] if image['Type'] == 'Primary')
    cached = image_status(base, f'/Items/{movie_id}/Images/Primary', token)
    assert cached['status'] == 200 and cached['content_type'].startswith('image/')
    params = urllib.parse.urlencode({'ImageUrl': primary['Url'], 'Type': 'Primary', 'ProviderName': 'Amane'})
    download_status, _ = request(base, f'/Items/{movie_id}/RemoteImages/Download?' + params, token, {})
    if prefix:
        preview = image_status(base, '/Items/RemoteSearch/Image?' + urllib.parse.urlencode({'ImageUrl': search[0]['ImageUrl'], 'ProviderName': 'Amane'}), token)
    else:
        # Jellyfin Web 的 img 直接请求候选 URL，无宿主 RemoteSearch/Image 路由。
        with urllib.request.urlopen(search[0]['ImageUrl'], timeout=10) as response:
            preview = {'status': response.status, 'content_type': response.headers.get('Content-Type'), 'bytes': len(response.read()), 'mode': 'direct'}
    # Emby 手动下载绕过 Provider 回调，并可能先移除旧图；随后重新自动刷新恢复测试库。
    if download_status >= 400:
        request(base, f'/Items/{movie_id}/Refresh?MetadataRefreshMode=FullRefresh&ImageRefreshMode=FullRefresh&ReplaceAllImages=true', token, {})
    status, cache = request(base, '/Amane/ClearCache', token, {})
    assert status == 200 and isinstance(cache['Cleared'], int)
    return {'port': port, 'health': health, 'movie': {k: movie.get(k) for k in ['Name', 'ProviderIds', 'Overview', 'ImageTags', 'BackdropImageTags']}, 'actor': {k: actor.get(k) for k in ['Name', 'ProviderIds', 'Overview', 'PremiereDate', 'ImageTags']}, 'actor_image': actor_image, 'remote_image_count': len(images['Images']), 'manual_download_status': download_status, 'identify_preview': preview, 'cached_primary': cached, 'clear_cache': cache}


if __name__ == '__main__':
    results = [verify(18096, 'emby-session.json', '/emby'), verify(18097, 'jellyfin-18097-session.json'), verify(18098, 'jellyfin-18098-session.json')]
    output = Path('docs/verification/host-observations.json')
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(results, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    for result in results:
        print(result['port'], 'metadata/actor/cache OK', 'manual', result['manual_download_status'], 'preview', result['identify_preview']['status'], 'cached', result['cached_primary']['status'])
