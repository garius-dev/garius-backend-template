using Garius.Core.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Garius.Api.Infrastructure.Authorization;

/// <summary>
/// Exige uma <b>permissão</b> do usuário.
///
/// <code>
/// group.MapPost("/{id}/approve", ...)
///      .RequirePermission(Permissions.Invoices.Approve);   // ✅
///
/// group.MapPost("/{id}/approve", ...)
///      .RequireAuthorization(new AuthorizeAttribute { Roles = "Financeiro" });   // ❌ NUNCA
/// </code>
///
/// <para>
/// <b>Nunca exija um papel.</b> Papel é um agrupamento de permissões, um detalhe de
/// configuração — no dia em que o "Gerente" também precisar aprovar fatura, isso se resolve
/// no banco, não recompilando. É o que separa um sistema de autorização que envelhece bem
/// daquele que degenera em <c>if (role == "Admin" || role == "Gerente" || ...)</c>.
/// </para>
/// </summary>
public sealed class RequirePermissionAttribute : AuthorizeAttribute
{
    /// <summary>
    /// Prefixo que identifica uma policy de permissão. O
    /// <see cref="PermissionPolicyProvider"/> reconhece a policy pelo prefixo e a cria na hora
    /// — sem isso, cada permissão teria de ser registrada à mão no <c>Program.cs</c>, e
    /// esquecer uma daria um 500 em vez de um 403.
    /// </summary>
    internal const string PolicyPrefix = "PERMISSION:";

    public RequirePermissionAttribute(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        Permission = permission;
        Policy = $"{PolicyPrefix}{permission}";
    }

    public string Permission { get; }

    internal static bool TryParse(string policyName, out string permission)
    {
        if (policyName.StartsWith(PolicyPrefix, StringComparison.Ordinal))
        {
            permission = policyName[PolicyPrefix.Length..];

            return true;
        }

        permission = string.Empty;

        return false;
    }
}

/// <summary>O que a policy exige: uma permissão.</summary>
internal sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public static class PermissionEndpointExtensions
{
    /// <summary>
    /// Exige a permissão neste endpoint (ou em todo o grupo).
    ///
    /// <code>
    /// group.MapGet("/", ...).RequirePermission(Permissions.Users.Read);
    /// </code>
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, Permission permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(permission);

        return builder.RequirePermission(permission.Value);
    }

    /// <inheritdoc cref="RequirePermission{TBuilder}(TBuilder, Permission)"/>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.RequireAuthorization(new RequirePermissionAttribute(permission));

        return builder;
    }
}
