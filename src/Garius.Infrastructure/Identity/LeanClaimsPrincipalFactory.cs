using System.Security.Claims;
using Garius.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Garius.Infrastructure.Identity;

/// <summary>
/// Monta o <see cref="ClaimsPrincipal"/> do usuário com <b>apenas a identidade</b> —
/// nenhuma permissão, nenhum papel.
///
/// <para>
/// <b>Por que isto existe.</b> O <c>UserClaimsPrincipalFactory</c> padrão do Identity
/// (registrado por <c>AddRoles&lt;&gt;()</c>) copia para o principal <b>todas</b> as roles do
/// usuário e <b>todas</b> as claims delas. Como as permissões deste template são claims de
/// papel, um usuário com 50 papéis × 20 permissões produziria um cookie de <b>~50 KB</b> —
/// medido, não estimado.
/// </para>
///
/// <para>
/// As consequências disso em produção são especialmente cruéis porque são <b>silenciosas</b>:
/// </para>
/// <list type="bullet">
///   <item>o navegador <b>descarta</b> cookies acima de ~4 KB <b>sem erro</b> — o usuário
///         simplesmente "não consegue logar", e não há nada nos logs;</item>
///   <item>o Traefik/nginx rejeita headers acima de 8 KB com <c>431 Request Header Fields Too
///         Large</c> — um erro que aparece só quando o usuário acumula papéis suficientes.</item>
/// </list>
///
/// <para>
/// Com este factory, o cookie tem <b>tamanho constante (~411 bytes)</b>, tenha o usuário 5 ou
/// 1000 permissões. Elas são resolvidas do banco (com cache) a cada requisição, pelo
/// <c>IPermissionResolver</c> — o que, de quebra, torna a revogação imediata: um cookie não
/// carrega permissões velhas porque não carrega permissão nenhuma.
/// </para>
///
/// <para>
/// Travado por <c>PermissionScaleTests</c>.
/// </para>
/// </summary>
internal sealed class LeanClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser>(userManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        // A base já adiciona o essencial: NameIdentifier, Name e SecurityStamp.
        // Ela NÃO adiciona papéis — isso é feito pelo UserClaimsPrincipalFactory<TUser, TRole>,
        // que herda desta classe e é o que o AddRoles<>() registraria se não o substituíssemos.
        var identity = await base.GenerateClaimsAsync(user);

        // Deliberadamente NÃO adicionamos:
        //   - papéis        (viram claims e incham o cookie)
        //   - permissões    (idem — e ficariam obsoletas até o próximo login)
        //
        // A claim de tenant é adicionada no fluxo de login, depois que o usuário SELECIONA o
        // tenant (o vínculo é N:N). Ver a Fase 4c.

        return identity;
    }
}
