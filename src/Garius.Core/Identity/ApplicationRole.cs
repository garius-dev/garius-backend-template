using Garius.Core.Entities;
using Microsoft.AspNetCore.Identity;

namespace Garius.Core.Identity;

/// <summary>
/// Um papel — um <b>conjunto nomeado de permissões</b>, no modelo do Google Cloud IAM.
///
/// <para>
/// A role é o veículo; a <b>permissão</b> é o que o endpoint exige. Um endpoint declara
/// <c>[RequirePermission("invoices.approve")]</c>, nunca <c>[Authorize(Roles = "Financeiro")]</c>.
/// No dia em que o "Gerente" também precisar aprovar fatura, mexe-se no banco, não no código.
/// </para>
/// </summary>
public sealed class ApplicationRole : IdentityRole<Guid>, IAuditable
{
    public ApplicationRole()
    {
        Id = Guid.CreateVersion7();
    }

    public ApplicationRole(string name) : this()
    {
        Name = name;
        NormalizedName = name.ToUpperInvariant();
    }

    /// <summary>Para que serve este papel. Aparece na tela de administração.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Papel do sistema (ex.: <c>SuperAdmin</c>): não pode ser editado nem removido pela
    /// interface. Sem isto, um administrador se tranca para fora ao apagar o próprio papel.
    /// </summary>
    public bool IsSystem { get; set; }

    /// <summary>
    /// <c>null</c> = papel global, disponível a todos os tenants.
    /// Preenchido = papel criado por um tenant específico, visível só para ele.
    /// </summary>
    public Guid? TenantId { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    // --- Navegações explícitas ---

    /// <summary>As permissões deste papel. Cada claim é uma permissão (<c>invoices.approve</c>).</summary>
    public ICollection<ApplicationRoleClaim> Claims { get; } = [];

    public ICollection<ApplicationUserRole> UserRoles { get; } = [];
}
