using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Amane.Api;

/// <summary>
/// Amane 诊断接口：供配置页"测试连接"按钮做服务端探活，避免浏览器直连 Amane 的 CORS 与 Token 暴露问题。
/// </summary>
[ApiController]
[Authorize]
[Route("Amane")]
public class AmaneDiagnosticsController : ControllerBase
{
    private readonly AmaneClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="AmaneDiagnosticsController"/> class.
    /// </summary>
    /// <param name="client">Instance of <see cref="AmaneClient"/>.</param>
    public AmaneDiagnosticsController(AmaneClient client)
    {
        _client = client;
    }

    /// <summary>
    /// 探活 Amane 服务并验证 Token 鉴权，返回延迟与版本信息。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>诊断结果。</returns>
    [HttpGet("Health")]
    public async Task<ActionResult<AmaneHealthCheckResult>> CheckHealth(CancellationToken cancellationToken)
    {
        return await _client.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
    }
}
