using System.Security.Claims;
using System.Text.Encodings.Web;
using Garius.Core.Authorization;
using Garius.Core.Machine;
using Garius.Infrastructure.Machine;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Garius.Api.Infrastructure.Authorization;

/// <summary>
/// Autentica uma <b>chave de API</b>, venha ela em <c>Authorization: Bearer gk_...</c> (o
/// caminho recomendado, e o que o mercado usa) ou no header <c>X-Api-Key</c>.
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
        // A chave chega por `Authorization: Bearer gk_...` (o caminho recomendado, que é o que
        // o mercado usa) ou pelo `X-Api-Key`. Quem sabe distinguir uma chave de um JWT no mesmo
        // header é o MachineAuth — e é o MESMO código que o forwarder de esquema usa para
        // mandar o request para cá. Ver MachineAuth.ExtractCredential.
        var kind = MachineAuth.ExtractCredential(
            Request.Headers.Authorization,
            Request.Headers[MachineAuth.ApiKeyHeader],
            out var key);

        if (kind != MachineCredentialKind.ApiKey)
        {
            // NoResult, não Fail: "este esquema não se aplica" — e não "a autenticação falhou".
            // Um Fail aqui abortaria a requisição de um usuário logado por cookie, que
            // legitimamente não manda chave nenhuma.
            return AuthenticateResult.NoResult();
        }

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
