using Microsoft.AspNetCore.Identity;

namespace Garius.Core.Identity;

/// <summary>
/// Permissão concedida <b>diretamente</b> a um usuário, fora de qualquer papel.
///
/// <para>
/// É a exceção pontual do modelo GCP IAM: "esta pessoa, e só ela, também pode exportar
/// relatórios" — sem precisar criar um papel novo para um caso único. Use com parcimônia:
/// permissões avulsas espalhadas são o que torna um sistema de autorização inauditável.
/// </para>
/// </summary>
public sealed class ApplicationUserClaim : IdentityUserClaim<Guid>
{
    public ApplicationUser User { get; set; } = null!;

    /// <summary>Restringe a permissão a um tenant. <c>null</c> = vale em todos os do usuário.</summary>
    public Guid? TenantId { get; set; }
}

/// <summary>
/// Uma permissão do papel (<c>ClaimType = "permission"</c>, <c>ClaimValue = "invoices.approve"</c>).
/// É por aqui que as permissões chegam ao usuário no caso normal.
/// </summary>
public sealed class ApplicationRoleClaim : IdentityRoleClaim<Guid>
{
    public ApplicationRole Role { get; set; } = null!;
}

/// <summary>Login externo (Google, Microsoft...). Não usado hoje; o Identity exige a tabela.</summary>
public sealed class ApplicationUserLogin : IdentityUserLogin<Guid>
{
    public ApplicationUser User { get; set; } = null!;
}

/// <summary>
/// Token do Identity (confirmação de e-mail, reset de senha, 2FA).
///
/// <para>
/// Note que <b>não</b> é aqui que vivem os refresh tokens: eles ficam no Redis, com TTL
/// nativo, para não poluir o banco. Ver a Fase 4c.
/// </para>
/// </summary>
public sealed class ApplicationUserToken : IdentityUserToken<Guid>
{
    public ApplicationUser User { get; set; } = null!;
}
