using System.Net;
using Microsoft.Extensions.Options;

namespace Garius.Api.Infrastructure.Networking;

/// <summary>
/// Resolve o IP real do cliente e o publica em <c>HttpContext.Items</c>, de onde todo o
/// resto (logs, rate limit, lockout, auditoria) o consome via <see cref="HttpContextExtensions.GetClientIp"/>.
///
/// <para>
/// <b>O ponto crítico:</b> <c>CF-Connecting-IP</c> é apenas um header HTTP — qualquer cliente
/// pode enviá-lo. Ele só é acreditado se o request <b>realmente</b> chegou de um range da
/// Cloudflare. Sem essa checagem, um atacante forja o header e escapa de rate limit, de
/// lockout por IP e da auditoria — que foi exatamente a falha do template anterior.
/// </para>
///
/// <para>
/// Roda <b>depois</b> de <c>UseForwardedHeaders</c>, que já normalizou
/// <c>Connection.RemoteIpAddress</c> a partir do <c>X-Forwarded-For</c> para os proxies
/// declarados em <c>Security:TrustedProxies</c>.
/// </para>
/// </summary>
internal sealed class RealIpMiddleware
{
    internal const string CloudflareHeader = "CF-Connecting-IP";
    internal const string ClientIpItemKey = "ClientIp";

    private readonly RequestDelegate _next;
    private readonly IReadOnlyList<IPNetwork> _cloudflareNetworks;
    private readonly bool _trustCloudflare;

    public RealIpMiddleware(RequestDelegate next, IOptions<SecurityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _next = next;

        var settings = options.Value;
        _trustCloudflare = settings.TrustCloudflareIps;

        var cidrs = settings.CloudflareIpRanges.Count > 0
            ? settings.CloudflareIpRanges
            : CloudflareIpRanges.Default;

        _cloudflareNetworks = _trustCloudflare
            ? CloudflareIpRanges.Parse(cidrs)
            : [];
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Items[ClientIpItemKey] = Resolve(context);

        return _next(context);
    }

    private string Resolve(HttpContext context)
    {
        // Após UseForwardedHeaders, este é o IP do peer imediato confiável
        // (ou o IP real, se o proxy estava declarado em TrustedProxies).
        var remoteIp = context.Connection.RemoteIpAddress;

        if (_trustCloudflare
            && remoteIp is not null
            && IsFromCloudflare(remoteIp)
            && context.Request.Headers.TryGetValue(CloudflareHeader, out var header))
        {
            var candidate = header.ToString();

            if (IPAddress.TryParse(candidate, out var clientIp))
            {
                return clientIp.ToString();
            }
        }

        return remoteIp?.ToString() ?? "unknown";
    }

    private bool IsFromCloudflare(IPAddress address)
    {
        // IPv4 encapsulado em IPv6 (::ffff:1.2.3.4) precisa ser normalizado, ou a
        // comparação contra os ranges IPv4 da Cloudflare falha silenciosamente.
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        foreach (var network in _cloudflareNetworks)
        {
            if (network.Contains(normalized))
            {
                return true;
            }
        }

        return false;
    }
}

public static class HttpContextExtensions
{
    /// <summary>
    /// IP real do cliente, resolvido pelo <see cref="RealIpMiddleware"/>.
    /// Use sempre este método — nunca <c>Connection.RemoteIpAddress</c> diretamente,
    /// que atrás do Traefik/Cloudflare devolve o IP do proxy.
    /// </summary>
    public static string GetClientIp(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(RealIpMiddleware.ClientIpItemKey, out var ip) && ip is string value
            ? value
            : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
