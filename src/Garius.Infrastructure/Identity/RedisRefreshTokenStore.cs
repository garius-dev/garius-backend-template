using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Garius.Core.Identity;
using Garius.Infrastructure.Caching;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Garius.Infrastructure.Identity;

/// <summary>
/// Refresh tokens no Redis, com rotação atômica e detecção de reuso. Ver
/// <see cref="IRefreshTokenStore"/> para o porquê de cada decisão.
///
/// <para>
/// <b>O token nunca é gravado.</b> A chave do Redis é o SHA-256 dele — como se faz com senha.
/// Um dump do Redis não entrega tokens utilizáveis, apenas hashes.
/// </para>
/// </summary>
internal sealed class RedisRefreshTokenStore(
    IConnectionMultiplexer redis,
    RedisOptions options,
    TimeProvider timeProvider,
    ILogger<RedisRefreshTokenStore> logger) : IRefreshTokenStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Quanto tempo um token consumido continua rastreável.
    ///
    /// <para>
    /// <b>É isto que faz a detecção de reuso existir.</b> Um <c>DEL</c> no consumo seria o
    /// óbvio — e mataria a proteção em silêncio: um token roubado simplesmente pareceria
    /// "inválido", indistinguível de um expirado, e nunca se saberia que houve roubo. O token
    /// consumido é <b>marcado</b>, não apagado.
    /// </para>
    /// </summary>
    private static readonly TimeSpan ConsumedRetention = Lifetime;

    /// <summary>
    /// Consome o token e emite o próximo — <b>atomicamente</b>, em uma única ida ao Redis.
    ///
    /// <para>
    /// Sem o Lua, duas requisições concorrentes com o mesmo token leriam "ainda não usado" e
    /// ambas emitiriam um token novo: exatamente o replay que a detecção deveria pegar. O
    /// Redis executa o script inteiro sem intercalar nenhum outro comando. <b>Nenhuma leitura
    /// acontece fora do script</b> — uma consulta prévia reabriria a janela de corrida.
    /// </para>
    /// </summary>
    /// <remarks>
    /// KEYS[1] = hash do token atual · KEYS[2] = hash do token novo · KEYS[3] = prefixo das chaves.
    /// ARGV[1] = TTL do consumido · ARGV[2] = TTL do novo · ARGV[3] = hash do token novo (membro do set).
    /// Retorno: {status, userId, tenantId, familyId}, status ∈ OK | MISSING | REUSED.
    /// </remarks>
    private const string RotateScript = """
        local data = redis.call('HGETALL', KEYS[1])

        if #data == 0 then
            return {'MISSING', '', '', ''}
        end

        local token = {}
        for i = 1, #data, 2 do
            token[data[i]] = data[i + 1]
        end

        local familyKey = KEYS[3] .. ':rt-family:' .. token['familyId']

        -- Token JÁ consumido reaparecendo: ou foi roubado, ou o cliente legítimo perdeu a
        -- resposta da rotação anterior. Nos dois casos a resposta correta é a mesma —
        -- derrubar a sessão inteira.
        if token['consumed'] == '1' then
            local members = redis.call('SMEMBERS', familyKey)
            for _, member in ipairs(members) do
                redis.call('DEL', KEYS[3] .. ':rt:' .. member)
            end
            redis.call('DEL', familyKey)

            return {'REUSED', token['userId'], token['tenantId'], token['familyId']}
        end

        -- Marca como consumido (NÃO apaga — ver ConsumedRetention).
        redis.call('HSET', KEYS[1], 'consumed', '1')
        redis.call('EXPIRE', KEYS[1], ARGV[1])

        -- Emite o próximo com a MESMA família: é a sessão que continua, não uma nova.
        redis.call('HSET', KEYS[2],
            'userId',   token['userId'],
            'tenantId', token['tenantId'],
            'familyId', token['familyId'],
            'consumed', '0')
        redis.call('EXPIRE', KEYS[2], ARGV[2])

        redis.call('SADD', familyKey, ARGV[3])
        redis.call('EXPIRE', familyKey, ARGV[2])

        return {'OK', token['userId'], token['tenantId'], token['familyId']}
        """;

    public async Task<RefreshToken> IssueAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        var db = redis.GetDatabase();

        var token = GenerateToken();
        var hash = Hash(token);
        var familyId = Guid.CreateVersion7();

        await db.HashSetAsync(TokenKey(hash),
        [
            new HashEntry("userId", userId.ToString()),
            new HashEntry("tenantId", tenantId?.ToString() ?? string.Empty),
            new HashEntry("familyId", familyId.ToString()),
            new HashEntry("consumed", "0")
        ]);

        await db.KeyExpireAsync(TokenKey(hash), Lifetime);

        // A família guarda os tokens da sessão, para revogá-la inteira de uma vez.
        await db.SetAddAsync(FamilyKey(familyId.ToString()), hash);
        await db.KeyExpireAsync(FamilyKey(familyId.ToString()), Lifetime);

        // E o usuário guarda as suas famílias, para "sair de todos os dispositivos".
        await db.SetAddAsync(UserFamiliesKey(userId), familyId.ToString());
        await db.KeyExpireAsync(UserFamiliesKey(userId), Lifetime);

        return new RefreshToken(token, timeProvider.GetUtcNow().Add(Lifetime));
    }

    public async Task<(RefreshToken Token, RefreshTokenData Data)?> RotateAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var db = redis.GetDatabase();

        var newToken = GenerateToken();
        var newHash = Hash(newToken);

        var result = (RedisValue[]?)await db.ScriptEvaluateAsync(
            RotateScript,
            [TokenKey(Hash(token)), TokenKey(newHash), options.InstanceName],
            [
                (int)ConsumedRetention.TotalSeconds,
                (int)Lifetime.TotalSeconds,
                newHash
            ]);

        if (result is null || result.Length < 4)
        {
            return null;
        }

        switch (result[0].ToString())
        {
            case "MISSING":
                return null;

            case "REUSED":
                // A família já foi revogada dentro do script (atomicamente).
                logger.LogWarning(
                    "REUSO DE REFRESH TOKEN detectado. Usuário={UserId} Família={FamilyId}. " +
                    "A sessão inteira foi revogada — ou o token foi roubado, ou o cliente " +
                    "legítimo perdeu a resposta de uma rotação.",
                    result[1].ToString(),
                    result[3].ToString());

                return null;

            default:
                var data = new RefreshTokenData(
                    Guid.Parse(result[1].ToString(), CultureInfo.InvariantCulture),
                    Guid.TryParse(result[2].ToString(), out var tenant) ? tenant : null,
                    Guid.Parse(result[3].ToString(), CultureInfo.InvariantCulture));

                var issued = new RefreshToken(newToken, timeProvider.GetUtcNow().Add(Lifetime));

                return (issued, data);
        }
    }

    public async Task RevokeAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var db = redis.GetDatabase();

        var familyId = await db.HashGetAsync(TokenKey(Hash(token)), "familyId");

        if (!familyId.IsNullOrEmpty)
        {
            await RevokeFamilyAsync(familyId!);
        }
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var db = redis.GetDatabase();

        var families = await db.SetMembersAsync(UserFamiliesKey(userId));

        foreach (var family in families)
        {
            await RevokeFamilyAsync(family!);
        }

        await db.KeyDeleteAsync(UserFamiliesKey(userId));

        logger.LogInformation(
            "Todas as sessões do usuário {UserId} foram revogadas ({Count} famílias)",
            userId, families.Length);
    }

    /// <summary>Apaga todos os tokens da família — inclusive os já consumidos.</summary>
    private async Task RevokeFamilyAsync(string familyId)
    {
        var db = redis.GetDatabase();

        var hashes = await db.SetMembersAsync(FamilyKey(familyId));

        foreach (var hash in hashes)
        {
            await db.KeyDeleteAsync(TokenKey(hash!));
        }

        await db.KeyDeleteAsync(FamilyKey(familyId));
    }

    /// <summary>
    /// 256 bits de entropia criptográfica. O token é <b>opaco</b>: não carrega informação
    /// nenhuma, então interceptá-lo não revela usuário nem tenant.
    /// </summary>
    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
               .Replace('+', '-')
               .Replace('/', '_')
               .TrimEnd('=');

    /// <summary>
    /// O que vai para o Redis é o hash, nunca o token. Um dump do Redis não entrega tokens
    /// utilizáveis — mesma lógica de guardar o hash de uma senha.
    ///
    /// <para>
    /// SHA-256 puro basta aqui (não é preciso bcrypt/argon2): o token tem 256 bits de entropia
    /// criptográfica, então não há dicionário nem força bruta viável contra ele. O custo de um
    /// KDF lento seria pago a cada request, sem ganho.
    /// </para>
    /// </summary>
    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private string TokenKey(string hash) => $"{options.InstanceName}:rt:{hash}";

    private string FamilyKey(string familyId) => $"{options.InstanceName}:rt-family:{familyId}";

    private string UserFamiliesKey(Guid userId) => $"{options.InstanceName}:rt-user:{userId}";
}
