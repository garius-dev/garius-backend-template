using System.Globalization;
using System.Text.Json;
using Garius.Api.Infrastructure.Networking;
using Garius.Core.Results;
using Garius.Infrastructure.RateLimiting;
using Microsoft.Extensions.Options;

namespace Garius.Api.Infrastructure.RateLimiting;

/// <summary>
/// Aplica o rate limit por <b>IP real</b>, com contadores no Redis (valem entre réplicas).
///
/// <para>
/// Duas camadas, e as duas importam:
/// </para>
/// <list type="number">
///   <item>um teto <b>geral</b>, generoso, que contém um cliente descontrolado;</item>
///   <item>tetos <b>agressivos</b> nos endpoints de autenticação, que são as superfícies de
///         brute force.</item>
/// </list>
/// </summary>
internal sealed class RateLimitMiddleware(
    RequestDelegate next,
    RedisRateLimiter limiter,
    IOptions<RateLimitOptions> options,
    ILogger<RateLimitMiddleware> logger)
{
    private readonly RateLimitOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.Enabled)
        {
            await next(context);

            return;
        }

        // O IP REAL — nunca o RemoteIpAddress cru.
        //
        // Atrás da Cloudflare e do Traefik, o RemoteIpAddress é o do PROXY: todos os usuários
        // do mundo teriam o mesmo IP, e o limite global de 100/min seria compartilhado por
        // todos eles. A aplicação inteira ficaria bloqueada em segundos, e a mensagem de erro
        // não daria nenhuma pista do porquê.
        //
        // O GetClientIp() vem do RealIpMiddleware, que valida o CF-Connecting-IP contra os
        // ranges da Cloudflare — sem isso, qualquer cliente forjaria o header e escaparia do
        // limite trocando um valor.
        var ip = context.GetClientIp() ?? "unknown";

        var path = context.Request.Path;

        // Os limites de autenticação são POR ENDPOINT e agressivos. O global também vale para
        // eles (as duas camadas se somam), mas é o específico que morde primeiro.
        var (rule, partition) = path switch
        {
            _ when path.StartsWithSegments("/auth/login")
                => (_options.Login, $"login:{ip}"),

            _ when path.StartsWithSegments("/auth/token")
                => (_options.Token, $"token:{ip}"),

            _ when path.StartsWithSegments("/auth/refresh")
                => (_options.Refresh, $"refresh:{ip}"),

            _ => (_options.Global, $"global:{ip}")
        };

        var result = await limiter.CheckAsync(
            partition, rule.PermitLimit, rule.Window, context.RequestAborted);

        if (!result.IsAllowed)
        {
            await RejectAsync(context, partition, result, ip);

            return;
        }

        await next(context);
    }

    private async Task RejectAsync(
        HttpContext context,
        string partition,
        RateLimitResult result,
        string ip)
    {
        // Um estouro de rate limit num endpoint de AUTENTICAÇÃO é um evento de segurança —
        // é o que se procura ao investigar um brute force. Vai como Warning, para o Grafana
        // poder alertar sobre ele.
        logger.LogWarning(
            "{Audit} rate_limit.exceeded Partição={Partition} IP={ClientIp} Rota={Path}",
            "SECURITY", partition, ip, context.Request.Path.Value);

        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(result.RetryAfter.TotalSeconds));

        // Retry-After é padrão HTTP (RFC 9110): um cliente bem-comportado o respeita e para de
        // martelar. Sem ele, o cliente continua tentando e continua sendo bloqueado — e o
        // 429 vira apenas ruído no log, sem conter nada.
        context.Response.Headers.RetryAfter =
            retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        var error = Error.TooManyRequests(
            "rate_limit.exceeded",
            $"Requisições em excesso. Tente novamente em {retryAfterSeconds} segundo(s).");

        // ProblemDetails, como TODO erro da API — o front não precisa de um caso especial para
        // o 429. Escrito à mão (e não pelo ToHttpResult) porque aqui não há um endpoint: o
        // middleware corta o pipeline antes de chegar a um.
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            type = "https://tools.ietf.org/html/rfc9110#section-15.5.29",
            title = "Requisições em excesso",
            status = StatusCodes.Status429TooManyRequests,
            detail = error.Message,
            code = error.Code,
            traceId = context.TraceIdentifier
        }), context.RequestAborted);
    }
}
