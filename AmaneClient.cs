using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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
    /// 按 Amane 内部整数 id 直取元数据（识别对话框绑定"Amane 电影 Id"时使用）。
    /// </summary>
    /// <param name="id">Amane 内部 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中条目；未命中或出错为 null。</returns>
    public async Task<AmaneMetadata?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        var detail = await GetAsync<AmaneMetadataDetailResponse>($"/api/metadata/{id}", cancellationToken).ConfigureAwait(false);
        return detail?.Metadata;
    }

    /// <summary>
    /// 统一解析元数据：按 ProviderIds 中的 Amane 键值逐级精确化，最终回退到名称搜索。
    /// 键设计：<c>AmaneId</c>（内部数字 id，精确直取）→ <c>Amane</c>（识别框值：Amane:番号 / 番号 / 数字 id）→ name 搜索。
    /// </summary>
    /// <param name="providerIds">条目的 ProviderIds。</param>
    /// <param name="name">条目名称（兜底搜索词）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中条目；未命中为 null。</returns>
    public async Task<AmaneMetadata?> ResolveMetadataAsync(
        IReadOnlyDictionary<string, string> providerIds,
        string? name,
        CancellationToken cancellationToken)
    {
        // 1. 内部数字 id（识别成功后自动写入，最快路径）
        if (providerIds.TryGetValue(Providers.AmaneMovieProvider.InternalIdProviderIdName, out var internalIdValue)
            && TryParseInternalId(internalIdValue, out var storedId))
        {
            var byStoredId = await GetByIdAsync(storedId, cancellationToken).ConfigureAwait(false);
            if (byStoredId is not null)
            {
                return byStoredId;
            }
        }

        // 2. 识别框值（容忍 "Amane:" 前缀）：数字直取，番号搜索
        var amaneValue = NormalizeIdValue(providerIds.TryGetValue(Providers.AmaneMovieProvider.ProviderIdName, out var raw) ? raw : null);
        if (!string.IsNullOrWhiteSpace(amaneValue))
        {
            if (TryParseInternalId(amaneValue, out var parsedId))
            {
                var byId = await GetByIdAsync(parsedId, cancellationToken).ConfigureAwait(false);
                if (byId is not null)
                {
                    return byId;
                }
            }

            return await LookupAsync(amaneValue, cancellationToken).ConfigureAwait(false);
        }

        // 3. 名称兜底
        return string.IsNullOrWhiteSpace(name)
            ? null
            : await LookupAsync(name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 规范化识别框输入：剥离 "Amane:" 前缀（大小写不敏感）并裁剪空白。
    /// </summary>
    internal static string? NormalizeIdValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.StartsWith("Amane:", StringComparison.OrdinalIgnoreCase)
            ? trimmed["Amane:".Length..].Trim()
            : trimmed;
    }

    /// <summary>
    /// 尝试把 ProviderIds 中的 Amane 值解析为内部整数 id（识别框允许填数字 id 或番号）。
    /// </summary>
    internal static bool TryParseInternalId(string? value, out int id)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
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

        var items = await SearchActorsAsync(name, 5, cancellationToken).ConfigureAwait(false);

        var actor = items.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal))
                    ?? items.FirstOrDefault();

        CacheActor(name, actor);
        return actor;
    }

    /// <summary>
    /// 按演员名检索演员列表。
    /// </summary>
    /// <param name="name">演员名。</param>
    /// <param name="limit">最大返回条数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中演员列表；出错或未命中为空列表。</returns>
    public async Task<IReadOnlyList<AmaneActor>> SearchActorsAsync(string name, int limit, CancellationToken cancellationToken)
    {
        var list = await GetAsync<AmaneActorListResponse>(
            $"/api/actors?search={Uri.EscapeDataString(name)}&limit={limit}",
            cancellationToken).ConfigureAwait(false);
        return list?.Items ?? (IReadOnlyList<AmaneActor>)Array.Empty<AmaneActor>();
    }

    /// <summary>
    /// 按 Amane 内部整数 id 直取演员（演员外部 ID 绑定时使用）。
    /// </summary>
    /// <param name="id">Amane 演员 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中演员；未命中或出错为 null。</returns>
    public async Task<AmaneActor?> GetActorByIdAsync(int id, CancellationToken cancellationToken)
    {
        var cacheKey = "id:" + id.ToString(CultureInfo.InvariantCulture);
        if (_actorCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Actor;
        }

        // 详情端点直接返回演员对象（无包装），实测契约见 AGENTS.md
        var actor = await GetAsync<AmaneActor>($"/api/actors/{id}", cancellationToken).ConfigureAwait(false);
        CacheActor(cacheKey, actor);
        return actor;
    }

    /// <summary>
    /// 统一解析演员：ProviderIds["Amane"] 数字 id 直取 → 框内名字搜索 → 条目名兜底；id 失效自动回退名字。
    /// </summary>
    /// <param name="providerIds">条目的 ProviderIds。</param>
    /// <param name="name">条目名称（兜底搜索词）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中演员；未命中为 null。</returns>
    public async Task<AmaneActor?> ResolveActorAsync(
        IReadOnlyDictionary<string, string> providerIds,
        string? name,
        CancellationToken cancellationToken)
    {
        // 外部 ID 框值（容忍 "Amane:" 前缀）：数字 id 直取，演员名搜索
        var amaneValue = NormalizeIdValue(providerIds.TryGetValue(Providers.AmaneMovieProvider.ProviderIdName, out var raw) ? raw : null);
        if (!string.IsNullOrWhiteSpace(amaneValue))
        {
            if (TryParseInternalId(amaneValue, out var id))
            {
                var byId = await GetActorByIdAsync(id, cancellationToken).ConfigureAwait(false);
                if (byId is not null)
                {
                    return byId;
                }

                // id 失效（如 Amane 库重建）：落到名字兜底
            }
            else
            {
                return await LookupActorAsync(amaneValue, cancellationToken).ConfigureAwait(false);
            }
        }

        return string.IsNullOrWhiteSpace(name)
            ? null
            : await LookupActorAsync(name, cancellationToken).ConfigureAwait(false);
    }

    private void CacheActor(string key, AmaneActor? actor)
    {
        _actorCache[key] = (actor, DateTimeOffset.UtcNow.Add(ActorCacheTtl));
        if (actor is not null)
        {
            if (!string.IsNullOrWhiteSpace(actor.Name))
            {
                _actorCache[actor.Name] = (actor, DateTimeOffset.UtcNow.Add(ActorCacheTtl));
            }

            if (actor.Id > 0)
            {
                _actorCache["id:" + actor.Id.ToString(CultureInfo.InvariantCulture)] = (actor, DateTimeOffset.UtcNow.Add(ActorCacheTtl));
            }
        }
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
