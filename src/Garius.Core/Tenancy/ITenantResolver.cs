namespace Garius.Core.Tenancy;

/// <summary>
/// Descobre o tenant do contexto atual. É o <b>único</b> ponto que muda entre
/// single-tenant e SaaS.
///
/// <list type="bullet">
///   <item><b>Single-tenant:</b> devolve sempre o mesmo GUID fixo. Zero código de tenant
///         no dia a dia, zero header, zero claim.</item>
///   <item><b>SaaS:</b> resolve por claim do usuário, subdomínio ou header.</item>
/// </list>
///
/// Trocar de um para o outro é uma linha no <c>Program.cs</c> — o resto da aplicação
/// não muda.
/// </summary>
public interface ITenantResolver
{
    /// <summary>
    /// Tenant do contexto atual.
    ///
    /// <para>
    /// <c>null</c> significa <b>sem filtro de tenant</b> — usado pelo bootstrap, pelas
    /// migrations e por jobs de manutenção que legitimamente enxergam todos os tenants.
    /// Em um request HTTP autenticado, nunca deve ser null.
    /// </para>
    /// </summary>
    Guid? CurrentTenantId { get; }
}
