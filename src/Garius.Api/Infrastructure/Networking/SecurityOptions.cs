namespace Garius.Api.Infrastructure.Networking;

/// <summary>Configuração de rede/confiança. Seção <c>Security</c>.</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Proxies confiáveis (IPs ou CIDRs) — tipicamente a rede Docker onde o Traefik roda.
    /// Necessário para que o <c>UseForwardedHeaders</c> aceite o <c>X-Forwarded-Proto</c>
    /// e o <c>X-Forwarded-For</c>.
    ///
    /// Se ficar vazio em produção, o ASP.NET <b>ignora</b> os headers encaminhados e todos
    /// os requests aparecem com o IP do container do Traefik.
    /// </summary>
    public IList<string> TrustedProxies { get; } = [];

    /// <summary>
    /// Aceitar o header <c>CF-Connecting-IP</c> quando o request vier de um range da
    /// Cloudflare. Sempre validado contra <see cref="CloudflareIpRanges"/> — o header
    /// nunca é acreditado às cegas.
    /// </summary>
    public bool TrustCloudflareIps { get; set; } = true;

    /// <summary>
    /// Sobrescreve os ranges da Cloudflare. Vazio = usa a lista embutida, que é o normal.
    /// </summary>
    public IList<string> CloudflareIpRanges { get; } = [];
}
