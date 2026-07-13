using System.Net;
using Garius.Api.Features.Auth;
using Garius.Api.Infrastructure.Authorization;
using Garius.Api.Infrastructure.Networking;
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
                LoginPage(http, tokens.RequestToken!, SafeReturnUrl(returnUrl), error: null),
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
                    LoginPage(http, tokens.RequestToken!, returnUrl, result.Error!.Message),
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
                        http,
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
            var token = http.Request.Cookies[CookieAuthSetup.RefreshCookie(http.Environment())];

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
    /// dependência inteira a manter para sempre por causa de um punhado de HTML.
    ///
    /// <para>
    /// <b>Tudo é inline, e não é preguiça: é o CSP.</b> Os headers de segurança
    /// (<c>SecurityHeadersMiddleware</c>) proíbem origem externa — nada de CDN, de fonte do
    /// Google, de imagem remota. O logo é um SVG embutido no próprio HTML (vetorial, escala em
    /// qualquer tela, zero requisição) e a tipografia é a do sistema.
    /// </para>
    /// </summary>
    private static string LoginPage(HttpContext http, string csrfToken, string returnUrl, string? error)
    {
        // O nonce que o SecurityHeadersMiddleware pôs no CSP desta resposta. Sem carimbá-lo no
        // <style>, o navegador DESCARTA o CSS — e a página é servida sem estilo nenhum. Era
        // exatamente o que acontecia antes: o CSP era `default-src 'none'` para TUDO, e o
        // <style> desta página nunca chegou a valer.
        var nonce = WebUtility.HtmlEncode(http.Items[SecurityHeadersMiddleware.NonceKey] as string ?? string.Empty);

        // WebUtility.HtmlEncode em TUDO que vem de fora: o returnUrl e a mensagem de erro são
        // refletidos na página, e sem escape isto seria um XSS refletido — servido pela própria
        // API, na origem que guarda o cookie de sessão.
        var errorHtml = error is null
            ? string.Empty
            : $"""
               <p class="error" role="alert">
                 <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M8 1.5 15 14H1L8 1.5Z" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"/><path d="M8 6v3.2" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/><circle cx="8" cy="11.6" r=".85" fill="currentColor"/></svg>
                 <span>{WebUtility.HtmlEncode(error)}</span>
               </p>
               """;

        // $$""" (dois cifrões): dentro dele, a interpolação é {{...}} e uma chave SOLTA é
        // literal. É o que permite escrever CSS — cheio de { } — sem escapar cada chave.
        return $$"""
            <!doctype html>
            <html lang="pt-BR">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Acesso administrativo · GariusTech</title>
              <style nonce="{{nonce}}">
                /* Identidade GariusTech: o azul→roxo do símbolo. */
                :root {
                  --azul:       #3e60e8;
                  --azul-claro: #73c4fa;
                  --roxo:       #7e45f9;
                  --roxo-forte: #4014f9;

                  --fundo:      #0b0d12;
                  --superficie: #151922;
                  --borda:      #262c39;
                  --texto:      #e8eaf0;
                  --suave:      #98a2b8;

                  --marca: linear-gradient(120deg, var(--azul) 0%, var(--azul-claro) 45%, var(--roxo) 100%);
                }

                * { box-sizing: border-box; }

                body {
                  margin: 0;
                  min-height: 100vh;
                  display: grid;
                  place-items: center;
                  padding: 1.5rem;
                  font-family: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
                  color: var(--texto);
                  background: var(--fundo);
                }

                /* O brilho de marca ao fundo — os mesmos tons do símbolo, bem diluídos. */
                body::before {
                  content: "";
                  position: fixed;
                  inset: 0;
                  background:
                    radial-gradient(46rem 30rem at 12% -10%, rgb(62 96 232 / .20), transparent 60%),
                    radial-gradient(38rem 26rem at 88% 108%, rgb(126 69 249 / .18), transparent 60%);
                  pointer-events: none;
                }

                .cartao {
                  position: relative;
                  width: min(400px, 100%);
                  padding: 2.5rem 2.25rem 2.25rem;
                  border: 1px solid var(--borda);
                  border-radius: 16px;
                  background: var(--superficie);
                  box-shadow: 0 24px 60px rgb(0 0 0 / .45);
                }

                /* O fio de gradiente no topo do cartão: a assinatura da marca. */
                .cartao::before {
                  content: "";
                  position: absolute;
                  inset: 0 0 auto;
                  height: 3px;
                  border-radius: 16px 16px 0 0;
                  background: var(--marca);
                }

                .marca { display: grid; justify-items: center; gap: .9rem; margin-bottom: 2rem; }
                .marca svg { width: 52px; height: 52px; display: block; }

                .nome { font-size: 1.35rem; letter-spacing: -.02em; }
                .nome b      { font-weight: 600; }
                .nome span   { font-weight: 200; color: var(--suave); }

                h1 {
                  margin: 0;
                  font-size: .78rem;
                  font-weight: 500;
                  letter-spacing: .09em;
                  text-transform: uppercase;
                  color: var(--suave);
                }

                label {
                  display: block;
                  margin-bottom: .4rem;
                  font-size: .8rem;
                  font-weight: 500;
                  color: var(--suave);
                }

                input {
                  width: 100%;
                  padding: .8rem .9rem;
                  margin-bottom: 1.15rem;
                  font: inherit;
                  color: var(--texto);
                  background: var(--fundo);
                  border: 1px solid var(--borda);
                  border-radius: 9px;
                  transition: border-color .15s, box-shadow .15s;
                }
                input::placeholder { color: #5a6478; }
                input:focus {
                  outline: none;
                  border-color: var(--azul-claro);
                  box-shadow: 0 0 0 3px rgb(115 196 250 / .16);
                }

                button {
                  width: 100%;
                  margin-top: .4rem;
                  padding: .85rem;
                  font: inherit;
                  font-weight: 600;
                  color: #fff;
                  background: var(--marca);
                  background-size: 150% 100%;
                  border: 0;
                  border-radius: 9px;
                  cursor: pointer;
                  transition: background-position .35s, transform .06s, box-shadow .2s;
                }
                button:hover {
                  background-position: 100% 0;
                  box-shadow: 0 8px 24px rgb(64 20 249 / .32);
                }
                button:active { transform: translateY(1px); }
                button:focus-visible { outline: 2px solid var(--azul-claro); outline-offset: 2px; }

                .error {
                  display: flex;
                  gap: .6rem;
                  align-items: center;
                  margin: 0 0 1.35rem;
                  padding: .75rem .85rem;
                  font-size: .85rem;
                  color: #ffb4b4;
                  background: rgb(239 68 68 / .10);
                  border: 1px solid rgb(239 68 68 / .35);
                  border-radius: 9px;
                }
                .error svg { flex: none; width: 16px; height: 16px; }

                /* Os quatro pontos da marca. */
                .pontos { display: flex; justify-content: center; gap: .45rem; margin-top: 1.75rem; }
                .pontos i { width: 6px; height: 6px; border-radius: 50%; }
                .pontos i:nth-child(1) { background: var(--azul); }
                .pontos i:nth-child(2) { background: var(--roxo); }
                .pontos i:nth-child(3) { background: var(--azul-claro); }
                .pontos i:nth-child(4) { background: var(--roxo-forte); }

                /* Respeita quem pediu menos movimento. */
                @media (prefers-reduced-motion: reduce) {
                  * { transition: none !important; }
                }
              </style>
            </head>
            <body>
              <main class="cartao">
                <div class="marca">
                  <!-- O símbolo, embutido: um CDN violaria o CSP, e um <img> externo seria uma requisição a mais. -->
                  <svg viewBox="0 0 640 640" role="img" aria-label="GariusTech">
                    <defs>
                      <linearGradient id="g1" x1="88" y1="350" x2="229" y2="104" gradientUnits="userSpaceOnUse">
                        <stop offset="0" stop-color="#3e60e8"/><stop offset="1" stop-color="#73c4fa"/>
                      </linearGradient>
                      <linearGradient id="g2" x1="111" y1="389" x2="275" y2="104" gradientUnits="userSpaceOnUse">
                        <stop offset="0" stop-color="#2a35e3"/><stop offset="1" stop-color="#67abf6"/>
                      </linearGradient>
                      <linearGradient id="g3" x1="111" y1="251" x2="275" y2="536" gradientUnits="userSpaceOnUse">
                        <stop offset="0" stop-color="#67abf6"/><stop offset="1" stop-color="#2a35e3"/>
                      </linearGradient>
                      <linearGradient id="g4" x1="88" y1="290" x2="229" y2="536" gradientUnits="userSpaceOnUse">
                        <stop offset="0" stop-color="#73c4fa"/><stop offset="1" stop-color="#3e60e8"/>
                      </linearGradient>
                      <linearGradient id="g5" x1="411" y1="536" x2="552" y2="290" gradientUnits="userSpaceOnUse">
                        <stop offset="0" stop-color="#73c4fa"/><stop offset="1" stop-color="#3e60e8"/>
                      </linearGradient>
                      <linearGradient id="g6" x1="365" y1="536" x2="529" y2="251" gradientUnits="userSpaceOnUse">
                        <stop offset="0" stop-color="#67abf6"/><stop offset="1" stop-color="#2a35e3"/>
                      </linearGradient>
                      <linearGradient id="g7" x1="365" y1="104" x2="529" y2="389" gradientUnits="userSpaceOnUse">
                        <stop offset="0" stop-color="#2a35e3"/><stop offset="1" stop-color="#67abf6"/>
                      </linearGradient>
                      <linearGradient id="g8" x1="411" y1="104" x2="552" y2="350" gradientUnits="userSpaceOnUse">
                        <stop offset="0" stop-color="#3e60e8"/><stop offset="1" stop-color="#73c4fa"/>
                      </linearGradient>
                      <linearGradient id="g9" x1="237" y1="536" x2="401" y2="251" gradientUnits="userSpaceOnUse">
                        <stop offset="0" stop-color="#4014f9"/><stop offset="1" stop-color="#854cf0"/>
                      </linearGradient>
                      <linearGradient id="g10" x1="282" y1="536" x2="424" y2="290" gradientUnits="userSpaceOnUse">
                        <stop offset="0" stop-color="#7e45f9"/><stop offset="1" stop-color="#9559f9"/>
                      </linearGradient>
                      <linearGradient id="g11" x1="239" y1="389" x2="403" y2="104" gradientUnits="userSpaceOnUse">
                        <stop offset="0" stop-color="#4014f9"/><stop offset="1" stop-color="#854cf0"/>
                      </linearGradient>
                      <linearGradient id="g12" x1="216" y1="350" x2="358" y2="104" gradientUnits="userSpaceOnUse">
                        <stop offset="0" stop-color="#7e45f9"/><stop offset="1" stop-color="#9559f9"/>
                      </linearGradient>
                    </defs>
                    <path fill="url(#g1)" d="M246.49,113.99l-141.64,245.35-13.35-23.12c-5.8-10.04-5.8-22.4,0-32.44l109.57-189.79h45.43Z"/>
                    <polygon fill="url(#g2)" points="291.91 113.99 127.55 398.69 104.84 359.34 246.49 113.99 291.91 113.99"/>
                    <polygon fill="url(#g3)" points="291.91 526 246.49 526 104.84 280.65 127.55 241.31 291.91 526"/>
                    <path fill="url(#g4)" d="M246.49,526h-45.43l-109.57-189.79c-5.79-10.04-5.79-22.4,0-32.44l13.35-23.12,141.64,245.35Z"/>
                    <path fill="url(#g5)" d="M393.51,526.01l141.64-245.35,13.35,23.12c5.8,10.04,5.8,22.4,0,32.44l-109.57,189.79h-45.43Z"/>
                    <polygon fill="url(#g6)" points="348.09 526.01 512.45 241.31 535.16 280.66 393.51 526.01 348.09 526.01"/>
                    <polygon fill="url(#g7)" points="348.09 114 393.51 114 535.16 359.35 512.45 398.69 348.09 114"/>
                    <path fill="url(#g8)" d="M393.51,114h45.43l109.57,189.79c5.79,10.04,5.79,22.4,0,32.44l-13.35,23.12-141.64-245.35Z"/>
                    <polygon fill="url(#g9)" points="407.05 280.65 265.41 526 219.98 526 384.35 241.31 407.05 280.65"/>
                    <polygon fill="url(#g10)" points="429.77 320 310.84 526 265.41 526 407.05 280.65 429.77 320"/>
                    <polygon fill="url(#g11)" points="232.95 359.35 374.59 114 420.02 114 255.65 398.69 232.95 359.35"/>
                    <polygon fill="url(#g12)" points="210.23 320 329.16 114 374.59 114 232.95 359.35 210.23 320"/>
                  </svg>

                  <div class="nome"><b>Garius</b><span>Tech</span></div>
                  <h1>Acesso administrativo</h1>
                </div>

                <form method="post" action="/admin/login">
                  {{errorHtml}}

                  <label for="email">E-mail</label>
                  <input id="email" name="email" type="email" required
                         autocomplete="username" autofocus placeholder="voce@empresa.com">

                  <label for="password">Senha</label>
                  <input id="password" name="password" type="password" required
                         autocomplete="current-password" placeholder="••••••••••••">

                  <input type="hidden" name="returnUrl" value="{{WebUtility.HtmlEncode(returnUrl)}}">
                  <input type="hidden" name="__RequestVerificationToken" value="{{WebUtility.HtmlEncode(csrfToken)}}">

                  <button type="submit">Entrar</button>
                </form>

                <div class="pontos" aria-hidden="true"><i></i><i></i><i></i><i></i></div>
              </main>
            </body>
            </html>
            """;
    }
}
