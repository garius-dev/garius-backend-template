using System.Security.Claims;
using Garius.Api.Infrastructure.Errors;
using Garius.Api.Infrastructure.Networking;
using Garius.Core.Authorization;
using Garius.Core.Security;

namespace Garius.Api.Infrastructure.Security;

/// <summary>
/// Quem está fazendo a requisição, a partir do <c>HttpContext</c>.
/// </summary>
internal sealed class HttpCurrentUser(
    IHttpContextAccessor accessor,
    IPermissionResolver permissionResolver) : ICurrentUser
{
    public Guid? UserId
    {
        get
        {
            var value = accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);

            return Guid.TryParse(value, out var id) ? id : null;
        }
    }

    /// <summary>IP real — via <c>CF-Connecting-IP</c> validado, nunca o do proxy.</summary>
    public string? ClientIp => accessor.HttpContext?.GetClientIp();

    public string? TraceId
    {
        get
        {
            var context = accessor.HttpContext;

            return context is null ? null : ProblemDetailsFactory.GetTraceId(context);
        }
    }

    /// <summary>
    /// Consulta as permissões efetivas (papéis + avulsas, em cache) e compara com curinga:
    /// <c>*</c> satisfaz tudo, <c>pii.*</c> satisfaz <c>pii.cpf.read</c>.
    /// </summary>
    public async Task<bool> HasPermissionAsync(
        string permission,
        CancellationToken cancellationToken = default)
    {
        var userId = UserId;

        if (userId is null)
        {
            return false;
        }

        var tenantId = Guid.TryParse(
            accessor.HttpContext?.User.FindFirstValue(AppClaims.TenantId), out var tenant)
                ? tenant
                : (Guid?)null;

        var granted = await permissionResolver.GetPermissionsAsync(
            userId.Value, tenantId, cancellationToken);

        return granted.Any(g => Permission.Matches(g, permission));
    }
}
