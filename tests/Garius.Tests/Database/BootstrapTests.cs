using Garius.Core.Tenancy;
using Garius.Infrastructure.Database;
using Garius.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Garius.Tests.Database;

/// <summary>
/// Prova o auto-bootstrap contra um Postgres real: a aplicação cria banco, role, grants e
/// aplica migrations sozinha, sem nenhum script externo.
///
/// <para>
/// Os dois testes de privilégio são os que importam de verdade. Sem eles, um bug real
/// passou despercebido na primeira implementação: o Postgres concede <c>CONNECT</c> a
/// <c>PUBLIC</c> em todo banco novo, então <b>o isolamento entre aplicações era uma
/// ilusão</b> até o <c>REVOKE</c> ser adicionado.
/// </para>
/// </summary>
public class BootstrapTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task Cria_banco_role_e_aplica_migrations_do_zero()
    {
        var (naming, bootstrapper) = Build();

        await using var db = CreateContext(naming.RootConnectionStringToAppDatabase);
        await bootstrapper.RunAsync(db, TestContext.Current.CancellationToken);

        // A migration rodou: a tabela existe e o tenant padrão foi semeado.
        var tenants = await db.Tenants.IgnoreQueryFilters()
            .ToListAsync(TestContext.Current.CancellationToken);

        tenants.ShouldHaveSingleItem().Slug.ShouldBe("default");
    }

    [Fact]
    public async Task E_idempotente_rodar_duas_vezes_nao_quebra()
    {
        var (naming, bootstrapper) = Build();

        await using var db = CreateContext(naming.RootConnectionStringToAppDatabase);

        await bootstrapper.RunAsync(db, TestContext.Current.CancellationToken);

        // Todo deploy roda o container de migrations. Ele precisa ser seguro de repetir.
        await Should.NotThrowAsync(() => bootstrapper.RunAsync(db, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task O_usuario_de_runtime_faz_CRUD_mas_NAO_faz_DDL()
    {
        var (naming, bootstrapper) = Build();

        await using var setup = CreateContext(naming.RootConnectionStringToAppDatabase);
        await bootstrapper.RunAsync(setup, TestContext.Current.CancellationToken);

        await using var connection = new NpgsqlConnection(naming.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        // CRUD: pode.
        await using (var select = new NpgsqlCommand("SELECT count(*) FROM tenants", connection))
        {
            var count = await select.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            Convert.ToInt64(count!, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1);
        }

        // DDL: não pode. Uma credencial de runtime vazada não deve poder alterar o schema.
        await using var ddl = new NpgsqlCommand("CREATE TABLE hack (id int)", connection);

        var exception = await Should.ThrowAsync<PostgresException>(
            () => ddl.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        exception.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task O_banco_fica_FECHADO_para_qualquer_outra_role_do_servidor()
    {
        var (naming, bootstrapper) = Build();

        await using var setup = CreateContext(naming.RootConnectionStringToAppDatabase);
        await bootstrapper.RunAsync(setup, TestContext.Current.CancellationToken);

        // Simula o usuário de OUTRA aplicação no mesmo servidor Postgres.
        await using var root = new NpgsqlConnection(naming.RootConnectionString);
        await root.OpenAsync(TestContext.Current.CancellationToken);

        await using (var createOther = new NpgsqlCommand(
            "DROP ROLE IF EXISTS outra_app_user; CREATE ROLE outra_app_user WITH LOGIN PASSWORD 'x'", root))
        {
            await createOther.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // O Postgres concede CONNECT a PUBLIC por padrão. Sem o REVOKE do bootstrap, esta
        // asserção falharia — e a credencial de qualquer app abriria o banco de todas.
        await using var check = new NpgsqlCommand(
            "SELECT has_database_privilege('outra_app_user', @db, 'CONNECT')", root);
        check.Parameters.AddWithValue("db", naming.DatabaseName);

        var canConnect = (bool)(await check.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        canConnect.ShouldBeFalse("o banco de uma aplicação não pode ser alcançado pelo usuário de outra");
    }

    /// <summary>
    /// Aponta o bootstrapper para o Postgres efêmero do Testcontainers. Cada teste usa um
    /// nome de aplicação único, então os testes não interferem entre si.
    /// </summary>
    private (DatabaseNaming Naming, DatabaseBootstrapper Bootstrapper) Build()
    {
        var source = new NpgsqlConnectionStringBuilder(fixture.PostgresConnectionString);

        var options = new DatabaseOptions
        {
            Host = source.Host!,
            Port = source.Port,
            RootUsername = source.Username!,
            RootPassword = source.Password!,
            AppPassword = "senha-de-runtime-do-teste",
            ApplicationName = $"app{Guid.NewGuid():N}"[..20]
        };

        var naming = new DatabaseNaming(options, typeof(BootstrapTests).Assembly);

        var bootstrapper = new DatabaseBootstrapper(
            naming,
            options,
            Options.Create(new TenancyOptions()),
            NullLogger<DatabaseBootstrapper>.Instance);

        return (naming, bootstrapper);
    }

    private static AppDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AppDbContext(options, new SystemResolver(), Infrastructure.TestCrypto.Encryptor);
    }

    private sealed class SystemResolver : ITenantResolver
    {
        public Guid? CurrentTenantId => null;
    }
}
