using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Amane;

/// <summary>
/// Amane API 薄客户端：仅负责请求与反序列化，不做任何番号解析或降级逻辑。
/// </summary>
public sealed class AmaneClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan ActorCacheTtl = TimeSpan.FromHours(6);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AmaneClient> _logger;

    // 演员查询缓存：避免同一演员在多部电影刷新时重复请求
    private readonly ConcurrentDictionary<string, (AmaneActor? Actor, DateTimeOffset ExpiresAt)> _actorCache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AmaneClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{AmaneClient}"/> interface.</param>
    public AmaneClient(IHttpClientFactory httpClientFactory, ILogger<AmaneClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// 按文件名/番号检索元数据，返回首个命中项；未命中或出错返回 null。
    /// </summary>
    /// <param name="query">Jellyfin 传入的名称（通常即文件名清洗结果或番号）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>首个命中条目；未命中为 null。</returns>
    public async Task<AmaneMetadata?> LookupAsync(string query, CancellationToken cancellationToken)
    {
        var results = await SearchAsync(query, 1, cancellationToken).ConfigureAwait(false);
        return results.Count > 0 ? results[0] : null;
    }

    /// <summary>
    /// 按文件名/番号检索元数据列表。
    /// </summary>
    /// <param name="query">检索词。</param>
    /// <param name="limit">最大返回条数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中条目列表；出错或未命中为空列表。</returns>
    public async Task<IReadOnlyList<AmaneMetadata>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var list = await GetAsync<AmaneListResponse>(
            $"/api/metadata?search={Uri.EscapeDataString(query)}&limit={limit}",
            cancellationToken).ConfigureAwait(false);
        return list?.Items ?? (IReadOnlyList<AmaneMetadata>)Array.Empty<AmaneMetadata>();
    }

    /// <summary>
    /// 按演员名检索演员信息（带 6 小时进程内缓存）。优先取名字精确匹配的条目。
    /// </summary>
    /// <param name="name">演员名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>首个命中演员；未命中或出错为 null。</returns>
    public async Task<AmaneActor?> LookupActorAsync(string name, CancellationToken cancellationToken)
    {
        if (_actorCache.TryGetValue(name, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Actor;
        }

        var list = await GetAsync<AmaneActorListResponse>(
            $"/api/actors?search={Uri.EscapeDataString(name)}&limit=5",
            cancellationToken).ConfigureAwait(false);

        var actor = list?.Items.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal))
                    ?? list?.Items.FirstOrDefault();

        _actorCache[name] = (actor, DateTimeOffset.UtcNow.Add(ActorCacheTtl));
        return actor;
    }

    /// <summary>
    /// 拉取图片响应（供 IRemoteImageProvider.GetImageResponse 使用）。
    /// </summary>
    /// <param name="url">图片 URL。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>HTTP 响应。</returns>
    public async Task<HttpResponseMessage> GetImageAsync(string url, CancellationToken cancellationToken)
    {
        // 不在此处释放 client/response，响应流由 Jellyfin 读取
        var client = CreateClient();
        return await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> GetAsync<T>(string pathAndQuery, CancellationToken cancellationToken)
        where T : class
    {
        var config = Plugin.Instance?.Configuration;
        var serverUrl = config?.ServerUrl?.TrimEnd('/') ?? "http://127.0.0.1:18000";
        var url = serverUrl + pathAndQuery;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(config?.ApiToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiToken);
            }

            // IHttpClientFactory 创建的 client 由工厂管理生命周期，不应手动释放
            var client = CreateClient();
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Amane 查询失败: {StatusCode} {Url}", response.StatusCode, url);
                return null;
            }

            var payload = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(payload, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Amane 查询异常: {Url}", url);
            return null;
        }
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient();
        var timeout = Plugin.Instance?.Configuration?.TimeoutSeconds ?? 10;
        client.Timeout = TimeSpan.FromSeconds(timeout > 0 ? timeout : 10);
        return client;
    }
}
