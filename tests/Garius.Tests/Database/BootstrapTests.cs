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

    /// <summary>
    /// O usuário de runtime precisa conseguir criar o schema <c>hangfire</c> — que é
    /// EXATAMENTE o que o Hangfire faz no primeiro boot da API.
    ///
    /// <para>
    /// <b>Este teste nasceu de um bug em produção.</b> O bootstrap dava
    /// <c>GRANT CREATE ON SCHEMA public</c> no banco do Hangfire, o que autoriza criar TABELAS
    /// dentro do <c>public</c> — mas o Hangfire.PostgreSql não usa o <c>public</c>: ele cria um
    /// schema PRÓPRIO, chamado <c>hangfire</c>. E <c>CREATE SCHEMA</c> é privilégio do BANCO.
    /// A API subia e morria com <c>42501: permission denied for database hangfire_{slug}</c>.
    /// </para>
    ///
    /// <para>
    /// A suíte não pegava porque a <c>ApiFactory</c> conecta como SUPERUSUÁRIO (o container
    /// efêmero não tem o usuário de runtime) e cria o banco do Hangfire por fora. Ou seja: o
    /// privilégio real do usuário real nunca era exercitado. Testar com superusuário é não
    /// testar permissão nenhuma.
    /// </para>
    /// </summary>
    [Fact]
    public async Task O_usuario_de_runtime_CRIA_o_schema_do_Hangfire()
    {
        var (naming, bootstrapper) = Build();

        await using var setup = CreateContext(naming.RootConnectionStringToAppDatabase);
        await bootstrapper.RunAsync(setup, TestContext.Current.CancellationToken);

        // Conecta no banco do HANGFIRE como o usuário de RUNTIME — não como superusuário.
        var hangfire = new NpgsqlConnectionStringBuilder(naming.AppConnectionString)
        {
            Database = naming.HangfireDatabaseName,
        }.ConnectionString;

        await using var connection = new NpgsqlConnection(hangfire);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        // O comando que o Hangfire.PostgreSql executa ao se instalar.
        await using var createSchema = new NpgsqlCommand(@"CREATE SCHEMA ""hangfire""", connection);

        var exception = await Record.ExceptionAsync(
            () => createSchema.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        exception.ShouldBeNull(
            "sem CREATE no BANCO do Hangfire, a API sobe e morre no primeiro boot");
    }

    /// <summary>
    /// <b>O cenário de toda app derivada:</b> ela cria features novas, com <b>tabelas novas</b>,
    /// numa migration que roda num deploy <i>posterior</i> ao bootstrap inicial. O usuário de
    /// runtime tem de conseguir usar essa tabela — sem que ninguém rode um GRANT à mão.
    ///
    /// <para>
    /// Não é gratuito: <c>ALTER DEFAULT PRIVILEGES</c> (que o bootstrap executa) só se aplica a
    /// objetos criados <b>pela role que o executou</b> — aqui, a root, que é justamente quem o
    /// container de migrations usa. Se a app derivada criasse tabelas por outro caminho, os
    /// privilégios default <b>não a alcançariam</b>.
    /// </para>
    ///
    /// <para>
    /// E há a rede de segurança: o <c>GrantOnExistingObjectsAsync</c> roda <b>depois</b> das
    /// migrations, em TODO deploy, dando <c>GRANT ... ON ALL TABLES</c>. É o que cobre o caso em
    /// que o privilégio default não pegou. As duas defesas juntas são o motivo de "criar uma
    /// tabela nova" simplesmente funcionar.
    /// </para>
    ///
    /// <para>
    /// Sem isso, o sintoma seria brutal e tardio: a migration aplica com sucesso, o deploy passa,
    /// e a API morre com <c>permission denied for table</c> na primeira query da feature nova —
    /// em produção.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Uma_tabela_criada_por_uma_migration_FUTURA_ja_nasce_acessivel()
    {
        var (naming, bootstrapper) = Build();

        await using var setup = CreateContext(naming.RootConnectionStringToAppDatabase);

        // 1. O bootstrap do primeiro deploy.
        await bootstrapper.RunAsync(setup, TestContext.Current.CancellationToken);

        // 2. Uma app derivada acrescenta uma feature. A migration dela roda no deploy seguinte,
        //    pelo MESMO caminho (o container de migrations, conectado como root).
        await using (var root = new NpgsqlConnection(naming.RootConnectionStringToAppDatabase))
        {
            await root.OpenAsync(TestContext.Current.CancellationToken);

            await using var migration = new NpgsqlCommand(
                @"CREATE TABLE invoices (id uuid PRIMARY KEY, total numeric NOT NULL)", root);

            await migration.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // 3. E o usuário de RUNTIME (não o root) usa a tabela nova — sem GRANT manual nenhum.
        await using var connection = new NpgsqlConnection(naming.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var insert = new NpgsqlCommand(
            "INSERT INTO invoices (id, total) VALUES (gen_random_uuid(), 10)", connection);

        var exception = await Record.ExceptionAsync(
            () => insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        exception.ShouldBeNull(
            "uma tabela criada por uma migration futura tem de nascer acessível ao runtime — " +
            "senão a app derivada sobe e morre com 'permission denied for table' na primeira " +
            "query da feature nova, em produção");

        await using var select = new NpgsqlCommand("SELECT count(*) FROM invoices", connection);

        Convert.ToInt64(
            await select.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(1);

        // E o runtime continua SEM poder alterar o schema: o privilégio novo é de DADO
        // (SELECT/INSERT/UPDATE/DELETE), nunca de DDL.
        await using var ddl = new NpgsqlCommand("DROP TABLE invoices", connection);

        var denied = await Should.ThrowAsync<PostgresException>(
            () => ddl.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        denied.SqlState.ShouldBe(
            PostgresErrorCodes.InsufficientPrivilege,
            "acessar a tabela nova não pode significar poder DERRUBÁ-LA");
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
