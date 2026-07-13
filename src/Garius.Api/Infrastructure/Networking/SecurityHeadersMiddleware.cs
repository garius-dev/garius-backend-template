using System.Security.Cryptography;

namespace Garius.Api.Infrastructure.Networking;

/// <summary>
/// Headers de segurança na resposta.
///
/// <para>
/// Esta é <b>quase toda</b> uma API JSON: para ela, o CSP certo é o mais fechado que existe
/// (<c>default-src 'none'</c>) — uma resposta JSON nunca carrega nada nem é enquadrada.
/// </para>
///
/// <para>
/// <b>Mas o template serve três páginas HTML</b> — <c>/admin/login</c>, <c>/scalar</c> e
/// <c>/jobs</c> —, e elas precisam do próprio CSS e (o Scalar e o Hangfire) do próprio
/// JavaScript. Com o <c>default-src 'none'</c> valendo para tudo, o navegador
/// <b>descartava o CSS delas em silêncio</b>: a página de login era servida como HTML cru, sem
/// estilo nenhum, e ninguém percebia — um CSP bloqueado não quebra a página, só a deixa feia.
/// </para>
///
/// <para>
/// <b>Nonce, e não <c>'unsafe-inline'</c>.</b> Seria uma linha a menos liberar o inline de vez —
/// e seria trocar a proteção por conveniência exatamente na origem que guarda o cookie de
/// sessão do administrador. Com o nonce, só o <c>&lt;style&gt;</c> que ESTA resposta emitiu
/// executa; um <c>&lt;script&gt;</c> injetado por um XSS não conhece o nonce (ele é aleatório a
/// cada requisição) e é bloqueado.
/// </para>
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    /// <summary>
    /// Onde a chave do nonce vive no <c>HttpContext</c>. A página o lê daqui para carimbá-lo no
    /// <c>&lt;style nonce="...">&gt;</c>.
    /// </summary>
    internal const string NonceKey = "csp-nonce";

    /// <summary>As páginas HTML do template. Todo o resto é JSON.</summary>
    private static readonly string[] HtmlPages = ["/admin", "/scalar", "/jobs"];

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

        // As páginas HTML precisam do próprio CSS/JS; a API não carrega nada.
        headers["Content-Security-Policy"] = IsHtmlPage(context.Request.Path)
            ? BuildPageCsp(context)
            : "default-src 'none'; frame-ancestors 'none'";

        // HSTS é responsabilidade do Traefik/Cloudflare (a borda TLS). Emitir daqui,
        // de dentro de um container que fala HTTP puro, seria enganoso.

        return next(context);
    }

    private static bool IsHtmlPage(PathString path) =>
        HtmlPages.Any(page => path.StartsWithSegments(page, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// O CSP das páginas administrativas. Continua fechado para o que importa —
    /// <c>default-src 'none'</c> e <c>frame-ancestors 'none'</c> seguem valendo —, e abre
    /// apenas o necessário para uma página funcionar.
    /// </summary>
    private static string BuildPageCsp(HttpContext context)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        context.Items[NonceKey] = nonce;

        return string.Join("; ",
            "default-src 'none'",

            // O CSS da própria página, carimbado com o nonce desta resposta.
            //
            // 'unsafe-inline' acompanha o nonce de propósito: é o FALLBACK para navegadores
            // antigos, que ignoram o nonce. Um navegador que ENTENDE nonce ignora o
            // 'unsafe-inline' quando há um nonce presente (é o que a spec manda) — então o
            // navegador moderno fica com a proteção, e o antigo, com a página funcionando.
            $"style-src 'self' 'nonce-{nonce}' 'unsafe-inline'",

            // O Scalar e o dashboard do Hangfire trazem o próprio JS.
            $"script-src 'self' 'nonce-{nonce}' 'unsafe-inline'",

            // SVG embutido (o logo) e os ícones do Hangfire.
            "img-src 'self' data:",
            "font-src 'self' data:",

            // O Scalar consulta o próprio /openapi.
            "connect-src 'self'",

            // O formulário de login só posta para a própria origem.
            "form-action 'self'",

            "base-uri 'none'",
            "frame-ancestors 'none'");
    }
}
