using Garius.Core.Tenancy;

namespace Garius.Core.Identity;

/// <summary>
/// Vínculo N:N entre usuário e tenant — a decisão que torna o login um processo de
/// dois passos quando o usuário pertence a mais de um.
///
/// <code>
/// POST /auth/login          → 200 { tenants: [A, B, C] }   (ainda SEM cookie)
/// POST /auth/select-tenant  → Set-Cookie (TenantId nas claims)
/// </code>
///
/// Com um único tenant, o login emite o cookie direto — o segundo passo é um atalho, não
/// uma obrigação.
/// </summary>
public sealed class ApplicationUserTenant
{
    public Guid UserId { get; set; }

    public ApplicationUser User { get; set; } = null!;

    public Guid TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// O tenant que o usuário assume ao entrar, quando pertence a vários. Evita a tela de
    /// seleção no caso comum de quem só trabalha em um deles.
    /// </summary>
    public bool IsDefault { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
}
