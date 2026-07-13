using Garius.Core.Tenancy;

namespace Garius.Infrastructure.Tenancy;

/// <summary>
/// Sem contexto de tenant: <c>CurrentTenantId</c> é <c>null</c>, então o global query
/// filter de tenant <b>não filtra nada</b> — enxerga todos os tenants.
///
/// <para>
/// Usado exclusivamente pelo bootstrap, pelas migrations e por jobs de manutenção que
/// legitimamente operam sobre todos os tenants. <b>Nunca</b> deve ser registrado no
/// pipeline de request.
/// </para>
/// </summary>
internal sealed class SystemTenantResolver : ITenantResolver
{
    public Guid? CurrentTenantId => null;
}
