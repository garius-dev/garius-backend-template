using System.Text.Json;
using Garius.Infrastructure.Caching;
using StackExchange.Redis;

namespace Garius.Infrastructure.Idempotency;

/// <summary>
/// Idempotência de requisições, no Redis.
///
/// <para>
/// <b>O problema.</b> O cliente manda <c>POST /pedidos</c>, a rede cai antes da resposta
/// chegar, e ele repete. Sem idempotência, nascem <b>dois pedidos</b> — e o cliente não tem
/// como saber, porque para ele a primeira tentativa "falhou". É o bug clássico de duplo
/// clique, de retry automático e de webhook reentregue.
/// </para>
///
/// <para>
/// <b>A solução.</b> O cliente manda um <c>Idempotency-Key</c>. A primeira requisição com
/// aquela chave executa e tem a resposta <b>gravada</b>; as repetições recebem a <b>mesma
/// resposta</b>, sem reexecutar nada.
/// </para>
/// </summary>
public sealed class RedisIdempotencyStore(IConnectionMultiplexer redis, RedisOptions options)
{
    /// <summary>
    /// Quanto tempo uma resposta fica guardada. 24h cobre com folga qualquer retry automático
    /// (que acontece em segundos ou minutos) sem encher o Redis indefinidamente.
    /// </summary>
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>
    /// Quanto tempo uma requisição pode ficar "em andamento" antes de a reserva ser
    /// considerada abandonada.
    ///
    /// <para>
    /// Existe porque um processo pode <b>morrer no meio</b> (deploy, OOM, crash). Sem este
    /// TTL, a chave ficaria travada em <c>InProgress</c> para sempre e o cliente <b>nunca mais
    /// conseguiria</b> executar aquela requisição — nem repetindo, nem esperando. Um minuto é
    /// mais do que qualquer requisição HTTP sadia leva.
    /// </para>
    /// </summary>
    private static readonly TimeSpan InProgressTimeout = TimeSpan.FromMinutes(1);

    private const string InProgressMarker = "__in_progress__";

    /// <summary>
    /// Tenta <b>reservar</b> a chave — atomicamente.
    ///
    /// <para>
    /// O <c>SET NX</c> é o coração da coisa: ou este request é o primeiro (e reserva), ou já
    /// existe algo ali. Sem a atomicidade, dois cliques simultâneos fariam os dois requests
    /// lerem "não existe" e os dois executarem — <b>exatamente a duplicação que a idempotência
    /// deveria impedir</b>, e que só se manifesta sob concorrência (ou seja: nunca em teste
    /// manual, sempre em produção).
    /// </para>
    /// </summary>
    /// <returns>
    /// <see cref="IdempotencyState.New"/> se reservou (pode executar);
    /// <see cref="IdempotencyState.InProgress"/> se outra requisição idêntica está executando
    /// AGORA; ou <see cref="IdempotencyState.Completed"/>, com a resposta gravada.
    /// </returns>
    public async Task<IdempotencyReservation> TryReserveAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        var db = redis.GetDatabase();
        var redisKey = BuildKey(key);

        // SET key __in_progress__ NX PX <timeout>
        var reserved = await db.StringSetAsync(
            redisKey,
            InProgressMarker,
            InProgressTimeout,
            When.NotExists);

        if (reserved)
        {
            return new IdempotencyReservation(IdempotencyState.New, null);
        }

        var existing = await db.StringGetAsync(redisKey);

        if (existing.IsNullOrEmpty)
        {
            // Expirou entre o SET NX e o GET. Raríssimo, mas possível — e tratar como uma
            // requisição nova é o comportamento correto (a reserva anterior morreu).
            return new IdempotencyReservation(IdempotencyState.New, null);
        }

        if ((string)existing! == InProgressMarker)
        {
            // Uma requisição idêntica está executando NESTE MOMENTO. Não dá para devolver a
            // resposta (ela ainda não existe) nem para executar de novo (duplicaria). O 409 é
            // a resposta honesta: "repita daqui a pouco".
            return new IdempotencyReservation(IdempotencyState.InProgress, null);
        }

        var stored = JsonSerializer.Deserialize<StoredResponse>((string)existing!);

        return new IdempotencyReservation(IdempotencyState.Completed, stored);
    }

    /// <summary>
    /// Grava a resposta, substituindo a reserva. A partir daqui, toda repetição da mesma chave
    /// recebe esta resposta sem reexecutar nada.
    /// </summary>
    public async Task CompleteAsync(
        string key,
        int statusCode,
        string body,
        CancellationToken cancellationToken = default)
    {
        var db = redis.GetDatabase();

        await db.StringSetAsync(
            BuildKey(key),
            JsonSerializer.Serialize(new StoredResponse(statusCode, body)),
            Retention);
    }

    /// <summary>
    /// Libera a reserva quando a requisição <b>falhou</b>.
    ///
    /// <para>
    /// <b>Um erro não pode ser gravado como resposta idempotente.</b> Se um 500 (banco fora do
    /// ar, por exemplo) ficasse guardado por 24h, o cliente receberia aquele mesmo 500 a cada
    /// retry — para sempre, mesmo depois de o banco voltar. A operação ficaria envenenada pela
    /// chave, e nada no sistema explicaria por quê. Falhou: apaga a reserva e deixa o cliente
    /// tentar de novo de verdade.
    /// </para>
    /// </summary>
    public async Task ReleaseAsync(string key, CancellationToken cancellationToken = default)
    {
        await redis.GetDatabase().KeyDeleteAsync(BuildKey(key));
    }

    private string BuildKey(string key) => $"{options.InstanceName}:idem:{key}";
}

public enum IdempotencyState
{
    /// <summary>Primeira vez: pode executar.</summary>
    New,

    /// <summary>Uma requisição idêntica está executando agora. Responder 409.</summary>
    InProgress,

    /// <summary>Já executou: devolver a resposta guardada, sem reexecutar.</summary>
    Completed
}

/// <param name="State">O que fazer com esta requisição.</param>
/// <param name="Response">A resposta guardada, quando <see cref="IdempotencyState.Completed"/>.</param>
public sealed record IdempotencyReservation(IdempotencyState State, StoredResponse? Response);

/// <param name="StatusCode">O status HTTP da resposta original.</param>
/// <param name="Body">O corpo da resposta original, tal como foi enviado.</param>
public sealed record StoredResponse(int StatusCode, string Body);
