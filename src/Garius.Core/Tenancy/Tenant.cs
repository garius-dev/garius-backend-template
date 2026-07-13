using Garius.Core.Entities;

namespace Garius.Core.Tenancy;

/// <summary>
/// Um tenant (cliente/organização). Em modo single-tenant existe exatamente um,
/// com o id fixo de <see cref="TenancyOptions.DefaultTenantId"/>.
///
/// <para>
/// Note que <c>Tenant</c> NÃO implementa <see cref="ITenantEntity"/> — ele não pertence
/// a um tenant, ele <i>é</i> o tenant. Aplicar o query filter aqui criaria uma recursão
/// e tornaria o tenant invisível para si mesmo.
/// </para>
/// </summary>
public sealed class Tenant : BaseEntity
{
    /// <summary>Nome de exibição.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Identificador estável e legível (ex.: <c>acme</c>). Usado para resolver o tenant
    /// por subdomínio (<c>acme.app.dominio.com</c>) ou por header em modo SaaS.
    /// </summary>
    public required string Slug { get; set; }
}
