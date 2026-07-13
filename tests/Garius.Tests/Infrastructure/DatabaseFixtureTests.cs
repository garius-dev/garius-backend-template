using Npgsql;
using StackExchange.Redis;

namespace Garius.Tests.Infrastructure;

/// <summary>
/// Valida que a infra de teste (Testcontainers) realmente sobe Postgres e Redis.
/// Se estes testes falharem, nenhum teste de integração das fases seguintes é confiável.
/// </summary>
public class DatabaseFixtureTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task Postgres_sobe_e_aceita_query()
    {
        await using var connection = new NpgsqlDataSourceBuilder(fixture.PostgresConnectionString)
            .Build()
            .CreateConnection();

        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version()";
        var version = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        version.ShouldNotBeNull();
        version.ToString()!.ShouldContain("PostgreSQL 17");
    }

    [Fact]
    public async Task Redis_sobe_e_aceita_comando()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);

        var db = redis.GetDatabase();
        await db.StringSetAsync("smoke", "ok", TimeSpan.FromSeconds(30));

        var value = await db.StringGetAsync("smoke");

        value.ToString().ShouldBe("ok");
    }
}
