namespace Garius.Core.Authorization;

/// <summary>
/// Nomes das claims da aplicação. <b>Definidos uma única vez</b>: o mesmo nome é gravado no
/// cookie/JWT e lido na autorização — duas definições que divergem produzem um bug silencioso
/// em que o usuário simplesmente "não tem" um tenant, e nada funciona.
/// </summary>
public static class AppClaims
{
    /// <summary>
    /// O tenant que o usuário selecionou. Como o vínculo é N:N, ele só existe <b>depois</b> da
    /// seleção — não já no login. Ver a Fase 4c.
    /// </summary>
    public const string TenantId = "tenant_id";

    /// <summary>Uma permissão (<c>invoices.approve</c>). Ver <see cref="Permission.ClaimType"/>.</summary>
    public const string Permission = Authorization.Permission.ClaimType;
}
