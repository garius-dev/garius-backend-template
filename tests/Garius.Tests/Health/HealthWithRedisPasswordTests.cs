using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Garius.Infrastructure.Database;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Garius.Tests.Health;

/// <summary>
/// O <c>/health/ready</c> tem de ficar <b>Healthy</b> quando o Redis exige <b>SENHA</b>.
///
/// <para>
/// <b>Este teste nasceu de um bug real, achado rodando a app.</b> O health check montava a
/// conexão do Redis a partir de <c>Redis:ConnectionString</c> <b>crua</b> — e essa string NÃO
/// tem a senha (ela vem separada, em <c>Redis:Password</c>, porque é segredo e vive no Secret
/// Manager). O Redis respondia <c>NOAUTH Authentication required</c>, o <c>/health/ready</c>
/// ficava <b>Unhealthy para sempre</b> — e a APLICAÇÃO funcionava perfeitamente, porque ela
/// monta a conexão certa (ver <c>RedisExtensions.AddRedis</c>).
/// </para>
///
/// <para>
/// Em produção isso é um container saudável que o orquestrador <b>mata em loop</b>. É a MESMA
/// armadilha que o comentário do Postgres, no <c>HealthSetup</c>, descreve: duas fórmulas
/// divergentes para a mesma conexão. Consertaram no Postgres e deixaram no Redis.
/// </para>
///
/// <para>
/// <b>Por que a suíte não pegava:</b> a <c>ApiFactory</c> sobe um Redis <b>SEM senha</b>. Com
/// senha vazia, a fórmula certa e a errada produzem a MESMA conexão, e o bug fica invisível.
/// Só um Redis que EXIGE autenticação — como todo Redis de verdade — encosta nele.
/// </para>
/// </summary>
[Collection(ApiCollectionWithRedisPassword.Name)]
public class HealthWithRedisPasswordTests(RedisPasswordApiFactory factory)
{
    [Fact]
    public async Task O_health_ready_fica_Healthy_com_o_Redis_exigindo_senha()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Sem o fix: "Unhealthy" — o health check tentava conectar SEM a senha.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Healthy", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Confirma que é o REDIS que está saudável — e não que o teste acima passou porque o check
    /// do Redis deixou de existir. Um health check ausente também devolveria "Healthy", e o
    /// teste de cima sozinho não distingue as duas coisas.
    /// </summary>
    [Fact]
    public async Task O_check_do_Redis_EXISTE_e_esta_saudavel()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/detail", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var redis = json.RootElement
            .GetProperty("checks")
            .EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "redis");

        Assert.Equal("Healthy", redis.GetProperty("status").GetString());
    }
}

/// <summary>
/// Uma fábrica cujo Redis <b>EXIGE senha</b> — o cenário de todo Redis real, e o único em que o
/// bug do health check aparece.
/// </summary>
public sealed class RedisPasswordApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string RedisPassword = "uma-senha-de-verdade";
    private const string ApplicationName = "HealthRedisPasswordTests";

    private readonly List<string> _variables = [];

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    // `--requirepass` é o ponto do teste: sem ele, o bug não se manifesta.
    private readonly RedisContainer _redis = new RedisBuilder("redis:7")
        .WithCommand("--requirepass", RedisPassword)
        .Build();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        var postgres = new Npgsql.NpgsqlConnectionStringBuilder(_postgres.GetConnectionString());

        Set("Database__Host", postgres.Host!);
        Set("Database__Port", postgres.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Set("Database__RootUsername", postgres.Username!);
        Set("Database__RootPassword", postgres.Password!);
        Set("Database__AppUsername", postgres.Username!);
        Set("Database__AppPassword", postgres.Password!);
        Set("Database__DatabaseName", postgres.Database!);
        Set("Database__ApplicationName", ApplicationName);

        // A CONNECTION STRING NÃO CARREGA A SENHA — ela vem separada, como em produção.
        // É justamente essa separação que o health check quebrado ignorava.
        var redis = new StackExchange.Redis.ConfigurationOptions();
        redis.EndPoints.Add(_redis.GetConnectionString().Split(',')[0]);

        Set("Redis__ConnectionString", redis.ToString());
        Set("Redis__Password", RedisPassword);
        Set("Redis__InstanceName", $"health-{Guid.NewGuid():N}");

        Set("Encryption__Keys__1", "ZFbLDHAltmKIu1ANyNd7XyLre4jRiwYwKWjL8Lrn7nU=");
        Set("Encryption__ActiveKeyVersion", "1");
        Set("Encryption__BlindIndexKey", "ywIgmu+JbmkZ2HMcpLnWgheAF0CxDQlVZrRjT3VpaO4=");

        Set("Jwt__SigningKey", "3Vk8pQjXzL2mNfYtRbHcWdEgUaSoI7xKvJ4nZq1eTpM=");
        Set("Jwt__Issuer", "https://tests.garius.local");
        Set("Jwt__Audience", "https://tests.garius.local");

        Set("GcpSecrets__Enabled", "false");
        Set("Serilog__Loki__Enabled", "false");
        Set("RateLimit__Enabled", "false");

        await CreateHangfireDatabaseAsync();
    }

    private async Task CreateHangfireDatabaseAsync()
    {
        var naming = new DatabaseNaming(
            new DatabaseOptions { ApplicationName = ApplicationName },
            typeof(RedisPasswordApiFactory).Assembly);

        await using var connection = new Npgsql.NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {naming.HangfireDatabaseName}";

        await command.ExecuteNonQueryAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        foreach (var name in _variables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        await Task.WhenAll(
            _postgres.DisposeAsync().AsTask(),
            _redis.DisposeAsync().AsTask());

        GC.SuppressFinalize(this);
    }

    private void Set(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        _variables.Add(name);
    }
}

/// <summary>
/// Coleção própria. Estas variáveis de ambiente são estado GLOBAL do processo: rodar em
/// paralelo com a <c>ApiCollection</c> faria uma sobrescrever a configuração da outra.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApiCollectionWithRedisPassword : ICollectionFixture<RedisPasswordApiFactory>
{
    public const string Name = "api-redis-password";
}
