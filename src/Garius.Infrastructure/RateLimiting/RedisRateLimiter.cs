using Garius.Infrastructure.Caching;
using StackExchange.Redis;

namespace Garius.Infrastructure.RateLimiting;

/// <summary>
/// Contador de rate limit <b>no Redis</b> — compartilhado por todas as réplicas.
///
/// <para>
/// <b>Por que não o <c>RateLimiter</c> nativo do .NET.</b> Ele guarda os contadores <b>em
/// memória, por processo</b>. Com N réplicas atrás do balanceador, o limite efetivo vira
/// <b>N × o limite configurado</b> — e o atacante nem precisa saber disso: o balanceador
/// espalha as tentativas dele sozinho. Um limite de 5 tentativas de login viraria 15 com três
/// réplicas, em silêncio. Pior: o número muda quando se escala a aplicação, então o teste que
/// passou com uma réplica não diz nada sobre produção.
/// </para>
///
/// <para>
/// <b>Janela deslizante, não fixa.</b> Uma janela fixa ("100 por minuto, zerando no minuto
/// cheio") permite o dobro do limite na virada: 100 requisições em 23:59:59 e mais 100 em
/// 00:00:01. A janela deslizante conta as requisições dos últimos N segundos <i>a partir de
/// agora</i>, e não tem virada para explorar.
/// </para>
/// </summary>
public sealed class RedisRateLimiter(IConnectionMultiplexer redis, RedisOptions options)
{
    /// <summary>
    /// Conta e decide, <b>atomicamente</b>, em uma única ida ao Redis.
    ///
    /// <para>
    /// O script é a parte que não pode ser feita com comandos soltos: um <c>ZCARD</c> seguido
    /// de um <c>ZADD</c> deixaria uma janela entre a contagem e o registro em que N requisições
    /// concorrentes leriam todas "ainda dentro do limite" e passariam todas. É exatamente o
    /// burst que o rate limit deveria conter — e ele some justamente sob carga, que é quando
    /// importa.
    /// </para>
    ///
    /// <para>
    /// A estrutura é um <b>sorted set</b> em que o score é o timestamp: remover o que saiu da
    /// janela é um <c>ZREMRANGEBYSCORE</c>, e contar o que sobrou é um <c>ZCARD</c>.
    /// </para>
    /// </summary>
    /// <remarks>
    /// KEYS[1] = a chave do contador.
    /// ARGV[1] = agora (ms) · ARGV[2] = tamanho da janela (ms) · ARGV[3] = limite
    /// ARGV[4] = membro único desta requisição (evita colisão de dois hits no mesmo ms).
    /// Retorno: { permitido (0|1), contagem, retryAfterMs }.
    /// </remarks>
    private const string SlidingWindowScript = """
        local now      = tonumber(ARGV[1])
        local window   = tonumber(ARGV[2])
        local limit    = tonumber(ARGV[3])
        local member   = ARGV[4]

        -- Descarta o que já saiu da janela.
        redis.call('ZREMRANGEBYSCORE', KEYS[1], 0, now - window)

        local count = redis.call('ZCARD', KEYS[1])

        if count >= limit then
            -- Estourou. Descobre quando a requisição MAIS ANTIGA sai da janela — é só nesse
            -- instante que abre uma vaga. É o que vai no header Retry-After.
            local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
            local retryAfter = window

            if oldest[2] then
                retryAfter = (tonumber(oldest[2]) + window) - now
            end

            if retryAfter < 0 then
                retryAfter = 0
            end

            -- NÃO registra a requisição bloqueada: contá-la faria um atacante insistente
            -- manter a janela sempre cheia, e o cliente legítimo nunca mais entraria.
            return { 0, count, retryAfter }
        end

        redis.call('ZADD', KEYS[1], now, member)

        -- O TTL evita que a chave de um IP que nunca mais volta fique no Redis para sempre.
        redis.call('PEXPIRE', KEYS[1], window)

        return { 1, count + 1, 0 }
        """;

    public async Task<RateLimitResult> CheckAsync(
        string partition,
        int limit,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        var db = redis.GetDatabase();

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var result = (RedisValue[]?)await db.ScriptEvaluateAsync(
            SlidingWindowScript,
            [$"{options.InstanceName}:rl:{partition}"],
            [
                now,
                (long)window.TotalMilliseconds,
                limit,
                // Membro único: duas requisições no MESMO milissegundo teriam o mesmo score, e
                // um sorted set descarta membros duplicados — a segunda não seria contada.
                $"{now}-{Guid.NewGuid():N}"
            ]);

        if (result is null || result.Length < 3)
        {
            // Falha ao falar com o Redis. FAIL-OPEN, deliberadamente.
            //
            // É a única escolha aqui, e é desconfortável: um Redis fora do ar deixaria a API
            // sem rate limit. Mas a alternativa (fail-closed) transformaria uma falha do Redis
            // numa INDISPONIBILIDADE TOTAL da aplicação — todo request seria bloqueado.
            //
            // Na prática o dilema é teórico: o Redis é dependência OBRIGATÓRIA (a app nem sobe
            // sem ele) e o refresh token depende dele, então um Redis fora do ar já derruba a
            // autenticação de qualquer forma. Ver RedisExtensions.
            return RateLimitResult.Allowed(0);
        }

        var permitted = (long)result[0] == 1;
        var count = (int)(long)result[1];

        return permitted
            ? RateLimitResult.Allowed(count)
            : RateLimitResult.Blocked(count, TimeSpan.FromMilliseconds((long)result[2]));
    }
}

/// <param name="IsAllowed">A requisição pode prosseguir?</param>
/// <param name="Count">Quantas requisições há na janela.</param>
/// <param name="RetryAfter">Quando tentar de novo. Vai no header <c>Retry-After</c>.</param>
public sealed record RateLimitResult(bool IsAllowed, int Count, TimeSpan RetryAfter)
{
    public static RateLimitResult Allowed(int count) => new(true, count, TimeSpan.Zero);

    public static RateLimitResult Blocked(int count, TimeSpan retryAfter) =>
        new(false, count, retryAfter);
}
