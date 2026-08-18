using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Garius.Api.Infrastructure.Errors;
using Garius.Core.Results;
using Garius.Infrastructure.RateLimiting;
using Microsoft.Extensions.Options;

namespace Garius.Api.Infrastructure.RateLimiting;

/// <summary>
/// A <b>segunda</b> camada de rate limit: por <b>identidade</b>, não por IP.
///
/// <para>
/// <b>Por que uma segunda camada, e não trocar a primeira.</b> Limite só por IP erra dos dois
/// lados: pune o cliente legítimo atrás de CGNAT (milhares de pessoas dividindo um endereço, e
/// portanto uma cota) e não contém o atacante com um <c>/64</c> de IPv6 (endereços de sobra
/// para diluir o volume). Limite só por identidade não contém quem ainda <i>não</i> se
/// autenticou. As duas dimensões são independentes, e faltando uma, um dos dois casos passa.
/// </para>
///
/// <para>
/// <b>Por que ela roda DEPOIS da autorização.</b> A camada por IP fica lá na frente de
/// propósito — ela é a defesa contra volume e precisa ser barata (ver a ordem do pipeline no
/// <c>Program.cs</c>: colocá-la depois da autenticação faria cada tentativa de brute force
/// pagar um PBKDF2 antes de ser recusada, e o "rate limit" viraria o vetor de DoS).
/// </para>
///
/// <para>
/// Esta, porém, <b>precisa</b> saber quem é o chamador — e isso só existe depois do
/// <c>UseAuthentication</c>/<c>UseAuthorization</c>. O custo é aceitável justamente porque ela
/// só age sobre requisições que <b>já passaram</b> pela camada de IP e pela autenticação: o
/// tráfego anônimo abusivo nunca chega aqui.
/// </para>
///
/// <para>
/// <b>Anônimo passa direto.</b> Não é brecha: quem não se autenticou já foi contado pela camada
/// de IP. Contar de novo aqui seria aplicar dois limites à mesma requisição pelo mesmo motivo.
/// </para>
/// </summary>
internal sealed class IdentityRateLimitMiddleware(
    RequestDelegate next,
    RedisRateLimiter limiter,
    IOptions<RateLimitOptions> options,
    ILogger<IdentityRateLimitMiddleware> logger)
{
    private readonly RateLimitOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var rule = _options.Identity;

        if (!_options.Enabled || !rule.Enabled)
        {
            await next(context);

            return;
        }

        var identity = ResolveIdentity(context);

        if (identity is null)
        {
            // Anônimo: já contado pela camada de IP.
            await next(context);

            return;
        }

        var result = await limiter.CheckAsync(
            $"identity:{identity}", rule.PermitLimit, rule.Window, context.RequestAborted);

        // Os headers da RFC 9331 vão em TODA resposta, não só no 429.
        //
        // Um cliente que só descobre o limite ao bater nele não tem como se comportar bem: ele
        // dispara em paralelo, toma 429, e a única estratégia que lhe resta é tentar de novo.
        // Com os headers, ele sabe quanto lhe resta ANTES de estourar — e é assim que um
        // integrador escreve um cliente que não briga com a API.
        WriteRateLimitHeaders(context, rule.PermitLimit, result);

        if (!result.IsAllowed)
        {
            await RejectAsync(context, identity, result);

            return;
        }

        await next(context);
    }

    /// <summary>
    /// Quem é o chamador — nas <b>três</b> formas de credencial que o template aceita.
    ///
    /// <para>
    /// O prefixo (<c>user:</c>, <c>client:</c>) importa: sem ele, um <c>client_id</c> que por
    /// acaso coincidisse com um id de usuário compartilharia a cota com ele. Improvável, mas o
    /// tipo de coincidência que ninguém depura.
    /// </para>
    /// </summary>
    private static string? ResolveIdentity(HttpContext context)
    {
        var user = context.User;

        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        // Máquina (M2M ou chave de API). Vem primeiro porque um principal de máquina também
        // carrega um NameIdentifier — e é o client_id que se quer limitar, não o sujeito.
        var clientId = user.FindFirst("client_id")?.Value;

        if (!string.IsNullOrWhiteSpace(clientId))
        {
            return $"client:{clientId}";
        }

        var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return string.IsNullOrWhiteSpace(userId) ? null : $"user:{userId}";
    }

    /// <summary>
    /// Headers da <b>RFC 9331</b>. É o vocabulário padrão de rate limit em HTTP — um cliente
    /// que já fala com outra API o entende sem ler documentação nenhuma.
    /// </summary>
    private static void WriteRateLimitHeaders(
        HttpContext context,
        int limit,
        RateLimitResult result)
    {
        var remaining = Math.Max(0, limit - result.Count);

        // OnStarting: os headers precisam ser escritos ANTES de a resposta começar a sair, e
        // neste ponto o pipeline ainda vai chamar o endpoint. Escrevê-los depois estoura com
        // "headers are read-only, response has already started".
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["RateLimit-Limit"] =
                limit.ToString(CultureInfo.InvariantCulture);

            context.Response.Headers["RateLimit-Remaining"] =
                remaining.ToString(CultureInfo.InvariantCulture);

            context.Response.Headers["RateLimit-Reset"] =
                ((int)Math.Ceiling(result.RetryAfter.TotalSeconds))
                    .ToString(CultureInfo.InvariantCulture);

            return Task.CompletedTask;
        });
    }

    private async Task RejectAsync(HttpContext context, string identity, RateLimitResult result)
    {
        // Warning, e com a identidade: diferente do 429 por IP, este diz QUEM estourou — é o
        // que permite descobrir o integrador com um retry-loop mal escrito antes de ele virar
        // um incidente.
        logger.LogWarning(
            "{Audit} rate_limit.identity_exceeded Identidade={Identity} Rota={Path}",
            "SECURITY", identity, context.Request.Path.Value);

        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(result.RetryAfter.TotalSeconds));

        context.Response.Headers.RetryAfter =
            retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        var error = Error.TooManyRequests(
            "rate_limit.identity_exceeded",
            $"Requisições em excesso para esta credencial. Tente novamente em {retryAfterSeconds} segundo(s).");

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            type = "https://tools.ietf.org/html/rfc9110#section-15.5.29",
            title = "Requisições em excesso",
            status = StatusCodes.Status429TooManyRequests,
            detail = error.Message,
            code = error.Code,
            // O MESMO traceId do resto da API — ver ProblemDetailsFactory.GetTraceId.
            traceId = ProblemDetailsFactory.GetTraceId(context)
        }), context.RequestAborted);
    }
}
