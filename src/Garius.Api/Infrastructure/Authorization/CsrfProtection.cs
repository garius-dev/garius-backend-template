using Garius.Api.Infrastructure.Errors;
using Garius.Core.Machine;
using Garius.Core.Results;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Garius.Api.Infrastructure.Authorization;

/// <summary>
/// Proteção contra CSRF, no padrão <b>double-submit</b>.
///
/// <para>
/// <b>Por que é necessária.</b> O cookie <c>HttpOnly</c> protege o token contra roubo por XSS —
/// mas cria o problema oposto: o navegador o envia <b>automaticamente</b> em toda requisição
/// para a API, inclusive numa disparada por um site malicioso. Sem CSRF, um formulário em
/// <c>site-do-mal.com</c> consegue fazer o navegador do usuário logado executar um
/// <c>POST /users/123/delete</c>.
/// </para>
///
/// <para>
/// <b>Como funciona:</b> o servidor envia um token num cookie <b>legível</b> pelo JavaScript; o
/// front o lê e o reenvia num <b>header</b>. Um site de terceiro consegue fazer o navegador
/// enviar o cookie, mas <b>não consegue ler o valor dele</b> (a same-origin policy o impede) —
/// e portanto não consegue montar o header.
/// </para>
///
/// <para>
/// <b>Bypass para máquina.</b> Requisições autenticadas por <c>Authorization: Bearer</c> (M2M)
/// ou <c>X-Api-Key</c> (terceiros) não precisam de CSRF: não há cookie ambiente que o navegador
/// envie sozinho, então não há o que explorar. Exigir o token delas só quebraria integrações.
/// </para>
///
/// <para>
/// O <c>SameSite=Lax</c> do cookie de sessão já mitiga muito disso nos navegadores modernos —
/// mas é uma única camada, e navegadores antigos e alguns fluxos a contornam. O template
/// anterior <b>configurava</b> o antiforgery e nunca o validava em lugar nenhum.
/// </para>
/// </summary>
internal static class CsrfProtection
{
    /// <summary>
    /// Cookie do antiforgery. Guarda o <b>segredo</b>; o valor que o front reenvia no header é
    /// outro (o <i>request token</i>), derivado dele. É o par que valida.
    /// </summary>
    internal const string CookieName = "__Host-garius.csrf";

    /// <summary>Header em que o front reenvia o <i>request token</i>.</summary>
    internal const string HeaderName = "X-CSRF-Token";

    /// <summary>
    /// Cookie <b>legível pelo JavaScript</b> com o request token — é este valor que o front lê
    /// e reenvia no header <see cref="HeaderName"/>.
    ///
    /// <para>
    /// O cookie do antiforgery (<see cref="CookieName"/>) guarda o segredo e é <c>HttpOnly</c>;
    /// o request token é derivado dele e pode ser público. Sem este segundo cookie, o front não
    /// teria como obter o token — a API não serve HTML, então não há como injetá-lo numa página.
    /// </para>
    /// </summary>
    internal const string RequestTokenCookieName = "garius.csrf-token";

    /// <summary>
    /// ⚠️ Em desenvolvimento o prefixo <c>__Host-</c> cai — ele exige <c>Secure</c>, e em
    /// <c>http://localhost</c> o navegador <b>descarta o cookie em silêncio</b>. Mesma armadilha
    /// do cookie de sessão; ver <see cref="CookieAuthSetup.AuthCookie"/>.
    /// </summary>
    internal static string CsrfCookie(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return environment.IsDevelopment()
            ? CookieName.Replace("__Host-", string.Empty, StringComparison.Ordinal)
            : CookieName;
    }

