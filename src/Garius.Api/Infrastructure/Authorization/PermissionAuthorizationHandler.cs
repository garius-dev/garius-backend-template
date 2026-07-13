using System.Security.Claims;
using Garius.Api.Infrastructure.Networking;
using Garius.Core.Authorization;
using Garius.Core.Machine;
using Microsoft.AspNetCore.Authorization;

namespace Garius.Api.Infrastructure.Authorization;

/// <summary>
/// Decide se o usuário tem a permissão exigida.
///
/// <para>
/// As permissões efetivas (papéis + avulsas) vêm do <see cref="IPermissionResolver"/>, que as
/// mantém em cache — do contrário cada request faria um JOIN entre usuário, papéis e claims
/// antes de chegar à lógica de negócio.
/// </para>
/// </summary>
internal sealed class PermissionAuthorizationHandler(
    IPermissionResolver permissionResolver,
    IHttpContextAccessor httpContextAccessor,
    ILogger<PermissionAuthorizationHandler> logger)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var granted = await GetGrantedPermissionsAsync(context.User);

        if (granted is null)
        {
            // Não autenticado. A policy já exige RequireAuthenticatedUser, então isto só
            // acontece se alguém montar a policy à mão — mas negar é o default seguro.
            return;
        }

        // Match com curinga: "*" satisfaz tudo; "invoices.*" satisfaz "invoices.approve".
        // O MESMO match para pessoa, client OAuth e chave de API — é o ponto de todo o desenho.
        var authorized = granted.Any(g => Permission.Matches(g, requirement.Permission));

        if (authorized)
        {
            context.Succeed(requirement);

            return;
        }

        // Acesso negado é um evento de segurança: é o que se procura quando alguém tenta
        // escalar privilégio. Vai como Warning para o Grafana poder alertar.
        logger.LogWarning(
            "Acesso NEGADO. Principal={Principal} Tipo={ClientType} Permissão={Permission} " +
            "Rota={Path} IP={ClientIp}",
            context.User.FindFirstValue(ClaimTypes.NameIdentifier),
            context.User.FindFirstValue(MachineAuth.ClientTypeClaim) ?? "user",
            requirement.Permission,
            httpContextAccessor.HttpContext?.Request.Path.Value,
            httpContextAccessor.HttpContext?.GetClientIp());
    }

    /// <summary>
    /// De onde vêm as permissões — e é aqui que pessoa e máquina se separam.
    ///
    /// <list type="bullet">
    ///   <item><b>Máquina</b> (JWT ou chave de API): as permissões estão <b>no próprio
    ///         principal</b>, como claims. Vieram assinadas dentro do JWT, ou foram montadas
    ///         pelo handler da chave a partir do banco. Nenhuma consulta a mais — é o que torna
    ///         a autorização M2M stateless e barata.</item>
    ///
    ///   <item><b>Pessoa</b> (cookie): as permissões <b>não</b> estão no principal, e é
    ///         deliberado — dentro do cookie elas estourariam o limite de tamanho do navegador
    ///         (ver <c>PermissionScaleTests</c>). Vêm do resolver, que as cacheia.</item>
    /// </list>
    ///
    /// <para>
    /// A distinção é feita pela claim <c>client_type</c>, não pela ausência de um id de usuário
    /// — um client tem <c>sub</c>, só que ele é o <c>client_id</c>, não um Guid.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyCollection<string>?> GetGrantedPermissionsAsync(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var isMachine = user.HasClaim(claim => claim.Type == MachineAuth.ClientTypeClaim);

        if (isMachine)
        {
            return [.. user.FindAll(AppClaims.Permission).Select(claim => claim.Value)];
        }

        var userId = GetUserId(user);

        if (userId is null)
        {
            return null;
        }

        return [.. await permissionResolver.GetPermissionsAsync(userId.Value, GetTenantId(user))];
    }

    private static Guid? GetUserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>
    /// O tenant vem da claim gravada quando o usuário o selecionou no login (Fase 4c).
    /// Importa porque as permissões variam por tenant: um papel de tenant só vale no dele.
    /// </summary>
    private static Guid? GetTenantId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(AppClaims.TenantId), out var id) ? id : null;
}
