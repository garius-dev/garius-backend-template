using Microsoft.AspNetCore.Identity;

namespace Garius.Core.Identity;

/// <summary>
/// Liga usuário e papel. Diferente do <see cref="IdentityUserRole{TKey}"/> padrão, tem
/// <b>navegações nos dois lados</b> — é o que permite <c>user.UserRoles.Select(r =&gt; r.Role)</c>
/// numa query só, em vez de um <c>GetRolesAsync()</c> extra a cada request.
///
/// <para>
/// <b>Não tem <c>TenantId</c></b>, e isso é deliberado. Quem carrega o tenant é o
/// <see cref="ApplicationRole"/>: um papel ou é global (<c>TenantId = null</c>) ou pertence a
/// um tenant. Para ser administrador na empresa A e leitor na empresa B, o usuário recebe
/// <b>dois papéis diferentes</b>.
/// </para>
///
/// <para>
/// A alternativa — pôr <c>TenantId</c> na chave primária — foi tentada e <b>quebra o
/// Identity</b>: o <c>UserStore</c> faz <c>Find(userId, roleId)</c> com uma chave de duas
/// partes, e <c>AddToRoleAsync</c> falha com "3-part composite key, but 2 values were passed".
/// Manter a chave do framework evita reescrever o store — acoplamento que doeria a cada
/// upgrade do .NET.
/// </para>
/// </summary>
public sealed class ApplicationUserRole : IdentityUserRole<Guid>
{
    public ApplicationUser User { get; set; } = null!;

    public ApplicationRole Role { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }
}
