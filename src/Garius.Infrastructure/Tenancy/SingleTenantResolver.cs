using Garius.Core.Tenancy;
using Microsoft.Extensions.Options;

namespace Garius.Infrastructure.Tenancy;

/// <summary>
/// Modo single-tenant: devolve sempre o mesmo tenant.
///
/// A aplicação inteira continua escrita como se fosse multi-tenant (o filtro roda, a
/// coluna existe), mas na prática ninguém lida com tenant. Migrar para SaaS depois é
/// trocar esta classe pelo <see cref="ClaimsTenantResolver"/> no DI — o schema já está pronto.
/// </summary>
internal sealed class SingleTenantResolver(IOptions<TenancyOptions> options) : ITenantResolver
{
    public Guid? CurrentTenantId { get; } = options.Value.DefaultTenantId;
}
