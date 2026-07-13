using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Garius.Tests.Infrastructure;

/// <summary>
/// Sobe Postgres e Redis REAIS, efêmeros, para os testes de integração.
///
/// Não usamos banco em memória nem mock: o template depende de comportamento específico
/// do Postgres (índice único parcial, timestamptz, uuid) e do Redis (TTL, scripts Lua).
/// Testar contra um substituto validaria o substituto, não o que vai para produção.
///
/// Os containers são descartados ao fim da suíte — não interferem nos containers de
/// desenvolvimento do usuário (postgres-dev / redis-dev), que usam portas fixas.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public string RedisConnectionString => _redis.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }
}
