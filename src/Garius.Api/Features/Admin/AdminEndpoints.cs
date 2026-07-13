using System.Net;
using Garius.Api.Features.Auth;
using Garius.Api.Infrastructure.Authorization;
using Microsoft.AspNetCore.Antiforgery;

namespace Garius.Api.Features.Admin;

/// <summary>
/// A porta de entrada das páginas administrativas — <b>o painel de jobs</b> (<c>/jobs</c>) e a
/// <b>documentação</b> (<c>/scalar</c>).
///
/// <para>
/// <b>Por que isto existe.</b> As duas são páginas HTML, abertas no <b>navegador</b>. A
/// autenticação da aplicação é por cookie — o que funciona — mas o cookie só é emitido pelo
/// <c>POST /auth/login</c>, que é uma chamada de <i>API</i>: para abrir <c>/jobs</c> em produção
/// seria preciso logar no Postman, copiar o cookie e injetá-lo à mão no navegador. Funciona, e é
/// exatamente o tipo de atrito que faz alguém abrir o dashboard "temporariamente" e nunca mais
/// fechar. A página de login remove a desculpa.
/// </para>
///
/// <para>
/// <b>Ela não inventa autenticação nenhuma.</b> Chama o mesmo <see cref="AuthService"/> do
/// <c>/auth/login</c> — e portanto herda o lockout por conta, o rate limit por IP, a checagem de
/// tenant e o mesmo cookie <c>HttpOnly</c>. Um segundo caminho de login seria um segundo lugar
/// para esquecer uma defesa.
/// </para>
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/admin").ExcludeFromDescription();

        group.MapGet("/login", (HttpContext http, IAntiforgery antiforgery, string? returnUrl) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(http);

            return Results.Content(
                LoginPage(tokens.RequestToken!, SafeReturnUrl(returnUrl), error: null),
                "text/html; charset=utf-8");
        })
        .AllowAnonymous();

        group.MapPost("/login", async (
            HttpContext http,
            AuthService auth,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            var form = await http.Request.ReadFormAsync(ct);

            var email = form["email"].ToString();
            var password = form["password"].ToString();
            var returnUrl = SafeReturnUrl(form["returnUrl"].ToString());

            // O MESMO serviço do /auth/login: lockout, rate limit e cookie idênticos.
            var result = await auth.LoginAsync(new LoginRequest(email, password), ct);

            if (!result.IsSuccess)
            {
                var tokens = antiforgery.GetAndStoreTokens(http);

                // A mensagem vem do AuthService, que já é deliberadamente genérica ("e-mail ou
                // senha inválidos") — não vaza se a conta existe. Ver AuthService.LoginAsync.
                return Results.Content(
                    LoginPage(tokens.RequestToken!, returnUrl, result.Error!.Message),
                    "text/html; charset=utf-8",
                    statusCode: (int)HttpStatusCode.Unauthorized);
            }

            // Um usuário com VÁRIOS tenants recebe um cookie parcial (sem a claim de tenant) e
            // teria de escolher um — o que as páginas admin não sabem fazer. Como jobs.read e
            // docs.read são permissões de operação (não de negócio), o caminho honesto é dizer
            // isso em vez de deixá-lo com uma sessão que não abre nada.
            if (!result.Value.Authenticated)
            {
                var tokens = antiforgery.GetAndStoreTokens(http);

                return Results.Content(
                    LoginPage(
                        tokens.RequestToken!,
                        returnUrl,
                        "Sua conta pertence a mais de uma organização. Entre pelo aplicativo " +
                        "e selecione uma antes de acessar as páginas administrativas."),
                    "text/html; charset=utf-8",
                    statusCode: (int)HttpStatusCode.Conflict);
            }

            http.IssueCsrfToken(antiforgery);

            return Results.Redirect(returnUrl);
        })
        .AllowAnonymous();

        group.MapPost("/logout", async (HttpContext http, AuthService auth, CancellationToken ct) =>
        {
            var token = http.Request.Cookies[CookieAuthSetup.RefreshCookieName];

            await auth.LogoutAsync(token, ct);

            return Results.Redirect("/admin/login");
        })
        .RequireAuthorization(AuthorizationSetup.AuthenticatedWithoutTenantPolicy);
    }

    /// <summary>
    /// Só permite voltar para um caminho <b>desta</b> aplicação.
    ///
    /// <para>
    /// ⚠️ Sem esta checagem o parâmetro vira um <b>open redirect</b>: um atacante manda
    /// <c>/admin/login?returnUrl=https://site-do-mal.com</c>, a vítima vê um domínio legítimo,
    /// faz login de verdade — e é jogada num clone que pede a senha "de novo". O link parece
    /// confiável porque <i>é</i> confiável: só o destino não é.
    /// </para>
    ///
    /// <para>
    /// A regra é conservadora de propósito: tem de começar com uma única <c>/</c>. Isso rejeita
    /// <c>//site-do-mal.com</c> (que o navegador trata como URL absoluta, herdando o esquema) e
    /// <c>https://...</c> — os dois contornos clássicos de uma validação ingênua.
    /// </para>
    /// </summary>
    private static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)
            || !returnUrl.StartsWith('/')
            || returnUrl.StartsWith("//", StringComparison.Ordinal))
        {
            return "/jobs";
        }

        return returnUrl;
    }

    /// <summary>
    /// A página, inline. Sem framework de view, sem arquivo estático, sem build de front — é um
    /// formulário com dois campos, e trazer Razor (ou um pipeline de assets) para isto seria uma
    /// dependência inteira a manter para sempre por causa de 40 linhas de HTML.
    /// </summary>
    private static string LoginPage(string csrfToken, string returnUrl, string? error)
    {
        // WebUtility.HtmlEncode em TUDO que vem de fora: o returnUrl e a mensagem de erro são
        // refletidos na página, e sem escape isto seria um XSS refletido — servido pela própria
        // API, na origem que guarda o cookie de sessão.
        var errorHtml = error is null
            ? string.Empty
            : $"""<p class="error">{WebUtility.HtmlEncode(error)}</p>""";

        // $$""" (dois cifrões): dentro dele, a interpolação é {{...}} e uma chave SOLTA é
        // literal. É o que permite escrever CSS — cheio de { } — sem escapar cada chave.
        return $$"""
            <!doctype html>
            <html lang="pt-BR">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Acesso administrativo</title>
              <style>
                :root { color-scheme: light dark; }
                body {
                  font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
                  display: grid; place-items: center; min-height: 100vh; margin: 0;
                  background: #0f1115; color: #e6e6e6;
                }
                form {
                  background: #171a21; padding: 2rem; border-radius: 12px;
                  width: min(360px, 90vw); box-shadow: 0 8px 32px rgb(0 0 0 / .4);
                }
                h1 { font-size: 1.1rem; margin: 0 0 1.5rem; font-weight: 600; }
                label { display: block; font-size: .8rem; margin-bottom: .35rem; color: #9aa4b2; }
                input {
                  width: 100%; padding: .65rem .75rem; margin-bottom: 1rem;
                  border: 1px solid #2a2f3a; border-radius: 6px;
                  background: #0f1115; color: #e6e6e6; box-sizing: border-box;
                }
                input:focus { outline: 2px solid #3b82f6; outline-offset: -1px; }
                button {
                  width: 100%; padding: .7rem; border: 0; border-radius: 6px;
                  background: #3b82f6; color: #fff; font-weight: 600; cursor: pointer;
                }
                button:hover { background: #2563eb; }
                .error {
                  background: #3b1418; border: 1px solid #7f1d1d; color: #fca5a5;
                  padding: .6rem .75rem; border-radius: 6px; font-size: .85rem; margin: 0 0 1rem;
                }
              </style>
            </head>
            <body>
              <form method="post" action="/admin/login">
                <h1>Acesso administrativo</h1>
                {{errorHtml}}
                <label for="email">E-mail</label>
                <input id="email" name="email" type="email" required autocomplete="username" autofocus>

                <label for="password">Senha</label>
                <input id="password" name="password" type="password" required autocomplete="current-password">

                <input type="hidden" name="returnUrl" value="{{WebUtility.HtmlEncode(returnUrl)}}">
                <input type="hidden" name="__RequestVerificationToken" value="{{WebUtility.HtmlEncode(csrfToken)}}">

                <button type="submit">Entrar</button>
              </form>
            </body>
            </html>
            """;
    }
}
