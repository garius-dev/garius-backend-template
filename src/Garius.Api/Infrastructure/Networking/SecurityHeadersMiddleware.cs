namespace Garius.Api.Infrastructure.Networking;

/// <summary>
/// Headers de segurança na resposta.
///
/// Esta é uma API JSON, não um site: os headers relevantes são os que impedem que uma
/// resposta seja interpretada como outra coisa (sniffing), embutida em um frame, ou
/// vazada como referrer. Não há CSP de página aqui — o CSP do frontend é
/// responsabilidade do frontend (Cloudflare Pages).
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var headers = context.Response.Headers;

        // Não deixa o browser "adivinhar" o content-type (evita que um JSON com conteúdo
        // controlado pelo usuário seja tratado como HTML e execute script).
        headers["X-Content-Type-Options"] = "nosniff";

        // Uma API JSON nunca deve ser renderizada em frame.
        headers["X-Frame-Options"] = "DENY";

        // Não vaza a URL da API (com ids no path) para terceiros.
        headers["Referrer-Policy"] = "no-referrer";

        // Desliga APIs de browser que uma API JSON jamais precisa.
        headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=(), payment=()";

        // CSP mínima: uma resposta desta API nunca deve carregar nada nem ser enquadrada.
        headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";

        // HSTS é responsabilidade do Traefik/Cloudflare (a borda TLS). Emitir daqui,
        // de dentro de um container que fala HTTP puro, seria enganoso.

        return next(context);
    }
}
