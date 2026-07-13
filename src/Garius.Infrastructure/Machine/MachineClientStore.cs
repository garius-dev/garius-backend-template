using Garius.Core.Machine;
using Garius.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Garius.Infrastructure.Machine;

/// <summary>
/// Valida as credenciais de máquina contra o banco. É o único lugar que compara segredos —
/// e o único que sabe que uma credencial pode estar expirada, revogada ou sem quota.
/// </summary>
public sealed class MachineClientStore(
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger<MachineClientStore> logger)
{
    /// <summary>
    /// Autentica um <c>client_id</c> + <c>client_secret</c>.
    ///
    /// <para>
    /// Devolve <c>null</c> em <b>qualquer</b> falha — client inexistente, segredo errado,
    /// expirado ou revogado. O chamador não sabe qual: distinguir "este client não existe" de
    /// "o segredo está errado" transformaria o endpoint num oráculo que enumera os client_ids
    /// válidos da aplicação. É a mesma razão pela qual o login não diz se o e-mail existe.
    /// </para>
    /// </summary>
    public async Task<OAuthClient?> AuthenticateClientAsync(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return null;
        }

        // IgnoreQueryFilters no TENANT: quem se autentica ainda não TEM tenant — é o próprio
        // client que o traz. Com o filtro ligado, o ClaimsTenantResolver devolveria null num
        // request anônimo (o que já não filtraria nada) mas em single-tenant devolveria o
        // tenant padrão — e um client de outro tenant simplesmente "não existiria". O filtro de
        // soft delete precisa continuar valendo, então ele é reaplicado à mão logo abaixo.
        var client = await db.OAuthClients
            .IgnoreQueryFilters()
            .Where(c => c.Enabled)
            .FirstOrDefaultAsync(c => c.ClientId == clientId, cancellationToken);

        if (client is null)
        {
            // Gasta o mesmo tempo de um hash real. Sem isto, um client_id inexistente responde
            // mais rápido do que um existente com segredo errado — e a diferença enumera os
            // client_ids válidos, exatamente o que a resposta genérica tenta evitar.
            MachineCredential.Verify(clientSecret, DummyHash);

            return null;
        }

        if (!MachineCredential.Verify(clientSecret, client.SecretHash))
        {
            logger.LogWarning(
                "{Audit} m2m.invalid_secret Client={ClientId}", "AUTH", clientId);

            return null;
        }

        var now = timeProvider.GetUtcNow();

        if (client.ExpiresAt is not null && now >= client.ExpiresAt)
        {
            logger.LogWarning("{Audit} m2m.expired Client={ClientId}", "AUTH", clientId);

            return null;
        }

        client.LastUsedAt = now;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("{Audit} m2m.token_issued Client={ClientId}", "AUTH", clientId);

        return client;
    }

    /// <summary>
    /// Autentica uma chave de API e <b>consome uma unidade da quota</b>.
    ///
    /// <para>
    /// Note que a busca é pelo <b>prefixo</b> (indexado) e a confirmação é pelo <b>hash
    /// completo</b>, em tempo constante. O prefixo não é único: sem a confirmação do hash,
    /// uma colisão de 8 caracteres autenticaria a chave errada.
    /// </para>
    /// </summary>
    public async Task<ApiKey?> AuthenticateApiKeyAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var prefix = MachineCredential.PrefixOf(key);
        var hash = MachineCredential.Hash(key);

        // Ver o comentário em AuthenticateClientAsync sobre o IgnoreQueryFilters.
        var candidates = await db.ApiKeys
            .IgnoreQueryFilters()
            .Where(k => k.Enabled && k.KeyPrefix == prefix)
            .ToListAsync(cancellationToken);

        // FixedTimeEquals, não ==: ver MachineCredential.Verify.
        var apiKey = candidates.FirstOrDefault(k => MachineCredential.Verify(key, k.KeyHash));

        if (apiKey is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();

        if (apiKey.IsExpired(now))
        {
            logger.LogWarning(
                "{Audit} apikey.expired Chave={KeyPrefix} Nome={Name}",
                "AUTH", apiKey.KeyPrefix, apiKey.Name);

            return null;
        }

        if (apiKey.IsExhausted)
        {
            logger.LogWarning(
                "{Audit} apikey.quota_exhausted Chave={KeyPrefix} Nome={Name} Limite={CallLimit}",
                "AUTH", apiKey.KeyPrefix, apiKey.Name, apiKey.CallLimit);

            return null;
        }

        // Incremento ATÔMICO, no banco — não `apiKey.CallCount++` seguido de SaveChanges.
        //
        // Duas chamadas concorrentes com a mesma chave leriam o mesmo valor e gravariam o mesmo
        // incremento: a quota andaria 1 em vez de 2, e uma chave com limite de 1000 poderia
        // fazer muito mais que isso sob concorrência. O ExecuteUpdate emite um único
        // `UPDATE ... SET call_count = call_count + 1`, que o Postgres resolve sem corrida.
        //
        // O efeito colateral é que a quota é aproximada NA BORDA: uma chamada pode passar
        // exatamente quando a quota estoura. Para um teto de uso (não um rate limit de
        // segurança), errar por um é aceitável — errar por N réplicas concorrentes não seria.
        await db.ApiKeys
            .IgnoreQueryFilters()
            .Where(k => k.Id == apiKey.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(k => k.CallCount, k => k.CallCount + 1)
                    .SetProperty(k => k.LastUsedAt, now),
                cancellationToken);

        return apiKey;
    }

    /// <summary>
    /// Um hash de formato válido, para gastar o mesmo tempo quando o client não existe.
    /// Ver <c>AuthService.FakePasswordCheckAsync</c> — a mesma defesa contra enumeração.
    /// </summary>
    private const string DummyHash =
        "0000000000000000000000000000000000000000000000000000000000000000";
}
