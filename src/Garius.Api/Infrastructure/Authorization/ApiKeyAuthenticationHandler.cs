using System.Security.Claims;
using System.Text.Encodings.Web;
using Garius.Core.Authorization;
using Garius.Core.Machine;
using Garius.Infrastructure.Machine;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Garius.Api.Infrastructure.Authorization;

/// <summary>
/// Autentica pelo header <c>X-Api-Key</c>.
///
/// <para>
/// Diferente do JWT (que é stateless e não custa nada validar), <b>cada request com chave de
/// API vai ao banco</b>. É o preço de uma credencial de longa duração: é justamente o que
/// permite revogá-la de imediato e contabilizar a quota. Um cache aqui devolveria a chave
/// revogada por mais alguns minutos — e a revogação imediata é a principal defesa que uma
/// chave que vive meses tem.
/// </para>
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    MachineClientStore store)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(MachineAuth.ApiKeyHeader, out var values))
        {
            // NoResult, não Fail: a ausência do header significa "este esquema não se aplica",
            // e não "a autenticação falhou". Um Fail aqui abortaria a requisição de um usuário
            // logado por cookie, que legitimamente não manda X-Api-Key.
            return AuthenticateResult.NoResult();
        }

        var key = values.ToString();

        if (string.IsNullOrWhiteSpace(key))
        {
            return AuthenticateResult.Fail("Chave de API vazia.");
        }

        var apiKey = await store.AuthenticateApiKeyAsync(key, Context.RequestAborted);

        if (apiKey is null)
        {
            // Mensagem única para inválida, expirada e sem quota — o cliente não precisa saber
            // qual, e distinguir daria a um atacante um oráculo sobre chaves válidas. O motivo
            // real vai para o log, no MachineClientStore.
            return AuthenticateResult.Fail("Chave de API inválida.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, apiKey.Id.ToString()),
            new(ClaimTypes.Name, apiKey.Name),
            new(MachineAuth.ClientTypeClaim, MachineAuth.ApiKeyType),

            // O tenant vem da CHAVE, não do request. É o que impede um terceiro de ler dados
            // de outro tenant mandando outro id — a mesma garantia que o JWT dá ao client OAuth.
            new(AppClaims.TenantId, apiKey.TenantId.ToString())
        };

        // Os escopos viram claims de permissão — as MESMAS que um usuário teria. É o que faz
        // .RequirePermission(...) funcionar igual para pessoa, client OAuth e chave de API.
        claims.AddRange(apiKey.Scopes.Select(scope => new Claim(AppClaims.Permission, scope)));

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, MachineAuth.ApiKeyScheme));

        return AuthenticateResult.Success(
            new AuthenticationTicket(principal, MachineAuth.ApiKeyScheme));
    }
}
