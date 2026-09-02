using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Amane;

/// <summary>
/// Amane API 薄客户端：仅负责请求与反序列化，不做任何番号解析或降级逻辑。
/// 内置并发信号量背压、每请求显式超时与连续失败熔断，保护后端与 Jellyfin 刷新线程。
/// </summary>
public sealed class AmaneClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan ActorCacheTtl = TimeSpan.FromHours(6);

    // 弹性默认值：并发上限 / 单请求超时 / 熔断阈值与冷却
    private const int DefaultMaxConcurrentRequests = 4;
    private const int DefaultTimeoutSeconds = 5;
    private const int DefaultCircuitFailureThreshold = 5;
    private static readonly TimeSpan DefaultCircuitCooldown = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AmaneClient> _logger;

    // 背压：限制同时打到 Amane 后端的元数据/演员查询并发数（图片透传不占额度）
    private readonly SemaphoreSlim _requestSemaphore;

    // 熔断：连续失败达到阈值后短时间直接快速失败，不再发请求
    private readonly int _circuitFailureThreshold;
    private readonly TimeSpan _circuitCooldown;
    private int _consecutiveFailures;
    private long _circuitOpenUntilTicks;

    // 测试覆盖项（internal 构造函数注入）；生产路径为 null 时读插件配置
    private readonly TimeSpan? _requestTimeoutOverride;
    private readonly string? _apiTokenOverride;

    // 演员查询缓存：避免同一演员在多部电影刷新时重复请求
    private readonly ConcurrentDictionary<string, (AmaneActor? Actor, DateTimeOffset ExpiresAt)> _actorCache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AmaneClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{AmaneClient}"/> interface.</param>
    public AmaneClient(IHttpClientFactory httpClientFactory, ILogger<AmaneClient> logger)
        : this(httpClientFactory, logger, null, null, DefaultCircuitFailureThreshold, DefaultCircuitCooldown)
    {
    }

    /// <summary>
    /// 测试用构造函数：允许覆盖并发上限、请求超时、熔断参数。
    /// </summary>
    internal AmaneClient(
        IHttpClientFactory httpClientFactory,
        ILogger<AmaneClient> logger,
        int? maxConcurrency,
        TimeSpan? requestTimeout,
        int circuitFailureThreshold,
        TimeSpan circuitCooldown,
        string? apiToken = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _requestTimeoutOverride = requestTimeout;
        _apiTokenOverride = apiToken;
        _circuitFailureThreshold = circuitFailureThreshold;
        _circuitCooldown = circuitCooldown;

        var concurrency = maxConcurrency
            ?? Plugin.Instance?.Configuration?.MaxConcurrentRequests
            ?? DefaultMaxConcurrentRequests;
        _requestSemaphore = new SemaphoreSlim(Math.Max(1, concurrency));
    }

    // 单请求超时：优先测试覆盖值，否则读插件配置（运行时改配置即时生效）
    private TimeSpan RequestTimeout
    {
        get
        {
            if (_requestTimeoutOverride.HasValue)
            {
                return _requestTimeoutOverride.Value;
            }

            var seconds = Plugin.Instance?.Configuration?.TimeoutSeconds ?? DefaultTimeoutSeconds;
            return TimeSpan.FromSeconds(seconds > 0 ? seconds : DefaultTimeoutSeconds);
        }
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

        // 图片透传同样受单请求超时约束，但不占信号量、不参与熔断计数
        // （GetAsync 默认 ResponseContentRead，返回时内容已缓冲，CTS 可随方法释放）
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);
        return await client.GetAsync(url, timeoutCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 配置页"测试连接"诊断：先探活 /api/health 测延迟与版本，再请求需鉴权的 /api/openapi.json 验证 Token。
    /// 不占信号量、不计熔断：用户手动诊断不应被熔断器挡住而误导。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>诊断结果（可达性、延迟、版本、鉴权状态）。</returns>
    public async Task<AmaneHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var serverUrl = config?.ServerUrl?.TrimEnd('/') ?? "http://127.0.0.1:18000";
        var result = new AmaneHealthCheckResult();

        var client = CreateClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);

        // 探针 1：就绪探测（无需 token），同时测往返延迟
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(serverUrl + "/api/health", timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();
            result.LatencyMs = sw.ElapsedMilliseconds;
            if (!response.IsSuccessStatusCode)
            {
                result.Error = $"/api/health 返回 {(int)response.StatusCode}";
                _logger.LogInformation("Amane 健康检查：服务不可达（{Error}）", result.Error);
                return result;
            }

            var payload = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            var health = await JsonSerializer.DeserializeAsync<AmaneHealthResponse>(payload, JsonOptions, timeoutCts.Token).ConfigureAwait(false);
            result.Reachable = true;
            result.Version = health?.Version;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            sw.Stop();
            result.LatencyMs = sw.ElapsedMilliseconds;
            result.Error = ex is OperationCanceledException
                ? $"连接超时（{RequestTimeout.TotalSeconds}s）"
                : ex.Message;
            _logger.LogInformation("Amane 健康检查：无法连接 {ServerUrl}（{Error}）", serverUrl, result.Error);
            return result;
        }

        // 探针 2：/api/health 不校验 token，用需鉴权的 /api/openapi.json 验证 Token 有效性
        var token = _apiTokenOverride ?? config?.ApiToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            result.AuthStatus = "notConfigured";
        }
        else
        {
            try
            {
                using var authRequest = new HttpRequestMessage(HttpMethod.Get, serverUrl + "/api/openapi.json");
                authRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var authResponse = await client.SendAsync(authRequest, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
                result.AuthStatus = authResponse.StatusCode switch
                {
                    HttpStatusCode.OK => "ok",
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "unauthorized",
                    _ => "unknown"
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                result.AuthStatus = "unknown";
                _logger.LogDebug(ex, "Amane 鉴权探针请求失败");
            }
        }

        _logger.LogInformation(
            "Amane 健康检查：延迟 {LatencyMs}ms，版本 {Version}，Token 鉴权 {AuthStatus}",
            result.LatencyMs,
            result.Version ?? "未知",
            result.AuthStatus);
        return result;
    }

    private async Task<T?> GetAsync<T>(string pathAndQuery, CancellationToken cancellationToken)
        where T : class
    {
        var config = Plugin.Instance?.Configuration;
        var serverUrl = config?.ServerUrl?.TrimEnd('/') ?? "http://127.0.0.1:18000";
        var url = serverUrl + pathAndQuery;

        // 熔断打开中：快速失败，不占信号量、不发请求
        var openUntilTicks = Interlocked.Read(ref _circuitOpenUntilTicks);
        if (openUntilTicks > 0 && DateTimeOffset.UtcNow.Ticks < openUntilTicks)
        {
            _logger.LogDebug("Amane 熔断器打开中，跳过请求: {Url}", url);
            return null;
        }

        await _requestSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 每请求显式超时：防止后端 LLM 刮削耗时挂起 Jellyfin 的刷新工作线程
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(config?.ApiToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiToken);
                }

                // IHttpClientFactory 创建的 client 由工厂管理生命周期，不应手动释放
                var client = CreateClient();
                using var response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    // 分状态码打标准日志：401/403 指向 Token 配置，5xx 指向后端故障，404 属正常未匹配
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        _logger.LogWarning(
                            "Amane 鉴权失败 ({StatusCode})，请检查插件配置中的 API Token: {Url}",
                            (int)response.StatusCode,
                            url);
                    }
                    else if ((int)response.StatusCode >= 500)
                    {
                        _logger.LogWarning("Amane 服务端错误 {StatusCode}: {Url}", (int)response.StatusCode, url);
                    }
                    else
                    {
                        _logger.LogInformation("Amane 查询返回 {StatusCode}: {Url}", (int)response.StatusCode, url);
                    }

                    RecordFailure();
                    return null;
                }

                var payload = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
                var result = await JsonSerializer.DeserializeAsync<T>(payload, JsonOptions, timeoutCts.Token).ConfigureAwait(false);
                RecordSuccess();
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Jellyfin 主动取消任务：立刻向上抛，不吞掉也不计入熔断
                throw;
            }
            catch (OperationCanceledException)
            {
                // 外部未取消 → 每请求 CTS / HttpClient.Timeout 触发
                _logger.LogWarning("Amane 请求超时 ({Timeout}s): {Url}", RequestTimeout.TotalSeconds, url);
                RecordFailure();
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                _logger.LogWarning(ex, "Amane 查询异常: {Url}", url);
                RecordFailure();
                return null;
            }
        }
        finally
        {
            _requestSemaphore.Release();
        }
    }

    private void RecordSuccess()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        Interlocked.Exchange(ref _circuitOpenUntilTicks, 0);
    }

    private void RecordFailure()
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        if (failures >= _circuitFailureThreshold)
        {
            Interlocked.Exchange(ref _circuitOpenUntilTicks, DateTimeOffset.UtcNow.Add(_circuitCooldown).Ticks);
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            _logger.LogWarning(
                "Amane 连续失败 {Count} 次，熔断 {Cooldown}s 内不再请求",
                failures,
                _circuitCooldown.TotalSeconds);
        }
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient();
        // HttpClient.Timeout 仅作兜底：比每请求 CTS 超时多 5s 缓冲，确保先由 CTS 触发以便区分超时与外部取消
        client.Timeout = RequestTimeout + TimeSpan.FromSeconds(5);
        return client;
    }
}
