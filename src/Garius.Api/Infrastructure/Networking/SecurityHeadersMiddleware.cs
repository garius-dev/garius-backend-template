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
/// <b>São TRÊS CSPs</b>, um por natureza de resposta — e a distinção não é preciosismo:
/// </para>
///
/// <list type="bullet">
///   <item><b>API</b> (o normal): <c>default-src 'none'</c>. Uma resposta JSON não carrega nada.</item>
///   <item><b>Nossa página</b> (o login): <b>nonce</b>. Nós escrevemos o HTML, então o nonce
///         funciona — e é a página que recebe senha e guarda o cookie de sessão.</item>
///   <item><b>Páginas de terceiros</b> (Scalar, Hangfire): <c>'unsafe-inline'</c>. Elas injetam
///         CSS e JS <b>em runtime, por JavaScript</b>, e um nó criado assim não carrega nonce.</item>
/// </list>
///
/// <para>
/// ⚠️ <b>Nonce e <c>'unsafe-inline'</c> NUNCA na mesma diretiva.</b> Pela spec, o navegador
/// <b>ignora</b> o <c>'unsafe-inline'</c> quando há um nonce. Não é fallback — é anulação. Mandar
/// os dois deixou a página do Scalar <b>em branco</b>.
/// </para>
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    /// <summary>
    /// Onde a chave do nonce vive no <c>HttpContext</c>. A página o lê daqui para carimbá-lo no
    /// <c>&lt;style nonce="...">&gt;</c>.
    /// </summary>
    internal const string NonceKey = "csp-nonce";

    /// <summary>
    /// Páginas HTML de <b>terceiros</b> — o Scalar e o dashboard do Hangfire.
    ///
    /// <para>
    /// Elas montam a própria interface <b>em runtime, por JavaScript</b>: criam
    /// <c>&lt;style&gt;</c> e <c>&lt;script&gt;</c> depois que a página carregou. Um nó criado
    /// assim <b>não tem como carregar o nonce</b> — o nonce é um atributo do HTML que o
    /// servidor emitiu, e esse HTML não existe.
    /// </para>
    ///
    /// <para>
    /// ⚠️ E <b>nonce e <c>'unsafe-inline'</c> não convivem</b>: pela spec, o navegador
    /// <b>IGNORA</b> o <c>'unsafe-inline'</c> quando há um nonce na diretiva. Não é fallback —
    /// é anulação. Mandar os dois deixa a página do Scalar <b>em branco</b>, com o console
    /// cuspindo "Applying inline style violates the following CSP directive".
    /// </para>
    ///
    /// <para>
    /// Para elas, então, <c>'unsafe-inline'</c> <b>sozinho</b>. É uma concessão consciente, e o
    /// que a limita é que estas páginas <b>não têm entrada de usuário</b>: renderizam um
    /// OpenAPI e uma fila de jobs, ambos nossos. O vetor clássico do <c>'unsafe-inline'</c> —
    /// refletir texto do atacante como HTML — não existe aqui. Some-se a isso que as duas
    /// exigem permissão (<c>docs.read</c> / <c>jobs.read</c>) e nunca são anônimas.
    /// </para>
    /// </summary>
    private static readonly string[] ThirdPartyPages = ["/scalar", "/jobs"];

    /// <summary>
    /// <b>Nossas</b> páginas. Aqui o HTML é escrito por nós, o <c>&lt;style&gt;</c> é estático,
    /// e o nonce funciona — então ele é usado, e <c>'unsafe-inline'</c> fica de fora.
    ///
    /// <para>
    /// É a página que <b>tem</b> entrada de usuário (e-mail, senha, <c>returnUrl</c>) e a que
    /// guarda o cookie de sessão do administrador. É exatamente onde o nonce vale a pena.
    /// </para>
    /// </summary>
    private static readonly string[] OwnPages = ["/admin"];

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

        // Três CSPs, um por natureza de resposta:
        //
        //   API (o normal)   -> default-src 'none'. Uma resposta JSON não carrega nada.
        //   Nossas páginas   -> NONCE. Nós escrevemos o HTML, então o nonce funciona.
        //   Páginas de fora  -> 'unsafe-inline'. O Scalar e o Hangfire injetam CSS/JS por
        //                       JavaScript em runtime, e um nó criado assim NÃO carrega nonce.
        headers["Content-Security-Policy"] = Matches(context.Request.Path, OwnPages)
            ? OwnPageCsp(context)
            : Matches(context.Request.Path, ThirdPartyPages)
                ? ThirdPartyPageCsp
                : "default-src 'none'; frame-ancestors 'none'";

        // HSTS é responsabilidade do Traefik/Cloudflare (a borda TLS). Emitir daqui,
        // de dentro de um container que fala HTTP puro, seria enganoso.

        return next(context);
    }

    private static bool Matches(PathString path, string[] pages) =>
        pages.Any(page => path.StartsWithSegments(page, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// O CSP da <b>nossa</b> página (o login). Nós escrevemos o HTML, o <c>&lt;style&gt;</c> é
    /// estático — então o <b>nonce</b> funciona, e <c>'unsafe-inline'</c> fica FORA.
    ///
    /// <para>
    /// Sem <c>'unsafe-inline'</c> de propósito: ele anularia o nonce (a spec manda o navegador
    /// ignorá-lo quando há nonce) e devolveria a página ao XSS. Esta é a página que recebe
    /// e-mail, senha e <c>returnUrl</c>, e que guarda o cookie de sessão do administrador — é
    /// onde a proteção mais importa.
    /// </para>
    /// </summary>
    private static string OwnPageCsp(HttpContext context)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        context.Items[NonceKey] = nonce;

        return string.Join("; ",
            "default-src 'none'",

            // Só o <style> que ESTA resposta emitiu. Um <style> ou <script> injetado por um XSS
            // não conhece o nonce (ele é novo a cada requisição) e é bloqueado.
            $"style-src 'nonce-{nonce}'",

            // A página de login não tem JavaScript nenhum. Nem 'self': não há o que carregar.
            "script-src 'none'",

            // O logo, que é um SVG embutido no HTML.
            "img-src 'self' data:",

            // O formulário só posta para a própria origem — a senha não pode sair para fora.
            "form-action 'self'",

            "base-uri 'none'",
            "frame-ancestors 'none'");
    }

    /// <summary>
    /// O CSP do <b>Scalar</b> e do <b>dashboard do Hangfire</b> — páginas de terceiros, que
    /// montam a interface em runtime por JavaScript.
    ///
    /// <para>
    /// <c>'unsafe-inline'</c> <b>sozinho</b>, sem nonce. Não é desleixo, é a única opção que
    /// funciona: um <c>&lt;style&gt;</c> criado por <c>document.createElement</c> não carrega
    /// nonce, e a spec manda o navegador <b>ignorar</b> o <c>'unsafe-inline'</c> se houver um
    /// nonce na diretiva. Os dois juntos = página em branco.
    /// </para>
    ///
    /// <para>
    /// O que torna a concessão aceitável: estas páginas <b>não refletem entrada de usuário</b>
    /// (renderizam o nosso OpenAPI e a nossa fila de jobs) e <b>exigem permissão</b>
    /// (<c>docs.read</c> / <c>jobs.read</c>) — nunca são servidas a um anônimo.
    /// </para>
    /// </summary>
    private const string ThirdPartyPageCsp =
        "default-src 'none'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +   // o Scalar busca o próprio /openapi
        "form-action 'self'; " +
        "base-uri 'none'; " +
        "frame-ancestors 'none'";
}
