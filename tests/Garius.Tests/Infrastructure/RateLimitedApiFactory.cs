using Garius.Infrastructure.Caching;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Garius.Tests.Infrastructure;

/// <summary>
/// A API real, com o <b>rate limit LIGADO</b> e com limites baixos, para exercitá-lo.
///
/// <para>
/// A <see cref="ApiFactory"/> comum o mantém <b>desligado</b>: todos os testes rodam do mesmo
/// IP (localhost) e compartilhariam a mesma janela — o <c>AuthFlowTests</c> sozinho estouraria
/// o limite de <c>/auth/login</c>, e os testes seguintes falhariam com 429 por um motivo que
/// não tem nada a ver com o que eles testam. Pior: a falha mudaria conforme a <b>ordem</b> de
/// execução, que o xUnit não garante.
/// </para>
///
/// <para>
/// ⚠️ Esta fábrica está numa <b>collection própria</b> (<see cref="RateLimitCollection"/>), e
/// não na <see cref="ApiCollection"/> — ela sobe seus próprios containers e escreve as mesmas
/// variáveis de ambiente. Rodar em paralelo com a outra causaria a corrida descrita em
/// <see cref="ApiCollection"/>.
/// </para>
/// </summary>
public sealed class RateLimitedApiFactory : ApiFactory
{
    protected override void ConfigureRateLimit()
    {
        SetVariable("RateLimit__Enabled", "true");

        // Limites baixos: o teste precisa estourá-los em poucas requisições, sem depender de
        // tempo (um teste que precisa esperar 60 segundos é um teste que ninguém roda).
        //
        // O GLOBAL fica folgado de propósito: ele é outra camada, e um teste que estoura o
        // limite de /auth/login não pode ser barrado pelo global no meio do caminho — senão
        // não se saberia QUAL dos dois limites mordeu, e o teste provaria a coisa errada.
        SetVariable("RateLimit__Global__PermitLimit", "100");
        SetVariable("RateLimit__Global__WindowSeconds", "60");

        SetVariable("RateLimit__Login__PermitLimit", "2");
        SetVariable("RateLimit__Login__WindowSeconds", "60");

        SetVariable("RateLimit__Token__PermitLimit", "2");
        SetVariable("RateLimit__Token__WindowSeconds", "60");
    }

    /// <summary>
    /// Zera os contadores de rate limit no Redis.
    ///
    /// <para>
    /// Chamado no <b>início de cada teste</b>. Sem isto, os testes — que rodam em série, mas
    /// dentro da <b>mesma janela de 60 segundos</b> e do <b>mesmo IP</b> — herdariam a cota
    /// gasta pelo anterior: o segundo teste começaria com o limite de <c>/auth/login</c> já
    /// esgotado e receberia 429 na primeira requisição.
    /// </para>
    ///
    /// <para>
    /// A alternativa seria <c>Task.Delay(60s)</c> entre os testes — o que ninguém roda.
    /// </para>
    /// </summary>
    public async Task ResetRateLimitsAsync()
    {
        var redis = Services.GetRequiredService<IConnectionMultiplexer>();
        var options = Services.GetRequiredService<RedisOptions>();

        var endpoint = redis.GetEndPoints().First();
        var server = redis.GetServer(endpoint);

        // SCAN + DEL das chaves de rate limit desta instância. É caro em produção (por isso o
        // cache de permissões usa geração em vez de SCAN — ver RedisPermissionResolver), mas
        // num Redis de teste com um punhado de chaves é o caminho direto.
        foreach (var key in server.Keys(pattern: $"{options.InstanceName}:rl:*"))
        {
            await redis.GetDatabase().KeyDeleteAsync(key);
        }
    }
}

/// <summary>
/// As classes que exercitam o rate limit.
///
/// <para>
/// Separada da <see cref="ApiCollection"/> pelo mesmo motivo que ela existe: variável de
/// ambiente é estado global do processo, e duas fábricas rodando em paralelo se atropelam.
/// </para>
///
/// <para>
/// ⚠️ <b><c>DisableParallelization</c> é obrigatório aqui</b>, e a razão é a própria natureza do
/// que se testa. O rate limit particiona por <b>IP</b>, e <b>todos os testes rodam do mesmo IP</b>
/// (localhost) — então eles compartilham a mesma janela de contagem. Rodando em paralelo, um
/// teste consome a cota do outro: o primeiro request de um teste já volta 429 porque outro
/// teste, num thread vizinho, esgotou o limite de <c>/auth/login</c> primeiro.
/// </para>
///
/// <para>
/// Não dá para contornar forjando o IP (mandar um <c>CF-Connecting-IP</c> diferente por teste):
/// o <c>RealIpMiddleware</c> só acredita nesse header se o request vier de um range REAL da
/// Cloudflare — que é precisamente a defesa contra um atacante que forja o header para escapar
/// do rate limit. A defesa funcionando é o que impede o atalho.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RateLimitCollection : ICollectionFixture<RateLimitedApiFactory>
{
    public const string Name = "RateLimit";
}
