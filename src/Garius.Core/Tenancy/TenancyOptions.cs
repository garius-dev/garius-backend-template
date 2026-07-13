namespace Garius.Core.Tenancy;

/// <summary>Seção <c>Tenancy</c>.</summary>
public sealed class TenancyOptions
{
    public const string SectionName = "Tenancy";

    /// <summary>
    /// <c>SingleTenant</c> (padrão) ou <c>MultiTenant</c>.
    ///
    /// <para>
    /// Alternar entre os dois <b>não muda o schema</b> — a coluna <c>TenantId</c> existe
    /// sempre. Muda apenas qual <see cref="ITenantResolver"/> é registrado.
    /// </para>
    /// </summary>
    public TenancyMode Mode { get; set; } = TenancyMode.SingleTenant;

    /// <summary>
    /// O tenant usado em modo <see cref="TenancyMode.SingleTenant"/>. Criado no bootstrap
    /// se não existir.
    /// </summary>
    public Guid DefaultTenantId { get; set; } = new("00000000-0000-0000-0000-000000000001");
}

public enum TenancyMode
{
    /// <summary>Um único tenant, fixo. A aplicação inteira ignora tenancy no dia a dia.</summary>
    SingleTenant,

    /// <summary>Vários tenants; o tenant vem do contexto do request (claim/subdomínio/header).</summary>
    MultiTenant
}
