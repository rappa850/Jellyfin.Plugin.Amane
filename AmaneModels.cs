using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Amane;

/// <summary>
/// Amane <c>GET /api/metadata</c> 的列表响应（{items, total}）。
/// </summary>
public sealed class AmaneListResponse
{
    /// <summary>Gets or sets the 条目列表（元素为完整的 MetadataResponse）。</summary>
    [JsonPropertyName("items")]
    public List<AmaneMetadata> Items { get; set; } = new();

    /// <summary>Gets or sets the 命中总数。</summary>
    [JsonPropertyName("total")]
    public int Total { get; set; }
}

/// <summary>
/// Amane MetadataResponse（真实 schema，见 Amane/amane-response.sample.json）。
/// </summary>
public sealed class AmaneMetadata
{
    /// <summary>Gets or sets the Amane 内部整数 id。</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the 番号，例如 IPZZ-822。</summary>
    [JsonPropertyName("number")]
    public string? Number { get; set; }

    /// <summary>Gets or sets the LLM 润色后的中文标题。</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the 演员名列表（纯字符串）。</summary>
    [JsonPropertyName("actors")]
    public List<string>? Actors { get; set; }

    /// <summary>Gets or sets the 导演名列表。</summary>
    [JsonPropertyName("directors")]
    public List<string>? Directors { get; set; }

    /// <summary>Gets or sets the 厂商（片商）。</summary>
    [JsonPropertyName("studio")]
    public string? Studio { get; set; }

    /// <summary>Gets or sets the 发行日期（YYYY-MM-DD）。</summary>
    [JsonPropertyName("release")]
    public string? Release { get; set; }

    /// <summary>Gets or sets the 时长（分钟）。</summary>
    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }

    /// <summary>Gets or sets the 标签列表（作为 Jellyfin 流派）。</summary>
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    /// <summary>Gets or sets the 剧情简介（宣发文案）。</summary>
    [JsonPropertyName("plot")]
    public string? Plot { get; set; }

    /// <summary>Gets or sets the 封面图 URL。</summary>
    [JsonPropertyName("poster_url")]
    public string? PosterUrl { get; set; }

    /// <summary>Gets or sets the 大图/横幅 URL。</summary>
    [JsonPropertyName("thumb_url")]
    public string? ThumbUrl { get; set; }

    /// <summary>Gets or sets the 剧照 URL 列表。</summary>
    [JsonPropertyName("extrafanart")]
    public List<string>? ExtraFanart { get; set; }

    /// <summary>Gets or sets the 综合评分（来源站 5 分制）。</summary>
    [JsonPropertyName("score")]
    public float? Score { get; set; }

    /// <summary>Gets or sets the 各来源原始数据；用于提取原文标题。</summary>
    [JsonPropertyName("raw")]
    public Dictionary<string, JsonElement>? Raw { get; set; }

    /// <summary>
    /// 从 <see cref="Raw"/> 中取第一个来源的原始标题（通常为日文原标题）。
    /// </summary>
    /// <returns>原始标题；取不到则为 null。</returns>
    public string? GetOriginalTitle()
    {
        if (Raw is null)
        {
            return null;
        }

        foreach (var element in Raw.Values)
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("title", out var title)
                && title.ValueKind == JsonValueKind.String)
            {
                return title.GetString();
            }
        }

        return null;
    }
}


/// <summary>
/// Amane <c>GET /api/actors</c> 的列表响应（{items, total}）。
/// </summary>
public sealed class AmaneActorListResponse
{
    /// <summary>Gets or sets the 演员条目列表。</summary>
    [JsonPropertyName("items")]
    public List<AmaneActor> Items { get; set; } = new();

    /// <summary>Gets or sets the 命中总数。</summary>
    [JsonPropertyName("total")]
    public int Total { get; set; }
}

/// <summary>
/// Amane ActorResponse（真实 schema，见 Amane/amane-actor.sample.json）。
/// </summary>
public sealed class AmaneActor
{
    /// <summary>Gets or sets the Amane 内部整数 id。</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the 演员名。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the 别名列表。</summary>
    [JsonPropertyName("aliases")]
    public List<string>? Aliases { get; set; }

    /// <summary>Gets or sets the 人物简介。</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>Gets or sets the 生日（YYYY-MM-DD）。</summary>
    [JsonPropertyName("birthday")]
    public string? Birthday { get; set; }

    /// <summary>Gets or sets the 出身地。</summary>
    [JsonPropertyName("birthplace")]
    public string? Birthplace { get; set; }

    /// <summary>Gets or sets the 头像/写真 URL 列表。</summary>
    [JsonPropertyName("image_urls")]
    public List<string>? ImageUrls { get; set; }
}


/// <summary>
/// Amane <c>GET /api/metadata/{id}</c> 的详情响应（{metadata, files, …}）。
/// </summary>
public sealed class AmaneMetadataDetailResponse
{
    /// <summary>Gets or sets the 元数据主体。</summary>
    [JsonPropertyName("metadata")]
    public AmaneMetadata? Metadata { get; set; }
}


/// <summary>
/// Amane <c>GET /api/health</c> 的就绪探测响应（{status, version}，无需 token）。
/// </summary>
public sealed class AmaneHealthResponse
{
    /// <summary>Gets or sets the 服务状态（正常为 "ok"）。</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>Gets or sets the Amane 版本号。</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}


/// <summary>
/// 配置页"测试连接"的诊断结果：可达性、延迟、版本与 Token 鉴权状态。
/// </summary>
public sealed class AmaneHealthCheckResult
{
    /// <summary>Gets or sets a value indicating whether Amane 服务可达（/api/health 探活成功）。</summary>
    public bool Reachable { get; set; }

    /// <summary>Gets or sets the /api/health 往返延迟（毫秒）。</summary>
    public long LatencyMs { get; set; }

    /// <summary>Gets or sets the Amane 版本号（可达时）。</summary>
    public string? Version { get; set; }

    /// <summary>Gets or sets the Token 鉴权结论：ok / unauthorized / notConfigured / unknown。</summary>
    public string AuthStatus { get; set; } = "unknown";

    /// <summary>Gets or sets the 失败原因（不可达或异常时）。</summary>
    public string? Error { get; set; }
}
