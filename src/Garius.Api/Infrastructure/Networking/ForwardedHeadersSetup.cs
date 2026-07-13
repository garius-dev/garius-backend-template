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
                "Security:TrustedProxies está vazio em Production.\n\n" +

                "É a rede Docker do TRAEFIK (não a da Cloudflare — essa é outra camada, e já vem " +
                "ligada em Security:TrustCloudflareIps).\n\n" +

                "Pegue o valor no servidor:\n" +
                "    docker network inspect garius_network --format '{{(index .IPAM.Config 0).Subnet}}'\n" +
                "    -> 172.18.0.0/16\n\n" +

                "e grave no Secret Manager:\n" +
                "    \"Security:TrustedProxies:0\": \"172.18.0.0/16\"\n\n" +

                "POR QUE isto é obrigatório: o X-Forwarded-For é só um HEADER — quem faz a " +
                "requisição escreve o que quiser nele. Sem a lista, o ASP.NET aceita esse header de " +
                "QUALQUER origem, e o IP do cliente passa a ser o que o atacante mandar. O rate " +
                "limit deixa de limitar (basta variar o IP a cada tentativa e o brute force de senha " +
                "passa livre), a auditoria de LGPD registra o IP que ele escolheu, e o lockout não " +
                "vê padrão nenhum.\n\n" +

                "Com a lista, o header só é aceito quando o request chega DA rede do Traefik — que é " +
                "o único que fala com este container. De qualquer outro lugar ele é ignorado, e vale " +
                "o IP real da conexão.\n\n" +

                "NÃO 'resolva' deixando a lista vazia: no ASP.NET, KnownProxies/KnownNetworks vazios " +
                "significam CONFIAR EM QUALQUER UM. É por isso que a aplicação se recusa a subir " +
                "assim, em vez de aceitar em silêncio — um rate limit que não limita é pior que " +
                "nenhum, porque passa a impressão de que existe uma defesa.");
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
