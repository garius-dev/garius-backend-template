using System.Security.Claims;
using Garius.Core.Authorization;
using Garius.Core.Tenancy;
using Microsoft.AspNetCore.Http;

namespace Garius.Infrastructure.Tenancy;

/// <summary>
/// Modo SaaS: o tenant vem de uma claim do usuário autenticado.
///
/// <para>
/// Como um usuário pode pertencer a vários tenants (N:N), a claim é gravada no cookie/JWT
/// no momento em que ele <b>seleciona</b> o tenant — não no login. Ver a Fase 4.
/// </para>
///
/// <para>
/// A claim é a única fonte aceitável: um header enviado pelo cliente seria trivialmente
/// forjável, e trocar de tenant seria só mudar um header.
/// </para>
/// </summary>
internal sealed class ClaimsTenantResolver(IHttpContextAccessor httpContextAccessor) : ITenantResolver
{
    public Guid? CurrentTenantId
    {
        get
        {
            // AppClaims.TenantId é a ÚNICA definição do nome desta claim — gravar com um nome
            // e ler com outro produziria um bug silencioso em que o usuário nunca tem tenant.
            var value = httpContextAccessor.HttpContext?.User.FindFirstValue(AppClaims.TenantId);

            return Guid.TryParse(value, out var tenantId) ? tenantId : null;
        }
    }
}
