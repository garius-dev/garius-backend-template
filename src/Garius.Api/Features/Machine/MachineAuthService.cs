using Garius.Core.Authorization;
using Garius.Core.Machine;
using Garius.Core.Results;
using Garius.Core.Security;
using Garius.Core.Tenancy;
using Garius.Infrastructure.Database;
using Garius.Infrastructure.Machine;
using Microsoft.EntityFrameworkCore;


namespace Garius.Api.Features.Machine;

/// <summary>
/// Emissão de token M2M e administração das credenciais de máquina.
/// </summary>
public sealed class MachineAuthService(
    MachineClientStore store,
    JwtIssuer jwtIssuer,
    IPermissionResolver permissions,
    ITenantResolver tenantResolver,
    ICurrentUser currentUser,
    AppDbContext db,
    ILogger<MachineAuthService> logger)
{
    /// <summary>
    /// OAuth2 <i>client credentials</i>: troca <c>client_id</c> + <c>client_secret</c> por um JWT.
    /// </summary>
    public async Task<Result<TokenResponse>> IssueTokenAsync(
        TokenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(
                request.GrantType, MachineAuth.ClientCredentialsGrant, StringComparison.Ordinal))
        {
            return Error.Validation(
                "unsupported_grant_type",
                $"Apenas '{MachineAuth.ClientCredentialsGrant}' é suportado.");
        }

        var client = await store.AuthenticateClientAsync(
            request.ClientId, request.ClientSecret, cancellationToken);

        // Uma resposta só para client inexistente, segredo errado, expirado e revogado — ver
        // MachineClientStore.AuthenticateClientAsync.
        if (client is null)
        {
            return Error.Unauthorized(
                "invalid_client", "client_id ou client_secret inválidos.");
        }

        var token = jwtIssuer.Issue(client);

        return new TokenResponse(
            token.AccessToken,
            token.TokenType,
            token.ExpiresInSeconds,
            token.Scope);
    }

    // --- administração -------------------------------------------------------

    public async Task<Result<CreateOAuthClientResponse>> CreateClientAsync(
        CreateOAuthClientRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scopes = await ValidateScopesAsync(request.Scopes, cancellationToken);

        if (!scopes.IsSuccess)
        {
            return Result<CreateOAuthClientResponse>.Failure(scopes.Error!);
        }

        var secret = MachineCredential.Generate();

        var client = new OAuthClient
        {
            // O client_id é público e não precisa ser secreto — mas ainda assim é aleatório, e
            // não derivado do nome. Um id previsível ("acme-integration") entrega ao atacante
            // metade do par de credenciais de graça.
            ClientId = $"cid_{MachineCredential.Generate()[..24]}",
            SecretHash = MachineCredential.Hash(secret),
            Name = request.Name,
            Scopes = [.. request.Scopes],
            TenantId = ResolveTenant(),
            ExpiresAt = request.ExpiresAt
        };

        db.OAuthClients.Add(client);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "{Audit} m2m.client_created Client={ClientId} Nome={Name} Escopos={Scopes} Por={UserId}",
            "AUTH", client.ClientId, client.Name, string.Join(',', client.Scopes), currentUser.UserId);

        // O SEGREDO EM CLARO, uma única vez. Depois desta resposta ele não existe mais em
        // lugar nenhum — nem no banco, nem no log (note que o log acima grava o client_id, não
        // o segredo).
        return new CreateOAuthClientResponse(
            client.Id,
            client.ClientId,
            secret,
            client.Name,
            client.Scopes,
            client.ExpiresAt);
    }

    public async Task<Result<CreateApiKeyResponse>> CreateApiKeyAsync(
        CreateApiKeyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scopes = await ValidateScopesAsync(request.Scopes, cancellationToken);

        if (!scopes.IsSuccess)
        {
            return Result<CreateApiKeyResponse>.Failure(scopes.Error!);
        }

        var key = MachineCredential.GenerateApiKey();

        var apiKey = new ApiKey
        {
            KeyPrefix = MachineCredential.PrefixOf(key),
            KeyHash = MachineCredential.Hash(key),
            Name = request.Name,
            Scopes = [.. request.Scopes],
            TenantId = ResolveTenant(),
            CallLimit = request.CallLimit,
            ExpiresAt = request.ExpiresAt
        };

        db.ApiKeys.Add(apiKey);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "{Audit} apikey.created Chave={KeyPrefix} Nome={Name} Escopos={Scopes} Por={UserId}",
            "AUTH", apiKey.KeyPrefix, apiKey.Name, string.Join(',', apiKey.Scopes), currentUser.UserId);

        return new CreateApiKeyResponse(
            apiKey.Id,
            key,
            apiKey.Name,
            apiKey.Scopes,
            apiKey.CallLimit,
            apiKey.ExpiresAt);
    }

    public async Task<Result<IReadOnlyList<OAuthClientDto>>> ListClientsAsync(
        CancellationToken cancellationToken)
    {
        var clients = await db.OAuthClients
            .OrderBy(c => c.Name)
            .Select(c => new OAuthClientDto(
                c.Id, c.ClientId, c.Name, c.Scopes, c.ExpiresAt, c.LastUsedAt))
            .ToListAsync(cancellationToken);

        return Result<IReadOnlyList<OAuthClientDto>>.Success(clients);
    }

    public async Task<Result<IReadOnlyList<ApiKeyDto>>> ListApiKeysAsync(
        CancellationToken cancellationToken)
    {
        var keys = await db.ApiKeys
            .OrderBy(k => k.Name)
            .Select(k => new ApiKeyDto(
                k.Id, k.KeyPrefix, k.Name, k.Scopes,
                k.CallLimit, k.CallCount, k.ExpiresAt, k.LastUsedAt))
            .ToListAsync(cancellationToken);

        return Result<IReadOnlyList<ApiKeyDto>>.Success(keys);
    }

    /// <summary>
    /// Revoga um client — soft delete (<c>Enabled = false</c>), como todo o resto do template.
    ///
    /// <para>
    /// ⚠️ <b>Os JWTs já emitidos continuam valendo até expirarem.</b> Isto não é um descuido: é
    /// a consequência inevitável de um token stateless, que é o que o torna barato (nenhuma ida
    /// ao banco por request). A janela é o <c>Jwt:LifetimeMinutes</c> — uma hora, por padrão.
    /// Se uma revogação verdadeiramente imediata for necessária, o caminho é uma blacklist de
    /// <c>jti</c> no Redis consultada a cada request; e aí o token deixa de ser stateless, e o
    /// custo volta. Uma chave de API, por consultar o banco a cada chamada, <b>morre na hora</b>.
    /// </para>
    /// </summary>
    public async Task<Result> RevokeClientAsync(Guid id, CancellationToken cancellationToken)
    {
        var client = await db.OAuthClients.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (client is null)
        {
            return Error.NotFound("client.not_found", "Client não encontrado.");
        }

        client.Enabled = false;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "{Audit} m2m.client_revoked Client={ClientId} Por={UserId}",
            "AUTH", client.ClientId, currentUser.UserId);

        return Result.Success();
    }

    /// <summary>Revoga uma chave de API. Diferente do JWT, o efeito é <b>imediato</b>.</summary>
    public async Task<Result> RevokeApiKeyAsync(Guid id, CancellationToken cancellationToken)
    {
        var apiKey = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, cancellationToken);

        if (apiKey is null)
        {
            return Error.NotFound("apikey.not_found", "Chave não encontrada.");
        }

        apiKey.Enabled = false;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "{Audit} apikey.revoked Chave={KeyPrefix} Por={UserId}",
            "AUTH", apiKey.KeyPrefix, currentUser.UserId);

        return Result.Success();
    }

    // --- interno -------------------------------------------------------------

    /// <summary>
    /// Duas checagens, e a segunda é a que importa.
    ///
    /// <list type="number">
    ///   <item><b>O escopo existe?</b> Um <c>invocies.read</c> (com erro de digitação) gravado
    ///         no banco daria um client que nunca autoriza nada, e ninguém entenderia por quê.</item>
    ///
    ///   <item><b>Quem cria TEM o escopo que está concedendo?</b> Sem isto, qualquer usuário com
    ///         <c>clients.create</c> poderia criar um client com escopo <c>*</c>, autenticar-se
    ///         com ele e passar a ser superadministrador — uma <b>escalada de privilégio</b> em
    ///         dois passos, aberta por uma permissão que parecia inócua. Ninguém pode delegar a
    ///         uma máquina um poder que não tem.</item>
    /// </list>
    /// </summary>
    private async Task<Result> ValidateScopesAsync(
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken)
    {
        if (scopes.Count == 0)
        {
            return Error.Validation(
                "client.no_scopes",
                "Um client sem escopo nenhum não consegue fazer nada. Informe ao menos um.");
        }

        var unknown = scopes
            .Where(scope => scope != Permissions.SuperAdmin && !Permissions.Exists(scope))
            .ToList();

        if (unknown.Count > 0)
        {
            return Error.Validation(
                "client.unknown_scope",
                $"Escopo(s) inexistente(s): {string.Join(", ", unknown)}.");
        }

        var userId = currentUser.UserId;

        if (userId is null)
        {
            return Error.Unauthorized();
        }

        var granted = await permissions.GetPermissionsAsync(
            userId.Value, tenantResolver.CurrentTenantId, cancellationToken);

        // Matches trata o curinga: quem tem `*` pode conceder qualquer coisa; quem tem
        // `invoices.*` pode conceder `invoices.read`, mas não `users.delete`.
        var forbidden = scopes
            .Where(scope => !granted.Any(g => Permission.Matches(g, scope)))
            .ToList();

        if (forbidden.Count > 0)
        {
            logger.LogWarning(
                "{Audit} m2m.scope_escalation_blocked Usuário={UserId} Escopos={Scopes}. " +
                "Tentou conceder a uma máquina permissões que ele próprio não tem.",
                "AUTH", userId, string.Join(',', forbidden));

            return Error.Forbidden(
                "client.scope_escalation",
                "Você não pode conceder permissões que não possui: " +
                $"{string.Join(", ", forbidden)}.");
        }

        return Result.Success();
    }

    /// <summary>
    /// O tenant da credencial é o tenant de <b>quem a cria</b> — nunca um id vindo do corpo da
    /// requisição, que seria trivialmente forjável para criar um client dentro de outro tenant.
    ///
    /// <para>
    /// Em single-tenant isto devolve o tenant padrão, e ninguém precisa pensar no assunto.
    /// </para>
    /// </summary>
    private Guid ResolveTenant() =>
        tenantResolver.CurrentTenantId
        ?? throw new InvalidOperationException(
            "Não há tenant no contexto. Criar uma credencial de máquina exige um request " +
            "autenticado — nunca um job de sistema, que enxerga todos os tenants.");
}
