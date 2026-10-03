define([], function () {
    return function (view) {
            const pluginId = 'e4c1a8a9-023d-4a7a-978b-eec21d66c059';
            const page = view;

            page.addEventListener('viewshow', function () {
                Dashboard.showLoadingMsg();
                ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                    page.querySelector('#ServerUrl').value = config.ServerUrl || 'http://127.0.0.1:18000';
                    page.querySelector('#ApiToken').value = config.ApiToken || '';
                    page.querySelector('#TimeoutSeconds').value = config.TimeoutSeconds || 5;
                    page.querySelector('#MaxConcurrentRequests').value = config.MaxConcurrentRequests || 4;
                    // 0 是合法值（禁用缓存），不能用 || 兜底
                    page.querySelector('#ActorCacheMinutes').value = config.ActorCacheMinutes !== undefined && config.ActorCacheMinutes !== null ? config.ActorCacheMinutes : 360;
                    Dashboard.hideLoadingMsg();
                });
            });

            page.querySelector('#AmaneConfigForm').addEventListener('submit', function (e) {
                e.preventDefault();
                Dashboard.showLoadingMsg();
                ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                    config.ServerUrl = page.querySelector('#ServerUrl').value.trim();
                    config.ApiToken = page.querySelector('#ApiToken').value.trim();
                    config.TimeoutSeconds = parseInt(page.querySelector('#TimeoutSeconds').value, 10) || 5;
                    config.MaxConcurrentRequests = parseInt(page.querySelector('#MaxConcurrentRequests').value, 10) || 4;
                    var cacheMinutes = parseInt(page.querySelector('#ActorCacheMinutes').value, 10);
                    config.ActorCacheMinutes = isNaN(cacheMinutes) ? 360 : Math.max(0, cacheMinutes);
                    ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                        Dashboard.processPluginConfigurationUpdateResult(result);
                    });
                });
            });

            page.querySelector('#TestConnection').addEventListener('click', function () {
                const button = page.querySelector('#TestConnection');
                const resultBox = page.querySelector('#TestConnectionResult');
                button.disabled = true;
                resultBox.style.display = 'block';
                resultBox.style.color = '';
                resultBox.textContent = '正在测试…';

                ApiClient.getJSON(ApiClient.getUrl('Amane/Health')).then(function (result) {
                    if (!result.Reachable) {
                        resultBox.style.color = '#c62828';
                        resultBox.textContent = '✗ 无法连接 Amane：' + (result.Error || '未知错误') + '（请检查服务地址与服务是否已启动）';
                    } else if (result.AuthStatus === 'ok') {
                        resultBox.style.color = '#2e7d32';
                        resultBox.textContent = '✓ 连接正常 · Amane v' + (result.Version || '?') + ' · 延迟 ' + result.LatencyMs + 'ms · Token 鉴权通过';
                    } else if (result.AuthStatus === 'unauthorized') {
                        resultBox.style.color = '#c62828';
                        resultBox.textContent = '✗ 服务可达（延迟 ' + result.LatencyMs + 'ms · v' + (result.Version || '?') + '），但 Token 鉴权失败，请检查 API Token';
                    } else if (result.AuthStatus === 'notConfigured') {
                        resultBox.style.color = '#ef6c00';
                        resultBox.textContent = '△ 服务可达（延迟 ' + result.LatencyMs + 'ms · v' + (result.Version || '?') + '），但未配置 API Token，刮削请求会被拒绝';
                    } else {
                        resultBox.style.color = '#ef6c00';
                        resultBox.textContent = '△ 服务可达（延迟 ' + result.LatencyMs + 'ms · v' + (result.Version || '?') + '），但鉴权状态未知';
                    }
                }).catch(function () {
                    resultBox.style.color = '#c62828';
                    resultBox.textContent = '✗ 测试请求失败，请查看 Emby 日志';
                }).finally(function () {
                    button.disabled = false;
                });
            });

            page.querySelector('#ClearCache').addEventListener('click', function () {
                const button = page.querySelector('#ClearCache');
                const resultBox = page.querySelector('#ClearCacheResult');
                button.disabled = true;
                resultBox.style.display = 'block';
                resultBox.style.color = '';
                resultBox.textContent = '正在清除…';

                ApiClient.fetch({ url: ApiClient.getUrl('Amane/ClearCache'), type: 'POST', dataType: 'json' }).then(function (result) {
                    return result && typeof result.json === 'function' ? result.json() : result;
                }).then(function (result) {
                    resultBox.style.color = '#2e7d32';
                    resultBox.textContent = '✓ 演员缓存已清除（' + (result.Cleared || 0) + ' 条），下次刮削将拉取最新数据';
                }).catch(function () {
                    resultBox.style.color = '#c62828';
                    resultBox.textContent = '✗ 清除请求失败，请查看 Emby 日志';
                }).finally(function () {
                    button.disabled = false;
                });
            });
            };
});
