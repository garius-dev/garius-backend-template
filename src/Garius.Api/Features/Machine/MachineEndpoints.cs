using Garius.Api.Infrastructure.Authorization;
using Garius.Api.Infrastructure.Errors;
using Garius.Api.Infrastructure.Validation;
using Garius.Core.Machine;

// O identificador `Permissions`, aqui dentro, resolveria para o NAMESPACE
// Garius.Api.Features.Permissions — não para a classe Garius.Core.Authorization.Permissions.
// O alias desfaz a ambiguidade sem obrigar a qualificar o nome inteiro a cada uso.
using AppPermissions = Garius.Core.Authorization.Permissions;

namespace Garius.Api.Features.Machine;

public static class MachineEndpoints
{
    public static void MapMachineEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        MapTokenEndpoint(app);
        MapClientAdministration(app);
    }

    /// <summary>
    /// <c>POST /auth/token</c> — OAuth2 <i>client credentials</i> (RFC 6749, §4.4).
    /// </summary>
    private static void MapTokenEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/token", async (
            TokenRequest request,
            MachineAuthService machine,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await machine.IssueTokenAsync(request, ct);

            // ⚠️ ESTE ENDPOINT NÃO USA O ENVELOPE PADRÃO DA API.
            //
            // É a única exceção deliberada ao contrato de resposta, e ela tem uma razão: a RFC
            // 6749 define o formato exato da resposta de token, e TODA biblioteca OAuth do
            // mundo espera encontrar `access_token` na RAIZ do JSON. Embrulhá-lo no nosso
            // `{ data: ... }` tornaria a API incompatível com qualquer cliente padrão — e o
            // integrador teria de escrever um parser à mão para falar com a gente.
            //
            // Consistência interna não vale mais do que interoperabilidade num protocolo
            // público. Nos ERROS vale o mesmo (RFC 6749, §5.2).
            if (result.IsSuccess)
            {
                return Results.Ok(result.Value);
            }

            var error = result.Error!;

            return Results.Json(
                new TokenErrorResponse(error.Code, error.Message),
                statusCode: error.Code == "unsupported_grant_type"
                    ? StatusCodes.Status400BadRequest
                    : StatusCodes.Status401Unauthorized);
        })
        .AllowAnonymous()
        .WithTags("Auth")
        .WithSummary("Emite um token de acesso M2M (OAuth2 client credentials).")
        .WithDescription(
            "Troca client_id + client_secret por um JWT de curta duração. " +
            "A resposta segue a RFC 6749 (access_token/token_type/expires_in na raiz), " +
            "NÃO o envelope padrão da API — é a única exceção, e existe para que clientes " +
            "OAuth padrão funcionem sem adaptação. " +
            "Use o token em `Authorization: Bearer <token>`.");
    }

    /// <summary>
    /// Administração das credenciais de máquina. Exige <c>clients.*</c> — ver o comentário em
    /// <see cref="AppPermissions.Clients"/> sobre por que essa permissão é perigosa.
    /// </summary>
    private static void MapClientAdministration(IEndpointRouteBuilder app)
    {
        // Ver AuthEndpoints: a validação é automática para todo request que tenha validator.
        //
        // Note que o POST /auth/token (mapeado FORA deste grupo, em MapTokenEndpoint) NÃO é
        // coberto — e é deliberado: ele segue a RFC 6749, cujos erros saem como
        // { "error": "invalid_client" }, não como o ProblemDetails da API. Ver MachineValidators.
        var group = app.MapGroup("/machine").WithTags("Machine").ValidateRequests();

        // --- clients OAuth ---

        group.MapPost("/clients", async (
            CreateOAuthClientRequest request,
            MachineAuthService machine,
            HttpContext http,
            CancellationToken ct) =>
                (await machine.CreateClientAsync(request, ct)).ToHttpResult(http))
        .RequirePermission(AppPermissions.Clients.Create)
        .WithSummary("Cria um client M2M.")
        .WithDescription(
            "O client_secret vem na resposta e NUNCA MAIS: o banco só guarda o hash. " +
            "Perdeu, crie outro client. " +
            "Não é possível conceder um escopo que você mesmo não possui — seria uma escalada " +
            "de privilégio em dois passos.");

        group.MapGet("/clients", async (
            MachineAuthService machine,
            HttpContext http,
            CancellationToken ct) =>
                (await machine.ListClientsAsync(ct)).ToHttpResult(http))
        .RequirePermission(AppPermissions.Clients.Read)
        .WithSummary("Lista os clients M2M (sem os segredos).");

        group.MapDelete("/clients/{id:guid}", async (
            Guid id,
            MachineAuthService machine,
            HttpContext http,
            CancellationToken ct) =>
                (await machine.RevokeClientAsync(id, ct)).ToHttpResult(http))
        .RequirePermission(AppPermissions.Clients.Revoke)
        .WithSummary("Revoga um client M2M.")
        .WithDescription(
            "⚠️ Os JWTs JÁ EMITIDOS continuam válidos até expirarem (até Jwt:LifetimeMinutes, " +
            "1h por padrão). É a consequência de um token stateless. Uma chave de API, essa " +
            "sim, morre na hora.");

        // --- chaves de API ---

        group.MapPost("/api-keys", async (
            CreateApiKeyRequest request,
            MachineAuthService machine,
            HttpContext http,
            CancellationToken ct) =>
                (await machine.CreateApiKeyAsync(request, ct)).ToHttpResult(http))
        .RequirePermission(AppPermissions.Clients.Create)
        .WithSummary("Cria uma chave de API para um terceiro.")
        .WithDescription(
            "A chave vem na resposta e NUNCA MAIS. Enviada em `X-Api-Key: <chave>`. " +
            "Defina um callLimit: é o que transforma um vazamento silencioso e ilimitado " +
            "num vazamento que trava e aparece.");

        group.MapGet("/api-keys", async (
            MachineAuthService machine,
            HttpContext http,
            CancellationToken ct) =>
                (await machine.ListApiKeysAsync(ct)).ToHttpResult(http))
        .RequirePermission(AppPermissions.Clients.Read)
        .WithSummary("Lista as chaves de API (prefixo, escopos e consumo da quota).");

        group.MapDelete("/api-keys/{id:guid}", async (
            Guid id,
            MachineAuthService machine,
            HttpContext http,
            CancellationToken ct) =>
                (await machine.RevokeApiKeyAsync(id, ct)).ToHttpResult(http))
        .RequirePermission(AppPermissions.Clients.Revoke)
        .WithSummary("Revoga uma chave de API. O efeito é imediato.");
    }
}
