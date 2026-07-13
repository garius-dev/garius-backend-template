using Garius.Core.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Garius.Infrastructure.Database;

/// <summary>
/// Cria tudo que a aplicação precisa no Postgres, sem depender de nenhum script externo
/// ou passo manual: banco de negócio, banco do Hangfire, a role de runtime, os grants, e
/// aplica as migrations.
///
/// <para>
/// Roda <b>apenas</b> no modo <c>MIGRATE_ONLY</c> — um container que faz o trabalho e
/// morre, antes de a API subir. Isso elimina por construção a concorrência entre réplicas:
/// não há duas instâncias fazendo <c>CREATE DATABASE</c> ao mesmo tempo.
/// </para>
///
/// <para>
/// O superusuário é usado <b>só aqui</b>. Depois disso a aplicação conecta como
/// <c>{app}_user</c>, que só tem CRUD.
/// </para>
/// </summary>
public sealed class DatabaseBootstrapper(
    DatabaseNaming naming,
    DatabaseOptions options,
    IOptions<TenancyOptions> tenancy,
    ILogger<DatabaseBootstrapper> logger)
{
    public async Task RunAsync(AppDbContext dbContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        if (string.IsNullOrWhiteSpace(options.RootPassword))
        {
            throw new InvalidOperationException(
                "Database:RootPassword não foi configurada. O bootstrap precisa do superusuário para " +
                "criar o banco e a role da aplicação. Configure-a no Google Secret Manager " +
                "(ou em variável de ambiente: Database__RootPassword).");
        }

        if (string.IsNullOrWhiteSpace(options.AppPassword))
        {
            throw new InvalidOperationException(
                "Database:AppPassword não foi configurada. É a senha com que a role de runtime " +
                "será criada e com que a aplicação vai conectar.");
        }

        logger.LogInformation("Bootstrap: iniciando para o banco {Database}", naming.DatabaseName);

        await CreateRoleAsync(cancellationToken);
        await CreateDatabaseAsync(naming.DatabaseName, cancellationToken);
        await CreateDatabaseAsync(naming.HangfireDatabaseName, cancellationToken);
        await RevokePublicAccessAsync(cancellationToken);
        await GrantPrivilegesAsync(cancellationToken);

        logger.LogInformation("Bootstrap: aplicando migrations");
        await dbContext.Database.MigrateAsync(cancellationToken);

        // Só depois das migrations: os GRANTs default cobrem objetos futuros, mas as
        // tabelas recém-criadas pelas migrations precisam de um GRANT explícito.
        await GrantOnExistingObjectsAsync(cancellationToken);

        await SeedDefaultTenantAsync(dbContext, cancellationToken);

        logger.LogInformation("Bootstrap: concluído");
    }

    /// <summary>
    /// O tenant padrão. Em modo single-tenant é o único que existe; em SaaS, é o tenant
    /// do sistema. Sem ele, a primeira inserção falharia por FK.
    ///
    /// <para>
    /// O seed pertence ao bootstrap, <b>não ao runtime</b>. No template anterior ele rodava
    /// em todo boot de toda réplica — duas queries antes de aceitar tráfego, e uma race
    /// entre réplicas subindo juntas.
    /// </para>
    /// </summary>
    private async Task SeedDefaultTenantAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var defaultTenantId = tenancy.Value.DefaultTenantId;

        // IgnoreQueryFilters: o bootstrap roda com o SystemTenantResolver (sem tenant), mas
        // o filtro de soft delete continua ativo — e um tenant desabilitado ainda existe.
        var exists = await dbContext.Tenants
            .IgnoreQueryFilters()
            .AnyAsync(t => t.Id == defaultTenantId, cancellationToken);

        if (exists)
        {
            return;
        }

        logger.LogInformation("Bootstrap: criando o tenant padrão {TenantId}", defaultTenantId);

        dbContext.Tenants.Add(new Tenant
        {
            Id = defaultTenantId,
            Name = "Default",
            Slug = "default"
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// A role é criada com a senha que <b>já está</b> no Secret Manager — não é gerada aqui.
    /// Assim não existe o problema de "onde guardo a senha que acabei de criar".
    /// </summary>
    private async Task CreateRoleAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(naming.RootConnectionString);
        await connection.OpenAsync(cancellationToken);

        var exists = await ScalarAsync<bool>(
            connection,
            "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @name)",
            cancellationToken,
            ("name", naming.AppUsername));

        if (exists)
        {
            // A senha pode ter sido rotacionada no Secret Manager: realinha o Postgres.
            logger.LogInformation("Bootstrap: role {Role} já existe, sincronizando a senha", naming.AppUsername);

            await ExecuteAsync(
                connection,
                $"ALTER ROLE {Quote(naming.AppUsername)} WITH PASSWORD {Literal(options.AppPassword)}",
                cancellationToken);

            return;
        }

        logger.LogInformation("Bootstrap: criando a role {Role}", naming.AppUsername);

        // LOGIN, sem NOSUPERUSER/NOCREATEDB explícitos (são o default): a role de runtime
        // não pode criar bancos nem escalar privilégio.
        await ExecuteAsync(
            connection,
            $"CREATE ROLE {Quote(naming.AppUsername)} WITH LOGIN PASSWORD {Literal(options.AppPassword)}",
            cancellationToken);
    }

    private async Task CreateDatabaseAsync(string databaseName, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(naming.RootConnectionString);
        await connection.OpenAsync(cancellationToken);

        var exists = await ScalarAsync<bool>(
            connection,
            "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @name)",
            cancellationToken,
            ("name", databaseName));

        if (exists)
        {
            logger.LogInformation("Bootstrap: banco {Database} já existe", databaseName);
            return;
        }

        logger.LogInformation("Bootstrap: criando o banco {Database}", databaseName);

        // CREATE DATABASE não pode rodar dentro de transação — daí o comando isolado.
        await ExecuteAsync(connection, $"CREATE DATABASE {Quote(databaseName)}", cancellationToken);
    }

    /// <summary>
    /// <b>Fecha o banco desta aplicação para o resto do mundo.</b>
    ///
    /// <para>
    /// O Postgres concede <c>CONNECT</c> a <c>PUBLIC</c> em todo banco novo — ou seja,
    /// <b>qualquer role do servidor já nasce podendo conectar em qualquer banco</b>. Sem
    /// este <c>REVOKE</c>, o <c>GRANT CONNECT</c> feito adiante é redundante e o isolamento
    /// é uma ilusão: o usuário desta app alcançaria o banco de todas as outras.
    /// </para>
    ///
    /// <para>
    /// O mesmo vale para o schema <c>public</c>: até o Postgres 14, <c>PUBLIC</c> tinha
    /// <c>CREATE</c> nele por padrão.
    /// </para>
    /// </summary>
    private async Task RevokePublicAccessAsync(CancellationToken cancellationToken)
    {
        foreach (var database in new[] { naming.DatabaseName, naming.HangfireDatabaseName })
        {
            await using var connection = new NpgsqlConnection(naming.RootConnectionStringTo(database));
            await connection.OpenAsync(cancellationToken);

            await ExecuteAsync(
                connection,
                $"REVOKE ALL ON DATABASE {Quote(database)} FROM PUBLIC",
                cancellationToken);

            await ExecuteAsync(connection, "REVOKE ALL ON SCHEMA public FROM PUBLIC", cancellationToken);
        }

        logger.LogInformation(
            "Bootstrap: acesso PUBLIC revogado — só {Role} e o superusuário alcançam estes bancos",
            naming.AppUsername);
    }

    /// <summary>
    /// Privilégios do usuário de runtime, seguindo o menor privilégio possível:
    /// CRUD nas tabelas, nada de DDL. E <b>só nos bancos desta aplicação</b> — a credencial
    /// desta app não abre o banco de nenhuma outra (ver <see cref="RevokePublicAccessAsync"/>).
    /// </summary>
    private async Task GrantPrivilegesAsync(CancellationToken cancellationToken)
    {
        foreach (var database in new[] { naming.DatabaseName, naming.HangfireDatabaseName })
        {
            await using var connection = new NpgsqlConnection(naming.RootConnectionStringTo(database));
            await connection.OpenAsync(cancellationToken);

            var user = Quote(naming.AppUsername);

            await ExecuteAsync(connection, $"GRANT CONNECT ON DATABASE {Quote(database)} TO {user}", cancellationToken);
            await ExecuteAsync(connection, $"GRANT USAGE ON SCHEMA public TO {user}", cancellationToken);

            // O Hangfire cria as próprias tabelas em runtime, então lá o usuário precisa de DDL.
            //
            // ⚠️ São DOIS grants, e o de baixo é o que costuma faltar.
            //
            // O Hangfire.PostgreSql não usa o schema `public`: ele cria um schema PRÓPRIO,
            // chamado `hangfire`. E `CREATE SCHEMA` é um privilégio do BANCO, não do schema —
            // o `GRANT CREATE ON SCHEMA public` (que autoriza criar TABELAS dentro do public)
            // não autoriza criar um SCHEMA novo.
            //
            // Sem o `GRANT CREATE ON DATABASE`, a API sobe, o Hangfire tenta se instalar e
            // morre com:  42501: permission denied for database hangfire_{slug}
            //             Where: SQL statement "CREATE SCHEMA ""hangfire"";"
            if (database == naming.HangfireDatabaseName)
            {
                await ExecuteAsync(connection, $"GRANT CREATE ON DATABASE {Quote(database)} TO {user}", cancellationToken);
                await ExecuteAsync(connection, $"GRANT CREATE ON SCHEMA public TO {user}", cancellationToken);
            }

            // Objetos que as MIGRATIONS criarem daqui em diante já nascem acessíveis.
            await ExecuteAsync(
                connection,
                $"ALTER DEFAULT PRIVILEGES IN SCHEMA public " +
                $"GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {user}",
                cancellationToken);

            await ExecuteAsync(
                connection,
                $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO {user}",
                cancellationToken);
        }
    }

    /// <summary>
    /// As migrations acabaram de criar tabelas. <c>ALTER DEFAULT PRIVILEGES</c> só vale
    /// para objetos futuros, então estas precisam de um GRANT explícito — sem isto, a
    /// aplicação sobe e falha em toda query com "permission denied for table".
    /// </summary>
    private async Task GrantOnExistingObjectsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(naming.RootConnectionStringToAppDatabase);
        await connection.OpenAsync(cancellationToken);

        var user = Quote(naming.AppUsername);

        await ExecuteAsync(
            connection,
            $"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {user}",
            cancellationToken);

        await ExecuteAsync(
            connection,
            $"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {user}",
            cancellationToken);
    }

    // --- SQL helpers -------------------------------------------------------
    //
    // Identificadores (nome de banco, de role) NÃO podem ser parâmetros em DDL do Postgres.
    // Precisam ser interpolados — e por isso precisam ser escapados corretamente, ou a
    // porta para SQL injection fica aberta. Os nomes derivam do assembly e da configuração,
    // mas "vem da configuração" não é o mesmo que "é confiável".

    /// <summary>Escapa um identificador: <c>meu"user</c> → <c>"meu""user"</c>.</summary>
    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <summary>Escapa um literal de string: <c>a'b</c> → <c>'a''b'</c>.</summary>
    private static string Literal(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);

        return (T)result!;
    }
}
