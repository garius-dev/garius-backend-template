using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Garius.Api.Infrastructure.Authorization;

/// <summary>
/// Autenticação por cookie <c>HttpOnly</c>.
///
/// <para>
/// A topologia (front em <c>app.dominio.com</c>, API em <c>api.dominio.com</c> — mesmo apex)
/// torna isto <b>same-site</b>: o cookie usa <c>SameSite=Lax</c> e não entra na briga com o
/// ITP do Safari, que bloqueia cookie de terceiro. Se o frontend fosse para um domínio
/// realmente distinto (<c>*.pages.dev</c>), seria preciso <c>SameSite=None</c> — e aí o
/// modelo inteiro precisaria ser revisto.
/// </para>
///
/// <para>
/// Os <b>fluxos</b> (login em dois passos, refresh rotativo, CSRF) chegam na Fase 4c. Aqui
/// fica só o esquema, do qual a autorização depende.
/// </para>
/// </summary>
internal static class CookieAuthSetup
{
    internal const string SchemeName = CookieAuthenticationDefaults.AuthenticationScheme;

    /// <summary>
    /// <c>__Host-</c> força <c>Secure</c>, <c>Path=/</c> e <b>proíbe</b> o atributo
    /// <c>Domain</c> — o navegador rejeita o cookie se qualquer dessas condições falhar. É a
    /// proteção mais forte contra um subdomínio comprometido sobrescrever o cookie de sessão.
    /// </summary>
    internal const string CookieName = "__Host-garius.auth";

    /// <summary>O refresh token, num cookie separado e restrito à rota de refresh.</summary>
    internal const string RefreshCookieName = "__Host-garius.refresh";

    /// <summary>
    /// ⚠️ Em <b>desenvolvimento</b> o prefixo <c>__Host-</c> TEM de cair, e isto não é um
    /// relaxamento gratuito — é o que faz o login funcionar.
    ///
    /// <para>
    /// O prefixo <b>exige</b> a flag <c>Secure</c>. Em <c>http://localhost</c> não há HTTPS,
    /// então o cookie sai sem <c>Secure</c> — e o navegador <b>descarta em silêncio</b> um
    /// cookie <c>__Host-</c> sem ela. O resultado era um <b>loop de redirecionamento</b>: o
    /// login autenticava (o log dizia <c>login.success</c>!), o <c>Set-Cookie</c> ia na
    /// resposta, o navegador jogava fora, e <c>/scalar</c> devolvia para o login. Sem um único
    /// erro em lugar nenhum.
    /// </para>
    ///
    /// <para>
    /// Em <b>produção</b> o prefixo volta, e com ele toda a proteção: lá há HTTPS, e a política
    /// é <c>CookieSecurePolicy.Always</c>.
    /// </para>
    /// </summary>
    internal static string AuthCookie(IHostEnvironment environment) =>
        Strip(CookieName, environment);

    /// <inheritdoc cref="AuthCookie"/>
    internal static string RefreshCookie(IHostEnvironment environment) =>
        Strip(RefreshCookieName, environment);

    private static string Strip(string name, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return environment.IsDevelopment()
            ? name.Replace("__Host-", string.Empty, StringComparison.Ordinal)
            : name;
    }

    /// <summary>
    /// O <see cref="IHostEnvironment"/> a partir do request — para os endpoints, que leem o nome
    /// do cookie e não têm o ambiente injetado.
    /// </summary>
    internal static IHostEnvironment Environment(this HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        return http.RequestServices.GetRequiredService<IHostEnvironment>();
    }

    internal static IServiceCollection AddCookieAuthentication(
        this IServiceCollection services,
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        services
            .AddAuthentication(SchemeName)
            .AddCookie(SchemeName, options =>
            {
                // O cookie é o esquema PADRÃO — é o que atende o navegador, que não manda
                // header de autorização nenhum.
                //
                // Mas um request que TRAZ credencial de máquina (Authorization: Bearer ou
                // X-Api-Key) precisa ser encaminhado ao esquema dela. Sem este seletor, o
                // handler de cookie atenderia esses requests também — e, não achando cookie,
                // os trataria como ANÔNIMOS: um JWT perfeitamente válido resultaria num 401
                // sem nenhuma explicação visível.
                //
                // O ForwardDefaultSelector é POR ESQUEMA (não existe em AuthenticationOptions):
                // é o esquema padrão que delega para outro quando o request não é dele.
                // Ver MachineAuthSetup.SelectScheme.
                options.ForwardDefaultSelector = MachineAuthSetup.SelectScheme;

                options.Cookie.Name = AuthCookie(environment);

                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;

                // Secure SEMPRE, exceto em desenvolvimento (onde não há HTTPS).
                //
                // NÃO derivar de Request.IsHttps: atrás do Traefik o container recebe HTTP
                // puro, e o cookie sairia SEM a flag Secure em produção — foi exatamente o
                // que aconteceu no template anterior.
                options.Cookie.SecurePolicy = environment.IsDevelopment()
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;

                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = false;

                // Uma API JSON não redireciona para tela de login: devolve 401/403. Sem isto,
                // o ASP.NET responderia 302 para /Account/Login — que não existe — e o
                // frontend receberia um HTML onde esperava um erro.
                //
                // A EXCEÇÃO são as páginas administrativas (/jobs, /scalar), que são HTML
                // aberto no NAVEGADOR: ali um 401 sem corpo é uma tela em branco, e o
                // administrador não tem para onde ir.
                //
                // A distinção é pelo `Accept` do request, e não por uma lista de rotas: um
                // navegador pede text/html, um cliente de API pede application/json. Uma lista
                // de rotas seria mais uma coisa para alguém esquecer de atualizar ao criar a
                // próxima página admin — e o esquecimento apareceria como uma tela em branco
                // inexplicável, não como um erro.
                options.Events.OnRedirectToLogin = context =>
                {
                    if (WantsHtml(context.Request))
                    {
                        context.Response.Redirect(
                            $"/admin/login?returnUrl={Uri.EscapeDataString(context.Request.Path)}");

                        return Task.CompletedTask;
                    }

                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;

                    return Task.CompletedTask;
                };

                options.Events.OnRedirectToAccessDenied = context =>
                {
                    // AUTENTICADO, mas sem a permissão. Aqui NÃO se redireciona para o login:
                    // logar de novo não daria a permissão que falta, e o usuário ficaria num
                    // laço entre a página e o formulário sem entender por quê. O 403 é a
                    // resposta honesta.
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;

                    return Task.CompletedTask;
                };
            })
            // JWT (client credentials) e chave de API, no mesmo pipeline.
            .AddMachineAuthentication(configuration);

        return services;
    }

    /// <summary>
    /// O request veio de um <b>navegador</b> pedindo uma página, ou de um cliente de API?
    ///
    /// <para>
    /// Um navegador manda <c>Accept: text/html,...</c> ao navegar. Um <c>fetch</c> ou um cliente
    /// HTTP manda <c>application/json</c> (ou nada). É a distinção certa porque descreve
    /// <b>o que o chamador consegue consumir</b> — que é exatamente a pergunta que importa: um
    /// redirect só faz sentido para quem sabe segui-lo e renderizar o resultado.
    /// </para>
    /// </summary>
    private static bool WantsHtml(HttpRequest request) =>
        request.Headers.Accept.Any(value =>
            value?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);
}
