using System.Reflection;
using Garius.Core.Authorization;
using Garius.Core.Identity;
using Garius.Infrastructure.Database;
using Garius.Infrastructure.Identity;
using Garius.Infrastructure.Security;
using Garius.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace Garius.Tests.Database;

/// <summary>
/// O primeiro usuário: o superadministrador que o bootstrap cria.
///
/// <para>
/// Sem ele, uma aplicação recém-implantada sobe <b>fechada</b> — a <c>FallbackPolicy</c> exige
/// autenticação em tudo, o <c>/scalar</c> exige <c>docs.read</c>, o <c>/jobs</c> exige
/// <c>jobs.read</c> — e não há usuário nenhum para conceder permissão a ninguém. É o ovo e a
/// galinha, e sem resolvê-lo o template não é utilizável depois do deploy.
/// </para>
/// </summary>
public class BootstrapAdminTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string AdminEmail = "admin@exemplo.com";
    private const string AdminPassword = "uma-senha-bem-longa-123";

    /// <summary>
    /// O admin nasce com a permissão CURINGA (<c>*</c>) — que abre o /scalar, o /jobs, e também
    /// toda permissão que você criar no futuro (um superadministrador não pode ficar obsoleto).
    /// </summary>
    [Fact]
    public async Task Cria_o_admin_com_permissao_TOTAL()
    {
        await using var app = Build();

        await app.BootstrapAsync(AdminEmail, AdminPassword);

        var user = await app.UserManager.FindByEmailAsync(AdminEmail);

        user.ShouldNotBeNull("o bootstrap deveria ter criado o administrador");

        // A senha foi HASHEADA pelo UserManager — nunca gravada em claro.
        (await app.UserManager.CheckPasswordAsync(user, AdminPassword)).ShouldBeTrue();

        var claim = await app.Db.UserClaims
            .IgnoreQueryFilters()
            .SingleAsync(
                c => c.UserId == user.Id && c.ClaimType == Permission.ClaimType,
                TestContext.Current.CancellationToken);

        claim.ClaimValue.ShouldBe(Permissions.SuperAdmin);
        claim.TenantId.ShouldBeNull("um superadministrador vale em TODOS os tenants");

        // O curinga abre qualquer permissão — inclusive as que ainda não existem.
        Permission.Matches(claim.ClaimValue!, "docs.read").ShouldBeTrue();
        Permission.Matches(claim.ClaimValue!, "jobs.read").ShouldBeTrue();
        Permission.Matches(claim.ClaimValue!, "qualquer.feature.futura").ShouldBeTrue();
    }

    /// <summary>
    /// <b>O teste que mais importa.</b> O bootstrap roda a CADA deploy. Se ele redefinisse a
    /// senha ou reconcedesse a permissão, seria uma porta dos fundos que <b>reabre sozinha</b>:
    /// você revoga o acesso de alguém, faz um deploy qualquer, e o acesso volta — em silêncio.
    /// </summary>
    [Fact]
    public async Task Rodar_DE_NOVO_nao_mexe_no_usuario_existente()
    {
        await using var app = Build();

        await app.BootstrapAsync(AdminEmail, AdminPassword);

        var user = await app.UserManager.FindByEmailAsync(AdminEmail);
        user.ShouldNotBeNull();

        // O que um administrador faria depois: trocar a senha e revogar o superpoder.
        await app.UserManager.RemovePasswordAsync(user);
        await app.UserManager.AddPasswordAsync(user, "OUTRA-senha-bem-longa-456");

        var claim = await app.Db.UserClaims
            .IgnoreQueryFilters()
            .SingleAsync(
                c => c.UserId == user.Id && c.ClaimType == Permission.ClaimType,
                TestContext.Current.CancellationToken);

        app.Db.UserClaims.Remove(claim);
        await app.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // O deploy SEGUINTE roda o bootstrap outra vez, com a mesma configuração.
        await app.BootstrapAsync(AdminEmail, AdminPassword);

        app.Db.ChangeTracker.Clear();

        var after = await app.UserManager.FindByEmailAsync(AdminEmail);
        after.ShouldNotBeNull();

        (await app.UserManager.CheckPasswordAsync(after, AdminPassword))
            .ShouldBeFalse("um deploy não pode RESETAR a senha que o administrador trocou");

        var claims = await app.Db.UserClaims
            .IgnoreQueryFilters()
            .Where(c => c.UserId == after.Id && c.ClaimType == Permission.ClaimType)
            .ToListAsync(TestContext.Current.CancellationToken);

        claims.ShouldBeEmpty("um deploy não pode RESSUSCITAR um acesso que foi revogado");
    }

    /// <summary>
    /// <b>O e-mail do admin NÃO pode aparecer em claro em NENHUMA coluna da tabela.</b>
    ///
    /// <para>
    /// Este teste nasceu de um vazamento real. O seeder fazia <c>UserName = settings.AdminEmail</c>,
    /// e isso <b>anulava a criptografia inteira</b>: o <c>Email</c> ia cifrado (AES-GCM), o
    /// <c>NormalizedEmail</c> virava índice cego (HMAC) — e o e-mail em claro reaparecia,
    /// perfeitamente legível, na coluna <c>UserName</c> logo ao lado. Quem abrisse a tabela
    /// <c>users</c> lia <c>admin@empresa.com</c> sem esforço nenhum. Toda a proteção de LGPD
    /// virava teatro, e a conta exposta era <b>a mais poderosa do sistema</b>.
    /// </para>
    ///
    /// <para>
    /// A varredura é feita em <b>SQL cru, sobre TODAS as colunas de texto</b>, e não pelo EF —
    /// de propósito. Uma consulta pelo EF descriptografaria o campo em memória e o teste passaria
    /// pela razão errada, que é o pior tipo de teste de segurança: o que dá verde com o bug
    /// presente. Aqui o que se lê é <b>o que está gravado no disco</b>.
    /// </para>
    ///
    /// <para>
    /// Varrer todas as colunas (em vez de checar só o <c>UserName</c>) é o que dá dentes ao
    /// teste: ele pega o vazamento de novo se alguém, no futuro, adicionar um
    /// <c>DisplayName = email</c> ou um campo de "login" qualquer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task O_email_do_admin_NAO_aparece_em_claro_em_NENHUMA_coluna()
    {
        await using var app = Build();

        await app.BootstrapAsync(AdminEmail, AdminPassword);

        var user = await app.UserManager.FindByEmailAsync(AdminEmail);
        user.ShouldNotBeNull();

        // SQL CRU: o que está no disco, sem o EF descriptografar nada pelo caminho.
        var connection = app.Db.Database.GetDbConnection();

        await connection.OpenAsync(TestContext.Current.CancellationToken);

        try
        {
            var leaked = new List<string>();

            await using var command = connection.CreateCommand();

            // Concatena TODAS as colunas de texto da linha e procura o e-mail dentro.
            command.CommandText = """
                SELECT column_name
                FROM information_schema.columns
                WHERE table_name = 'users'
                  AND data_type IN ('text', 'character varying', 'character')
                """;

            var textColumns = new List<string>();

            await using (var reader = await command.ExecuteReaderAsync(
                TestContext.Current.CancellationToken))
            {
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                {
                    textColumns.Add(reader.GetString(0));
                }
            }

            textColumns.ShouldNotBeEmpty("a tabela users tem colunas de texto");

            foreach (var column in textColumns)
            {
                await using var probe = connection.CreateCommand();

                // O nome da coluna vem do information_schema (não de entrada do usuário), e vai
                // entre aspas duplas. O e-mail procurado vai como PARÂMETRO.
                probe.CommandText =
                    $"""SELECT COUNT(*) FROM users WHERE "{column}" LIKE '%' || @email || '%'""";

                var parameter = probe.CreateParameter();
                parameter.ParameterName = "@email";
                parameter.Value = AdminEmail;
                probe.Parameters.Add(parameter);

                var hits = Convert.ToInt64(
                    await probe.ExecuteScalarAsync(TestContext.Current.CancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture);

                if (hits > 0)
                {
                    leaked.Add(column);
                }
            }

            leaked.ShouldBeEmpty(
                $"o e-mail do admin está EM CLARO na(s) coluna(s): {string.Join(", ", leaked)}. " +
                "A criptografia de PII só vale se o dado não reaparecer legível na coluna ao lado.");

            // E o UserName é um identificador OPACO — o próprio Id.
            user.UserName.ShouldBe(
                user.Id.ToString(),
                "o UserName é o Id: não revela nada e é único por construção");
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    /// <summary>
    /// Falha FECHADA: sem as chaves configuradas, NENHUM usuário é criado.
    ///
    /// <para>
    /// A alternativa — criar um <c>admin/admin</c> quando falta configuração — transformaria a
    /// ausência de config numa conta com permissão TOTAL aberta na internet, num template que
    /// PARECE seguro. É assim que deploys vazam.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SEM_configuracao_NAO_cria_usuario_nenhum()
    {
        await using var app = Build();

        await app.BootstrapAsync(adminEmail: "", adminPassword: "");

        var anyUser = await app.Db.Users
            .IgnoreQueryFilters()
            .AnyAsync(TestContext.Current.CancellationToken);

        anyUser.ShouldBeFalse(
            "sem Bootstrap:AdminEmail/AdminPassword, o bootstrap NÃO pode criar conta nenhuma");
    }

    /// <summary>
    /// Monta o MESMO grafo do modo <c>MIGRATE_ONLY</c> (via <c>AddPersistence</c>), contra o
    /// Postgres efêmero. Cada teste usa um nome de aplicação único — os bancos não colidem.
    /// </summary>
    private BootstrapScope Build()
    {
        var source = new NpgsqlConnectionStringBuilder(fixture.PostgresConnectionString);
        var applicationName = $"app{Guid.NewGuid():N}"[..20];

        var settings = new Dictionary<string, string?>
        {
            ["Database:Host"] = source.Host,
            ["Database:Port"] = source.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Database:RootUsername"] = source.Username,
            ["Database:RootPassword"] = source.Password,
            ["Database:AppPassword"] = "senha-de-runtime-do-teste",
            ["Database:ApplicationName"] = applicationName,

            ["Encryption:Keys:1"] = "ZFbLDHAltmKIu1ANyNd7XyLre4jRiwYwKWjL8Lrn7nU=",
            ["Encryption:ActiveKeyVersion"] = "1",
            ["Encryption:BlindIndexKey"] = "ywIgmu+JbmkZ2HMcpLnWgheAF0CxDQlVZrRjT3VpaO4=",
        };

        return new BootstrapScope(settings, typeof(BootstrapAdminTests).Assembly);
    }

    /// <summary>
    /// Um provider de serviços no modo bootstrap. As chaves do admin entram por configuração
    /// (como em produção, vindas do Secret Manager) e podem mudar entre execuções.
    /// </summary>
    private sealed class BootstrapScope(
        Dictionary<string, string?> settings,
        Assembly migrationsAssembly) : IAsyncDisposable
    {
        private ServiceProvider _provider = null!;
        private IServiceScope _scope = null!;

        public AppDbContext Db => _scope.ServiceProvider.GetRequiredService<AppDbContext>();

        public UserManager<ApplicationUser> UserManager =>
            _scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        /// <summary>Roda o bootstrap completo — banco, migrations e o admin.</summary>
        public async Task BootstrapAsync(string adminEmail, string adminPassword)
        {
            await DisposeScopeAsync();

            settings["Bootstrap:AdminEmail"] = adminEmail;
            settings["Bootstrap:AdminPassword"] = adminPassword;

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings)
                .Build();

            var services = new ServiceCollection();

            services.AddLogging();
            services.AddHttpContextAccessor();
            services.AddScoped<Garius.Core.Security.ICurrentUser, Garius.Api.Infrastructure.Security.SystemCurrentUser>();
            services.AddFieldEncryption(configuration);

            // migrateOnly: true — o MESMO caminho do container de migrations.
            services.AddPersistence(
            configuration,
            new HostingEnvironment { EnvironmentName = "Production" },
            migrationsAssembly, migrateOnly: true);

            _provider = services.BuildServiceProvider();
            _scope = _provider.CreateScope();

            var bootstrapper = _scope.ServiceProvider.GetRequiredService<DatabaseBootstrapper>();
            var seeder = _scope.ServiceProvider.GetRequiredService<BootstrapAdminSeeder>();

            await bootstrapper.RunAsync(Db, TestContext.Current.CancellationToken);
            await seeder.SeedAsync(TestContext.Current.CancellationToken);
        }

        private async Task DisposeScopeAsync()
        {
            _scope?.Dispose();

            if (_provider is not null)
            {
                await _provider.DisposeAsync();
            }
        }

        public async ValueTask DisposeAsync() => await DisposeScopeAsync();
    }
}
