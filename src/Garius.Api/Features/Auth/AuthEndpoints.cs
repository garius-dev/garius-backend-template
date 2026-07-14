using System.Security.Claims;
using Garius.Api.Infrastructure.Authorization;
using Garius.Api.Infrastructure.Errors;
using Garius.Api.Infrastructure.Validation;
using Garius.Core.Authorization;
using Garius.Core.Results;
using Microsoft.AspNetCore.Antiforgery;

namespace Garius.Api.Features.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // ValidateRequests: todo endpoint DESTE GRUPO cujo request tenha um
        // AbstractValidator<T> registrado é validado antes do handler — sem uma chamada por
        // endpoint para alguém esquecer. Um endpoint novo aqui dentro nasce coberto.
        // Ver ValidationSetup e AuthValidators.
        var group = app.MapGroup("/auth").WithTags("Auth").ValidateRequests();

        // --- públicos ---

        group.MapPost("/login", async (
            LoginRequest request,
            AuthService auth,
            HttpContext http,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            var result = await auth.LoginAsync(request, ct);

            if (result.IsSuccess)
            {
                // Emite o par de CSRF. O front lê o cookie `garius.csrf-token` e passa a enviar
                // o valor no header X-CSRF-Token em toda requisição que altera estado.
                http.IssueCsrfToken(antiforgery);
            }

            return result.ToHttpResult(http);
        })
        .AllowAnonymous()
        .WithSummary("Autentica com e-mail e senha.")
        .WithDescription(
            "Com um único tenant, a sessão é criada direto (authenticated: true). " +
            "Com vários, devolve a lista de tenants (authenticated: false) e a sessão só nasce " +
            "em /auth/select-tenant.");

        group.MapPost("/select-tenant", async (
            SelectTenantRequest request,
            AuthService auth,
            HttpContext http,
            CancellationToken ct) =>
        {
            // Nesta etapa o usuário JÁ provou a senha, mas ainda não tem cookie de sessão —
            // a identidade vem do resultado do login, que o front devolve.
            var userId = GetUserId(http);

            return userId is null
                ? Result<LoginResponse>.Failure(Error.Unauthorized()).ToHttpResult(http)
                : (await auth.SelectTenantAsync(userId.Value, request.TenantId, ct)).ToHttpResult(http);
        })
        // Exige autenticação, mas NÃO exige tenant — é justamente o endpoint que o define.
        // Sem isto, a FallbackPolicy (que exige a claim de tenant) o tornaria inalcançável:
        // o usuário nunca conseguiria escolher um tenant, porque não tem tenant ainda.
        .RequireAuthorization(AuthorizationSetup.AuthenticatedWithoutTenantPolicy)
        .WithSummary("Escolhe o tenant e cria a sessão.");

        group.MapGet("/csrf", (HttpContext http, IAntiforgery antiforgery) =>
        {
            // Emite (ou reemite) o par de CSRF: o cookie-segredo HttpOnly e o cookie legível
            // pelo JS com o request token.
            http.IssueCsrfToken(antiforgery);

            return Result<object>.Success(new { issued = true }).ToHttpResult(http);
        })
        // ANÔNIMO, e isso é deliberado: o token de CSRF NÃO é uma credencial. Ele não autentica
        // ninguém — só prova que quem o envia consegue LER cookies da nossa origem, coisa que um
        // site de terceiro não consegue (same-origin policy). Emiti-lo a um anônimo não concede
        // nada: sem o cookie de SESSÃO, o token de CSRF sozinho não abre porta nenhuma.
        //
        // Exigir autenticação aqui, aliás, criaria o mesmo impasse que o /auth/login tinha.
        .AllowAnonymous()
        .WithSummary("Emite o token de CSRF (cookie legível pelo JS).")
        .WithDescription(
            "O login já emite o par de CSRF — este endpoint existe para o front RECUPERÁ-LO " +
            "sem precisar relogar. É o caso do usuário que volta com a sessão ainda válida " +
            "(o cookie de sessão dura 8h) mas sem o cookie de CSRF (que é de sessão do " +
            "navegador): sem isto, ele teria uma sessão boa e NENHUMA forma de obter o token, " +
            "e todo POST responderia 403 até ele deslogar e logar de novo. " +
            "Chame-o no boot do app, antes da primeira requisição que altera estado.");

        group.MapPost("/refresh", async (
            AuthService auth,
            HttpContext http,
            CancellationToken ct) =>
        {
            var token = http.Request.Cookies[CookieAuthSetup.RefreshCookie(http.Environment())];

            return string.IsNullOrWhiteSpace(token)
                ? Result<LoginResponse>
                    .Failure(Error.Unauthorized("auth.no_refresh_token", "Sessão expirada."))
                    .ToHttpResult(http)
                : (await auth.RefreshAsync(token, ct)).ToHttpResult(http);
        })
        .AllowAnonymous()
        .WithSummary("Renova a sessão com o refresh token.")
        .WithDescription(
            "O token é rotacionado a cada uso. Se um token já consumido for reapresentado " +
            "(sinal de roubo), a sessão INTEIRA é revogada e é preciso logar de novo.");

        // --- autenticados ---

        group.MapPost("/logout", async (
            AuthService auth,
            HttpContext http,
            CancellationToken ct) =>
        {
            var token = http.Request.Cookies[CookieAuthSetup.RefreshCookie(http.Environment())];

            return (await auth.LogoutAsync(token, ct)).ToHttpResult(http);
        })
        // Sem exigir tenant: deve ser possível sair mesmo de uma sessão parcial.
        .RequireAuthorization(AuthorizationSetup.AuthenticatedWithoutTenantPolicy)
        .WithSummary("Encerra a sessão.");

        group.MapGet("/me", async (
            AuthService auth,
            HttpContext http,
            CancellationToken ct) =>
        {
            var userId = GetUserId(http);

            return userId is null
                ? Result<CurrentUserDto>.Failure(Error.Unauthorized()).ToHttpResult(http)
                : (await auth.GetCurrentUserAsync(userId.Value, GetTenantId(http), ct)).ToHttpResult(http);
        })
        .RequireAuthorization(AuthorizationSetup.AuthenticatedWithoutTenantPolicy)
        .WithSummary("Dados do usuário autenticado, com suas permissões efetivas.")
        .WithDescription(
            "As permissões vêm AQUI, na resposta — nunca dentro do cookie, que teria seu " +
            "tamanho estourado. O frontend as usa para decidir o que renderizar.");
    }

    private static Guid? GetUserId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static Guid? GetTenantId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(AppClaims.TenantId), out var id) ? id : null;
}