    internal static IServiceCollection AddCsrfProtection(
        this IServiceCollection services,
        IHostEnvironment environment)
    {
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = CsrfCookie(environment);

            // Este cookie guarda o SEGREDO do antiforgery — é HttpOnly. O valor que o front
            // reenvia é outro (o request token), publicado em RequestTokenCookieName.
            options.Cookie.HttpOnly = true;

            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;

            options.HeaderName = HeaderName;
        });

        return services;
    }

    /// <summary>
    /// Emite o par de CSRF: o cookie-segredo (HttpOnly, pelo próprio antiforgery) e um cookie
    /// <b>legível pelo JS</b> com o request token, que o front lê e reenvia no header.
    ///
    /// <para>
    /// Chamado no login. Sem isto, o front não teria como obter o token: esta API serve JSON,
    /// não HTML, então não há página onde injetá-lo.
    /// </para>
    /// </summary>
    internal static void IssueCsrfToken(this HttpContext context, IAntiforgery antiforgery)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(antiforgery);

        var environment = context.RequestServices.GetRequiredService<IHostEnvironment>();

        var tokens = antiforgery.GetAndStoreTokens(context);

        context.Response.Cookies.Append(
            RequestTokenCookieName,
            tokens.RequestToken!,
            new CookieOptions
            {
                // NÃO é HttpOnly: o front PRECISA lê-lo. É seguro — este valor não autentica
                // ninguém; ele só prova que quem o enviou consegue ler cookies da mesma origem,
                // o que um site de terceiro não consegue (same-origin policy).
                HttpOnly = false,

                // Secure sempre, exceto em desenvolvimento (HTTP puro — o navegador nem
                // reenviaria o cookie, e o CSRF nunca funcionaria localmente).
                Secure = !environment.IsDevelopment(),
                SameSite = SameSiteMode.Lax,
                Path = "/"
            });
    }

    /// <summary>
    /// Valida o token de CSRF em métodos que alteram estado, <b>quando a autenticação veio do
    /// cookie</b>.
    /// </summary>
    internal static void UseCsrfProtection(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Use(async (context, next) =>
        {
            if (RequiresCsrfValidation(context))
            {
                var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();

                try
                {
                    await antiforgery.ValidateRequestAsync(context);
                }
                catch (AntiforgeryValidationException)
                {
                    // Uma falha de CSRF é um evento de segurança: ou é um ataque, ou o front
                    // está mal configurado. Nos dois casos, tem de aparecer no log.
                    var logger = context.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Csrf");

                    logger.LogWarning(
                        "CSRF: token ausente ou inválido em {Method} {Path}",
                        context.Request.Method,
                        context.Request.Path);

                    // Escreve o PRÓPRIO ProblemDetails, com um código que diz a verdade.
                    //
                    // ⚠️ Antes, aqui só se punha o status 403 e se retornava. O corpo ficava
                    // vazio — e o AuthProblemDetailsMiddleware, que converte todo 401/403 sem
                    // corpo em ProblemDetails, o rotulava "auth.insufficient_permission":
                    // "Você não tem permissão para acessar este recurso."
                    //
                    // Uma falha de CSRF NÃO É falta de permissão, e a mensagem mandava o
                    // desenvolvedor caçar o bug no lugar errado — revisar permissões de um
                    // usuário que já tinha todas elas. Um erro que mente sobre a própria causa
                    // custa mais caro que o erro em si.
                    await WriteCsrfProblemAsync(context);

                    return;
                }
            }

            await next(context);
        });
    }

    /// <summary>
    /// Endpoints que <b>criam</b> a credencial, e por isso não podem exigir o token de CSRF.
    ///
    /// <para>
    /// ⚠️ Sem esta exceção, <c>/auth/login</c> fica <b>impossível de chamar</b> para quem já tem
    /// um cookie de sessão no navegador: o login é um POST, o cookie de sessão está lá, o
    /// antiforgery exige um header que o cliente ainda não tem motivo nenhum de mandar — e o
    /// login responde <b>403</b>. Pior: o <c>AuthProblemDetailsMiddleware</c> traduzia esse 403
    /// em "auth.insufficient_permission", e o erro passava a <b>acusar falta de permissão num
    /// endpoint anônimo</b>. Foi assim que este bug apareceu — um superadmin com a permissão
    /// <c>*</c> levando 403 ao tentar relogar, procurando o defeito na autorização, que estava
    /// perfeita.
    /// </para>
    ///
    /// <para>
    /// E não há o que um CSRF explore aqui: quem chama <c>/auth/login</c> tem de <b>provar a
    /// senha</b>, e quem chama <c>/auth/token</c> tem de <b>provar o client_secret</b>. Um
    /// atacante que já tivesse essas coisas não precisaria de CSRF nenhum. O pior que um CSRF
    /// contra o login consegue é logar a vítima <i>na conta do próprio atacante</i>.
    /// </para>
    ///
    /// <para>
    /// O <c>/auth/refresh</c> <b>também</b> entra, e a razão é diferente: quem barra o ataque
    /// ali é o <c>SameSite=Lax</c> do cookie, não o token. <c>Lax</c> impede o navegador de
    /// enviar o cookie num <b>POST cross-site</b> — e o refresh é POST. O site malicioso nem
    /// consegue fazer o cookie viajar, então não há o que o token de CSRF acrescente.
    /// </para>
    ///
    /// <para>
    /// E exigi-lo ali <b>custava</b>: o front precisava ter o token de CSRF em mãos <i>antes</i>
    /// de conseguir renovar a sessão — justamente na situação em que ele pode não tê-lo (o cookie
    /// de sessão dura 8h; o de CSRF é de sessão do navegador). Uma redundância que cria um
    /// impasse não é defesa em profundidade; é só o impasse.
    /// </para>
    ///
    /// <para>
    /// O <c>/logout</c> <b>continua exigindo</b> o token, de propósito: forçar o logout de alguém
    /// é um ataque real (irritante, não crítico), ele não prova credencial nenhuma, e — ao
    /// contrário do refresh — o front sempre tem o token quando chega lá (ele acabou de usar a
    /// sessão).
    /// </para>
    /// </summary>
    private static readonly string[] CredentialEstablishingPaths =
    [
        "/auth/login",
        "/auth/token",
        "/auth/refresh",
    ];

    /// <summary>
    /// O 403 do CSRF, em ProblemDetails — o mesmo contrato de todo erro da API, com um
    /// <c>code</c> que identifica a causa real.
    /// </summary>
    private static async Task WriteCsrfProblemAsync(HttpContext context)
    {
        var error = Error.Forbidden(
            "auth.csrf_token_invalid",
            $"Token de CSRF ausente ou inválido. Envie o valor do cookie " +
            $"'{RequestTokenCookieName}' no header '{HeaderName}'.");

        var problem = ProblemDetailsFactory.Create(error, context);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync<ProblemDetails>(problem);
    }

    private static bool RequiresCsrfValidation(HttpContext context)
    {
        // GET/HEAD/OPTIONS/TRACE não alteram estado.
        if (HttpMethods.IsGet(context.Request.Method)
            || HttpMethods.IsHead(context.Request.Method)
            || HttpMethods.IsOptions(context.Request.Method)
            || HttpMethods.IsTrace(context.Request.Method))
        {
            return false;
        }

        // Os endpoints que CRIAM a credencial, e onde se prova senha ou segredo.
        // Ver CredentialEstablishingPaths — e note que o /auth/refresh NÃO está lá.
        if (CredentialEstablishingPaths.Any(
                path => context.Request.Path.StartsWithSegments(
                    path, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Credencial de MÁQUINA (Bearer ou chave de API): dispensa CSRF.
        //
        // O CSRF existe porque o navegador envia o cookie SOZINHO, inclusive numa requisição
        // disparada por um site malicioso. Um header não tem esse problema: nenhum site de
        // terceiro consegue fazer o navegador da vítima enviar um `Authorization` ou um
        // `X-Api-Key` que ele não conhece. Exigir o token deles só quebraria integrações.
        //
        // Os DOIS headers são checados. A última linha (exigir o cookie de sessão) já cobriria
        // o caso comum de uma máquina — que não tem cookie nenhum —, mas não o de um request
        // que traga as duas coisas: um terceiro chamando a API do navegador de alguém que por
        // acaso tem sessão aberta seria barrado por um CSRF que não se aplica a ele.
        if (context.Request.Headers.ContainsKey("Authorization")
            || context.Request.Headers.ContainsKey(MachineAuth.ApiKeyHeader))
        {
            return false;
        }

        // Só faz sentido validar se HÁ um cookie de sessão para ser explorado.
        return context.Request.Cookies.ContainsKey(CookieAuthSetup.AuthCookie(context.Environment()));
    }
}
