using System.Security.Claims;
using Garius.Api.Infrastructure.Authorization;
using Garius.Api.Infrastructure.Networking;
using Garius.Core.Authorization;
using Garius.Core.Identity;
using Garius.Core.Results;
using Garius.Infrastructure.Database;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Garius.Api.Features.Auth;

public sealed class AuthService(
    UserManager<ApplicationUser> userManager,
    IUserClaimsPrincipalFactory<ApplicationUser> principalFactory,
    IRefreshTokenStore refreshTokens,
    IPermissionResolver permissions,
    AppDbContext db,
    IHttpContextAccessor httpContextAccessor,
    IHostEnvironment environment,
    ILogger<AuthService> logger)
{
    public async Task<Result<LoginResponse>> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await userManager.FindByEmailAsync(request.Email);

        // Mesma resposta para "usuário não existe" e "senha errada".
        //
        // Uma mensagem específica ("este e-mail não está cadastrado") transforma o login num
        // ORÁCULO DE ENUMERAÇÃO: um atacante descobre quais e-mails têm conta, o que já é um
        // vazamento — e o primeiro passo de um ataque direcionado.
        if (user is null)
        {
            await FakePasswordCheckAsync();

            return InvalidCredentials();
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            await AuditAsync("login.lockout", user.Id);

            return Error.TooManyRequests(
                "auth.locked_out",
                "Muitas tentativas de login. Tente novamente em alguns minutos.");
        }

        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            // Incrementa o contador de falhas DA CONTA (o lockout do Identity).
            //
            // Isto protege contra brute force em UMA conta. NÃO protege contra password
            // spraying (a mesma senha em milhares de contas, de IPs rotativos) — para isso é
            // preciso rate limit por IP, que é uma dimensão INDEPENDENTE. O template anterior
            // tinha só a dimensão de IP, e por isso permitia spraying E travava usuários
            // atrás do mesmo NAT.
            await userManager.AccessFailedAsync(user);

            await AuditAsync("login.failed", user.Id);

            return InvalidCredentials();
        }

        await userManager.ResetAccessFailedCountAsync(user);

        var tenants = await GetTenantsAsync(user.Id, cancellationToken);

        if (tenants.Count == 0)
        {
            return Error.Forbidden(
                "auth.no_tenant",
                "Seu usuário não está vinculado a nenhuma organização.");
        }

        // Com um único tenant, não há o que escolher: emite o cookie direto.
        if (tenants.Count == 1)
        {
            return await CreateSessionAsync(user, tenants[0].Id, cancellationToken);
        }

        // Vários tenants: o usuário precisa escolher.
        //
        // Emitimos um cookie PARCIAL — identidade sim, tenant não. Ele é o que permite chamar
        // /auth/select-tenant sem reenviar a senha, e sem inventar um "token de meia-sessão".
        //
        // ⚠️ Um cookie sem tenant é perigoso se vazar para os endpoints de negócio: o query
        // filter de tenant compara com `null` e NÃO FILTRA NADA — o usuário enxergaria dados
        // de todos os tenants. É por isso que existe a policy `RequireTenant`, aplicada por
        // padrão a tudo que não seja o próprio fluxo de seleção. Ver AuthorizationSetup.
        await SignInPartialAsync(user);

        await AuditAsync("login.tenant_selection_required", user.Id);

        return new LoginResponse(Authenticated: false, tenants, User: null);
    }

    public async Task<Result<LoginResponse>> SelectTenantAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());

        if (user is null)
        {
            return Error.Unauthorized();
        }

        // O usuário PERTENCE a este tenant? Sem esta checagem, bastaria mandar o id de outro
        // tenant no corpo para entrar nele.
        var belongs = await db.UserTenants
            .AnyAsync(ut => ut.UserId == userId && ut.TenantId == tenantId, cancellationToken);

        if (!belongs)
        {
            logger.LogWarning(
                "Tentativa de selecionar um tenant a que o usuário NÃO pertence. " +
                "Usuário={UserId} Tenant={TenantId} IP={ClientIp}",
                userId, tenantId, ClientIp);

            return Error.Forbidden("auth.tenant_not_allowed", "Você não pertence a esta organização.");
        }

        return await CreateSessionAsync(user, tenantId, cancellationToken);
    }

    /// <summary>
    /// Troca o refresh token por uma sessão nova, rotacionando-o.
    ///
    /// <para>
    /// Se o token já tiver sido consumido, o store <b>revoga a família inteira</b> e devolve
    /// <c>null</c> — ver <see cref="IRefreshTokenStore"/>.
    /// </para>
    /// </summary>
    public async Task<Result<LoginResponse>> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var rotated = await refreshTokens.RotateAsync(refreshToken, cancellationToken);

        if (rotated is null)
        {
            return Error.Unauthorized("auth.invalid_refresh_token", "Sessão expirada. Faça login novamente.");
        }

        var (token, data) = rotated.Value;

        var user = await userManager.FindByIdAsync(data.UserId.ToString());

        // O usuário pode ter sido desabilitado desde a emissão do token. O query filter de
        // soft delete faz o FindByIdAsync devolver null — e a sessão morre aqui.
        if (user is null)
        {
            return Error.Unauthorized("auth.invalid_refresh_token", "Sessão expirada. Faça login novamente.");
        }

        await SignInAsync(user, data.TenantId, token);

        return new LoginResponse(
            Authenticated: true,
            Tenants: null,
            User: await BuildCurrentUserAsync(user, data.TenantId, cancellationToken));
    }

    public async Task<Result> LogoutAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;

        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            await refreshTokens.RevokeAsync(refreshToken, cancellationToken);
        }

        await http.SignOutAsync(CookieAuthSetup.SchemeName);

        http.Response.Cookies.Delete(CookieAuthSetup.RefreshCookie(environment));

        return Result.Success();
    }

    public async Task<Result<CurrentUserDto>> GetCurrentUserAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());

        return user is null
            ? Error.Unauthorized()
            : await BuildCurrentUserAsync(user, tenantId, cancellationToken);
    }

    // --- interno -------------------------------------------------------------

    private async Task<Result<LoginResponse>> CreateSessionAsync(
        ApplicationUser user,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var refreshToken = await refreshTokens.IssueAsync(user.Id, tenantId, cancellationToken);

        await SignInAsync(user, tenantId, refreshToken);

        await AuditAsync("login.success", user.Id);

        return new LoginResponse(
            Authenticated: true,
            Tenants: null,
            User: await BuildCurrentUserAsync(user, tenantId, cancellationToken));
    }

    /// <summary>
    /// Cookie <b>parcial</b>: identidade sim, tenant não. Só serve para chamar
    /// <c>/auth/select-tenant</c> — a policy <c>RequireTenant</c> barra tudo o mais.
    /// Nenhum refresh token é emitido: a sessão ainda não existe de verdade.
    /// </summary>
    private async Task SignInPartialAsync(ApplicationUser user)
    {
        var principal = await principalFactory.CreateAsync(user);

        var http = httpContextAccessor.HttpContext!;

        await http.SignInAsync(
            CookieAuthSetup.SchemeName,
            principal,
            new AuthenticationProperties
            {
                // Curto: é uma janela para escolher o tenant, não uma sessão.
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
            });

        // Ver o comentário em SignInAsync: sem isto, o token de CSRF emitido em seguida
        // nasceria vinculado ao usuário anônimo.
        http.User = principal;
    }

    private async Task SignInAsync(ApplicationUser user, Guid? tenantId, RefreshToken refreshToken)
    {
        var http = httpContextAccessor.HttpContext!;

        // O principal sai do LeanClaimsPrincipalFactory: só identidade, NENHUMA permissão.
        // Ver PermissionScaleTests — com as permissões dentro, o cookie estouraria o limite
        // do navegador e o login quebraria em silêncio.
        var principal = await principalFactory.CreateAsync(user);

        if (tenantId is not null && principal.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(new Claim(AppClaims.TenantId, tenantId.Value.ToString()));
        }

        await http.SignInAsync(CookieAuthSetup.SchemeName, principal);

        // Atualiza o principal do request CORRENTE.
        //
        // O SignInAsync só grava o cookie: o HttpContext.User continua anônimo até o PRÓXIMO
        // request. Isso quebraria o token de CSRF emitido em seguida — o antiforgery o vincula
        // à identidade do usuário, e ele nasceria vinculado ao anônimo, falhando na validação
        // do primeiro POST autenticado.
        http.User = principal;

        // O refresh token vai num cookie SEPARADO do de sessão.
        http.Response.Cookies.Append(
            CookieAuthSetup.RefreshCookie(environment),
            refreshToken.Token,
            new CookieOptions
            {
                HttpOnly = true,

                // Secure SEMPRE, exceto em desenvolvimento (que roda em HTTP puro — um cookie
                // Secure simplesmente não seria reenviado pelo navegador, e o refresh nunca
                // funcionaria localmente).
                //
                // NÃO derivar de Request.IsHttps: atrás do Traefik o container recebe HTTP, e o
                // cookie sairia SEM a flag Secure em PRODUÇÃO — o erro exato do template anterior.
                Secure = !environment.IsDevelopment(),

                SameSite = SameSiteMode.Lax,
                Expires = refreshToken.ExpiresAt,
                Path = "/"
            });
    }

    private async Task<CurrentUserDto> BuildCurrentUserAsync(
        ApplicationUser user,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        var granted = await permissions.GetPermissionsAsync(user.Id, tenantId, cancellationToken);

        return new CurrentUserDto(
            user.Id,
            // MASCARADO. O e-mail em claro exige leitura auditada via IPiiReader — não é
            // derramado numa resposta de login.
            user.EmailPii.Masked,
            user.DisplayName,
            tenantId,
            [.. granted]);
    }

    private async Task<IReadOnlyList<TenantOptionDto>> GetTenantsAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
            await db.UserTenants
                .Where(ut => ut.UserId == userId)
                .Select(ut => new TenantOptionDto(ut.TenantId, ut.Tenant.Name, ut.IsDefault))
                .ToListAsync(cancellationToken);

    /// <summary>
    /// Gasta o mesmo tempo de um <c>CheckPasswordAsync</c> real quando o usuário não existe.
    ///
    /// <para>
    /// Sem isto, a resposta para um e-mail inexistente volta em ~1 ms e a de um e-mail real em
    /// ~100 ms (o custo do PBKDF2). A diferença é medível e transforma o login num oráculo de
    /// enumeração — a mensagem genérica sozinha não basta.
    /// </para>
    /// </summary>
    private async Task FakePasswordCheckAsync()
    {
        var dummy = new ApplicationUser
        {
            PasswordHash = "AQAAAAIAAYagAAAAEL1234567890abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789=="
        };

        await userManager.CheckPasswordAsync(dummy, "senha-qualquer");
    }

    private static Result<LoginResponse> InvalidCredentials() =>
        Error.Unauthorized("auth.invalid_credentials", "E-mail ou senha inválidos.");

    private string? ClientIp => httpContextAccessor.HttpContext?.GetClientIp();

    private Task AuditAsync(string eventType, Guid userId)
    {
        // Eventos de autenticação vão para o log estruturado com a propriedade Audit=true,
        // que é o que o Grafana consegue indexar e alertar. (Uma tabela de auditoria de
        // login inflaria o banco sem ganho — o Loki já retém e busca isso melhor.)
        logger.LogInformation(
            "{Audit} {EventType} Usuário={UserId} IP={ClientIp}",
            "AUTH", eventType, userId, ClientIp);

        return Task.CompletedTask;
    }
}
