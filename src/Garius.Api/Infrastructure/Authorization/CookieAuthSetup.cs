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

                options.Cookie.Name = CookieName;

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
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;

                    return Task.CompletedTask;
                };

                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;

                    return Task.CompletedTask;
                };
            })
            // JWT (client credentials) e chave de API, no mesmo pipeline.
            .AddMachineAuthentication(configuration);

        return services;
    }
}
