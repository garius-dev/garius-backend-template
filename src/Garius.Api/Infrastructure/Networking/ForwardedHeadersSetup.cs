using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

// Microsoft.AspNetCore.HttpOverrides também define um IPNetwork (legado, deprecado).
// KnownIPNetworks usa o System.Net.IPNetwork nativo — o alias remove a ambiguidade.
using IPNetwork = System.Net.IPNetwork;

namespace Garius.Api.Infrastructure.Networking;

/// <summary>
/// Faz o ASP.NET entender que está atrás de um proxy (Traefik) e de um CDN (Cloudflare).
///
/// <para>
/// Sem isto, dois estragos: <c>Request.IsHttps</c> é <c>false</c> dentro do container
/// (o Traefik termina o TLS e fala HTTP puro com a app) — e aí um cookie marcado como
/// "Secure só se IsHttps" sai <b>sem a flag Secure em produção</b> — e o
/// <c>RemoteIpAddress</c> é o do container do Traefik, não o do cliente.
/// </para>
///
/// <para>
/// <b>Atenção:</b> o ASP.NET só honra os headers encaminhados se o proxy imediato estiver
/// declarado. Com <c>Security:TrustedProxies</c> vazio, os headers são <b>silenciosamente
/// ignorados</b> — foi o que aconteceu no template anterior.
/// </para>
/// </summary>
internal static class ForwardedHeadersSetup
{
    internal static IServiceCollection AddConfiguredForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var trustedProxies = configuration
            .GetSection($"{SecurityOptions.SectionName}:TrustedProxies")
            .Get<string[]>() ?? [];

        if (environment.IsProduction() && trustedProxies.Length == 0)
        {
            // Fail-fast: no template anterior, esta lista vazia fez o ASP.NET ignorar os
            // headers do proxy em silêncio — quebrando rate limit, lockout e auditoria,
            // e derrubando a flag Secure dos cookies. Melhor não subir do que subir mentindo.
            throw new InvalidOperationException(
                "Security:TrustedProxies está vazio em Production. Sem isso, o ASP.NET ignora " +
                "X-Forwarded-For e X-Forwarded-Proto: todo request apareceria com o IP do Traefik " +
                "(quebrando rate limit, lockout e auditoria) e os cookies sairiam sem a flag Secure. " +
                "Declare a rede Docker do Traefik (ex.: \"172.18.0.0/16\").");
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // O default do ASP.NET é confiar apenas em loopback. Como o Traefik chega pela
            // rede Docker (172.x), sem limpar isto ele não seria reconhecido como proxy.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();

            foreach (var proxy in trustedProxies)
            {
                if (IPAddress.TryParse(proxy, out var ip))
                {
                    options.KnownProxies.Add(ip);
                }
                else if (IPNetwork.TryParse(proxy, out var network))
                {
                    options.KnownIPNetworks.Add(network);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Security:TrustedProxies contém um valor que não é IP nem CIDR: '{proxy}'.");
                }
            }

            if (trustedProxies.Length == 0)
            {
                // Fora de produção não há proxy: o cliente fala direto com o Kestrel.
                options.KnownIPNetworks.Add(new IPNetwork(IPAddress.Loopback, 8));
                options.KnownIPNetworks.Add(new IPNetwork(IPAddress.IPv6Loopback, 128));
            }
        });

        return services;
    }
}
