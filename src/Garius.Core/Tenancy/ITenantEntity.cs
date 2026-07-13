namespace Garius.Core.Tenancy;

/// <summary>
/// Entidade que pertence a um tenant. O <c>DbContext</c> aplica um global query filter
/// automático em toda entidade que implementa esta interface — não há como esquecer de
/// filtrar e vazar dado entre clientes.
///
/// <para>
/// <b>O schema nasce sempre multi-tenant</b>, mesmo em modo single-tenant: nesse caso
/// existe um tenant único e fixo, e o filtro compara com um valor constante (custo ~zero,
/// o Postgres resolve pelo índice).
/// </para>
///
/// <para>
/// Ligar tenancy depois seria uma migration destrutiva: <c>UNIQUE(Email)</c> global versus
/// <c>UNIQUE(TenantId, Email)</c>. Por isso a coluna existe desde o primeiro dia, e alternar
/// single ↔ SaaS é trocar o <see cref="ITenantResolver"/> no DI — uma linha.
/// </para>
/// </summary>
public interface ITenantEntity
{
    Guid TenantId { get; set; }
}
