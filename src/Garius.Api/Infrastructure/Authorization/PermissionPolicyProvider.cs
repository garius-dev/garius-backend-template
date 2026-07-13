using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Garius.Api.Infrastructure.Authorization;

/// <summary>
/// Cria as policies de permissão <b>sob demanda</b>, a partir do nome.
///
/// <para>
/// Sem isto, cada permissão precisaria ser registrada à mão no <c>Program.cs</c>
/// (<c>options.AddPolicy("invoices.approve", ...)</c>) — e esquecer uma daria um <b>500</b> em
/// tempo de execução ("policy not found"), não um 403. Com dezenas de permissões, esquecer é
/// questão de tempo.
/// </para>
///
/// <para>
/// Aqui, declarar <c>.RequirePermission(Permissions.Invoices.Approve)</c> no endpoint é tudo
/// o que é preciso: a policy passa a existir.
/// </para>
/// </summary>
internal sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        ArgumentNullException.ThrowIfNull(policyName);

        if (RequirePermissionAttribute.TryParse(policyName, out var permission))
        {
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                // Também exige o tenant: um cookie PARCIAL (emitido no login quando há vários
                // tenants a escolher) não pode alcançar um endpoint de negócio — o query filter
                // compararia o tenant com `null` e não filtraria nada. Ver AuthorizationSetup.
                .RequireClaim(Core.Authorization.AppClaims.TenantId)
                .AddRequirements(new PermissionRequirement(permission))
                .Build();

            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        // Não é uma policy de permissão: deixa o provider padrão resolver.
        return _fallback.GetPolicyAsync(policyName);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();
}
