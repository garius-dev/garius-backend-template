namespace Garius.Api.Infrastructure.Networking;

/// <summary>
/// Limites do servidor.
///
/// <para>
/// Enforçados <b>pelo Kestrel</b>, não por um middleware que lê <c>Content-Length</c>.
/// O template anterior validava o header — que o cliente envia. Bastava omitir o
/// <c>Content-Length</c> e usar <c>Transfer-Encoding: chunked</c> para passar direto pela
/// validação e streamar o corpo inteiro. Aqui o limite é do servidor, e não há como burlar.
/// </para>
/// </summary>
internal static class KestrelSetup
{
    internal static void ConfigureKestrelLimits(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WebHost.ConfigureKestrel(options =>
        {
            // 10 MB. Um upload maior que isto deve ir para storage (GCS/S3) via URL
            // assinada, não trafegar pela API.
            options.Limits.MaxRequestBodySize = 10 * 1024 * 1024;

            options.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
            options.Limits.MaxRequestLineSize = 8 * 1024;

            // Timeouts curtos: atrás do Traefik, conexão ociosa é recurso preso à toa
            // e é o vetor de um slowloris.
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(20);

            // Não anuncia "Server: Kestrel" — não entrega a stack de graça.
            options.AddServerHeader = false;
        });
    }
}
