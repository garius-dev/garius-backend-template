using System.Net;

namespace Garius.Api.Infrastructure.Networking;

/// <summary>
/// Ranges oficiais da Cloudflare (https://www.cloudflare.com/ips/).
///
/// Servem para responder a uma única pergunta: <b>este request veio mesmo da Cloudflare?</b>
/// Só nesse caso o header <c>CF-Connecting-IP</c> pode ser acreditado — do contrário
/// qualquer cliente forjaria o header e burlaria rate limit, lockout e auditoria.
///
/// A lista muda raramente. Se mudar, é possível sobrescrevê-la por configuração em
/// <c>Security:CloudflareIpRanges</c> sem alterar código.
/// </summary>
internal static class CloudflareIpRanges
{
    /// <summary>Atualizado em 2026-07. Fonte: https://www.cloudflare.com/ips/</summary>
    internal static readonly string[] Default =
    [
        // IPv4
        "173.245.48.0/20",
        "103.21.244.0/22",
        "103.22.200.0/22",
        "103.31.4.0/22",
        "141.101.64.0/18",
        "108.162.192.0/18",
        "190.93.240.0/20",
        "188.114.96.0/20",
        "197.234.240.0/22",
        "198.41.128.0/17",
        "162.158.0.0/15",
        "104.16.0.0/13",
        "104.24.0.0/14",
        "172.64.0.0/13",
        "131.0.72.0/22",

        // IPv6
        "2400:cb00::/32",
        "2606:4700::/32",
        "2803:f800::/32",
        "2405:b500::/32",
        "2405:8100::/32",
        "2a06:98c0::/29",
        "2c0f:f248::/32"
    ];

    internal static IReadOnlyList<IPNetwork> Parse(IEnumerable<string> cidrs)
    {
        ArgumentNullException.ThrowIfNull(cidrs);

        var networks = new List<IPNetwork>();

        foreach (var cidr in cidrs)
        {
            if (IPNetwork.TryParse(cidr, out var network))
            {
                networks.Add(network);
            }
            else
            {
                throw new InvalidOperationException(
                    $"CIDR inválido na configuração de ranges da Cloudflare: '{cidr}'.");
            }
        }

        return networks;
    }
}
