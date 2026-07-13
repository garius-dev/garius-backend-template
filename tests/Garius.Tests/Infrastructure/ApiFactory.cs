using Garius.Infrastructure.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Garius.Tests.Infrastructure;

/// <summary>
/// Sobe a <b>API real</b> (o <c>Program.cs</c> inteiro) contra Postgres e Redis efêmeros.
///
/// <para>
/// Nada é substituído por mock: o pipeline completo roda — cookie, CSRF, autorização,
/// criptografia, refresh no Redis. É o que permite testar o fluxo como um frontend o
/// executaria.
/// </para>
///
/// <para>
/// <b>A configuração vai por variável de ambiente</b>, não por <c>ConfigureAppConfiguration</c>.
/// O host carrega o <c>appsettings.Development.json</c> — que aponta para o Postgres e o Redis
/// <b>locais do desenvolvedor</b> — <i>depois</i> do <c>ConfigureAppConfiguration</c>, e o
/// venceria. Na cascata do .NET, as variáveis de ambiente vêm depois do appsettings, então são
/// o único jeito de garantir que o teste use os containers efêmeros e não a máquina de quem o
/// roda.
/// </para>
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();

    private readonly List<string> _variables = [];

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        var postgres = new Npgsql.NpgsqlConnectionStringBuilder(_postgres.GetConnectionString());

        // "__" é o separador de seção em variável de ambiente.
        Set("Database__Host", postgres.Host!);
        Set("Database__Port", postgres.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Set("Database__RootUsername", postgres.Username!);
        Set("Database__RootPassword", postgres.Password!);

        // O usuário de runtime não existe no container efêmero: o teste conecta como
        // superusuário. O bootstrap (que cria roles e grants) é exercitado em BootstrapTests.
        Set("Database__AppUsername", postgres.Username!);
        Set("Database__AppPassword", postgres.Password!);
        Set("Database__DatabaseName", postgres.Database!);
        Set("Database__ApplicationName", ApplicationName);

        Set("Redis__ConnectionString", _redis.GetConnectionString());
        Set("Redis__Password", string.Empty);
        Set("Redis__InstanceName", $"tests-{Guid.NewGuid():N}");

        // Chaves de teste reais (32 bytes) — o AES-GCM e o HMAC rodam de verdade.
        Set("Encryption__Keys__1", "ZFbLDHAltmKIu1ANyNd7XyLre4jRiwYwKWjL8Lrn7nU=");
        Set("Encryption__ActiveKeyVersion", "1");
        Set("Encryption__BlindIndexKey", "ywIgmu+JbmkZ2HMcpLnWgheAF0CxDQlVZrRjT3VpaO4=");

        // Chave do JWT de máquina. A aplicação NÃO SOBE sem ela (fail-fast deliberado — ver
        // JwtIssuer.BuildSigningKey), então ela é obrigatória mesmo nos testes que nunca
        // emitem um token.
        Set("Jwt__SigningKey", "3Vk8pQjXzL2mNfYtRbHcWdEgUaSoI7xKvJ4nZq1eTpM=");
        Set("Jwt__Issuer", "https://tests.garius.local");
        Set("Jwt__Audience", "https://tests.garius.local");

        // Sem GCP: o teste não pode depender de rede nem de credencial.
        Set("GcpSecrets__Enabled", "false");
        Set("Serilog__Loki__Enabled", "false");

        // RATE LIMIT DESLIGADO por padrão — e é uma decisão, não uma preguiça.
        //
        // Todos os testes rodam do MESMO IP (localhost) e compartilham a mesma janela. O
        // AuthFlowTests sozinho faz mais de 5 logins, e estouraria o limite de /auth/login;
        // os testes seguintes receberiam 429 e falhariam por um motivo que não tem NADA a ver
        // com o que eles testam — e a falha mudaria conforme a ORDEM de execução, que o xUnit
        // não garante.
        //
        // A RateLimitedApiFactory o liga (com limites baixos) sobrescrevendo este método, e o
        // exercita de propósito. Ver RateLimitTests.
        ConfigureRateLimit();

        // O Hangfire (Fase 5) usa um banco SEPARADO — hangfire_{slug}. Em produção quem o cria
        // é o bootstrap (MIGRATE_ONLY); aqui, o container efêmero só traz o banco padrão, e o
        // host morre no boot com "database hangfire_integrationtests does not exist".
        //
        // O Hangfire cria as próprias TABELAS sozinho; o que ele não faz é criar o BANCO.
        await CreateHangfireDatabaseAsync();
    }

    /// <summary>
    /// Cria o banco do Hangfire, que em produção é criado pelo <c>DatabaseBootstrapper</c>.
    ///
    /// <para>
    /// O nome vem do <b>próprio <c>DatabaseNaming</c></b>, e não escrito à mão: ele é a fonte
    /// única desses nomes (ver a classe), e duplicar a fórmula aqui é exatamente o erro que ela
    /// existe para impedir — no template anterior, duas fórmulas divergentes fizeram o health
    /// check autenticar com um usuário que não existia, e ninguém percebeu.
    /// </para>
    /// </summary>
    private async Task CreateHangfireDatabaseAsync()
    {
        var naming = new DatabaseNaming(
            new DatabaseOptions { ApplicationName = ApplicationName },
            typeof(ApiFactory).Assembly);

        await using var connection = new Npgsql.NpgsqlConnection(_postgres.GetConnectionString());

        await connection.OpenAsync();

        await using var command = connection.CreateCommand();

        command.CommandText = $"CREATE DATABASE {naming.HangfireDatabaseName}";

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// O <c>Database:ApplicationName</c> destes testes. Determina os nomes no Postgres —
    /// inclusive o do banco do Hangfire.
    /// </summary>
    private const string ApplicationName = "IntegrationTests";

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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Cria o schema. O bootstrap completo (roles, grants) é testado em BootstrapTests.
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
        });
    }

    /// <summary>
    /// Desligado por padrão. A <c>RateLimitedApiFactory</c> sobrescreve para ligá-lo.
    /// </summary>
    protected virtual void ConfigureRateLimit() => SetVariable("RateLimit__Enabled", "false");

    /// <summary>
    /// Define uma variável de ambiente e a registra para ser <b>limpa</b> no dispose — sem
    /// isso, ela vazaria para os testes seguintes (variável de ambiente é estado global do
    /// processo).
    /// </summary>
    protected void SetVariable(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        _variables.Add(name);
    }

    private void Set(string name, string value) => SetVariable(name, value);
}
