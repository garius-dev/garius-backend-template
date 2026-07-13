using System.Text;
using Garius.Core.Identity;
using Garius.Core.Security;
using Garius.Core.Tenancy;
using Garius.Infrastructure.Database;
using Garius.Infrastructure.Database.Interceptors;
using Garius.Infrastructure.Identity;
using Garius.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Garius.Tests.Identity;

/// <summary>
/// O teste central da Fase 4a: <b>o ASP.NET Core Identity funciona com o e-mail
/// criptografado</b>, sem que o <c>UserManager</c> precise ser reescrito.
///
/// <para>
/// A peça que torna isso possível é o <see cref="BlindIndexLookupNormalizer"/>: ele faz o
/// <c>NormalizedEmail</c> guardar o HMAC do e-mail em vez do e-mail. O <c>UserStore</c>
/// compara HMAC com HMAC sem saber que é um HMAC — e <c>FindByEmailAsync</c> continua
/// funcionando nativamente.
/// </para>
/// </summary>
/// <remarks>
/// Não paraleliza: cada teste limpa as tabelas do Identity no Postgres compartilhado do
/// fixture, e rodar isso concorrentemente derrubaria os dados de outro teste no meio.
/// </remarks>
[Collection("Identity")]
public class IdentityWithEncryptedEmailTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string Email = "joao.silva@empresa.com";
    private const string Password = "SenhaForte123!@#";

    [Fact]
    public async Task Cria_usuario_e_o_e_mail_NAO_aparece_em_lugar_nenhum_do_banco()
    {
        await using var scope = await BuildAsync();
        var users = scope.UserManager;

        var result = await users.CreateAsync(NewUser(), Password);
        result.Succeeded.ShouldBeTrue(string.Join("; ", result.Errors.Select(e => e.Description)));

        // Varre a linha inteira, coluna por coluna, procurando o e-mail em claro.
        // É o que um atacante com o dump do banco faria.
        await using var connection = new NpgsqlConnection(fixture.PostgresConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new NpgsqlCommand("SELECT * FROM users LIMIT 1", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);

        for (var i = 0; i < reader.FieldCount; i++)
        {
            var raw = reader.GetValue(i);

            var text = raw switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                _ => raw.ToString() ?? string.Empty
            };

            text.ShouldNotContain(Email, Case.Insensitive,
                $"a coluna '{reader.GetName(i)}' contém o e-mail em claro");
            text.ShouldNotContain("joao.silva", Case.Insensitive,
                $"a coluna '{reader.GetName(i)}' vaza parte do e-mail");
        }
    }

    /// <summary>
    /// A prova de que o Identity não precisou ser reescrito: o método padrão do
    /// <c>UserManager</c> encontra o usuário — sem decifrar nada, buscando pelo índice cego.
    /// </summary>
    [Fact]
    public async Task FindByEmailAsync_funciona_normalmente()
    {
        await using var scope = await BuildAsync();
        var users = scope.UserManager;

        await users.CreateAsync(NewUser(), Password);

        var found = await users.FindByEmailAsync(Email);

        found.ShouldNotBeNull();
        found.EmailPii.Reveal().ShouldBe(Email);
    }

    [Fact]
    public async Task O_login_e_case_insensitive_de_graca()
    {
        await using var scope = await BuildAsync();
        var users = scope.UserManager;

        await users.CreateAsync(NewUser(), Password);

        // A normalização (minúsculas, trim) vive dentro do índice cego, então o usuário que
        // digita o e-mail em maiúsculas ainda encontra a própria conta.
        var found = await users.FindByEmailAsync("  JOAO.SILVA@EMPRESA.COM  ");

        found.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_senha_e_verificada_normalmente()
    {
        await using var scope = await BuildAsync();
        var users = scope.UserManager;

        var created = await users.CreateAsync(NewUser(), Password);
        created.Succeeded.ShouldBeTrue(string.Join("; ", created.Errors.Select(e => e.Description)));

        var user = await users.FindByEmailAsync(Email);
        user.ShouldNotBeNull();
        user.PasswordHash.ShouldNotBeNullOrEmpty("a senha precisa ter sido gravada");

        (await users.CheckPasswordAsync(user, Password)).ShouldBeTrue();
        (await users.CheckPasswordAsync(user, "senha-errada")).ShouldBeFalse();
    }

    [Fact]
    public async Task Nao_permite_dois_usuarios_com_o_mesmo_e_mail()
    {
        await using var scope = await BuildAsync();
        var users = scope.UserManager;

        (await users.CreateAsync(NewUser(), Password)).Succeeded.ShouldBeTrue();

        // Mesmo e-mail, grafia diferente. O índice único é sobre o HMAC, e a normalização
        // torna as duas grafias idênticas — então isto colide, como deve.
        var duplicate = await Should.ThrowAsync<DbUpdateException>(
            () => users.CreateAsync(NewUser(email: "JOAO.SILVA@empresa.com"), Password));

        duplicate.InnerException.ShouldBeOfType<PostgresException>()
            .SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    /// <summary>
    /// Trava um bug sutil que já aconteceu.
    ///
    /// <para>
    /// O <c>CreateAsync</c> preenche o <c>NormalizedEmail</c> chamando
    /// <c>NormalizeEmail(user.Email)</c> — a propriedade <b>string</b> herdada do Identity.
    /// Como a coluna <c>Email</c> é ignorada no mapeamento, era tentador não preenchê-la —
    /// e aí <c>NormalizeEmail(null)</c> devolvia <c>null</c>, o índice cego nunca era gravado,
    /// e <c>FindByEmailAsync</c> não achava ninguém. O login inteiro ficava quebrado.
    /// </para>
    ///
    /// <para>
    /// A correção: o setter de <c>EmailPii</c> alimenta <c>Email</c> em memória (nunca no banco).
    /// </para>
    /// </summary>
    [Fact]
    public async Task O_indice_cego_e_gravado_em_NormalizedEmail_no_CreateAsync()
    {
        await using var scope = await BuildAsync();

        await scope.UserManager.CreateAsync(NewUser(), Password);

        var stored = await scope.Db.Users.IgnoreQueryFilters()
            .Select(u => u.NormalizedEmail)
            .FirstAsync(TestContext.Current.CancellationToken);

        // É o HMAC do e-mail em base64 — nunca o e-mail.
        stored.ShouldNotBeNullOrEmpty();
        stored.ShouldNotContain("joao", Case.Insensitive);

        var expected = Convert.ToBase64String(TestCrypto.BlindIndex.Compute(PiiScope.Email, Email));
        stored.ShouldBe(expected);
    }

    /// <summary>
    /// A navegação explícita que o IdentityUser padrão não tem: papéis e permissões numa
    /// query só, em vez de um GetRolesAsync() extra a cada request.
    /// </summary>
    [Fact]
    public async Task As_navegacoes_explicitas_carregam_o_grafo_numa_query_so()
    {
        await using var scope = await BuildAsync();
        var users = scope.UserManager;
        var roles = scope.RoleManager;
        var db = scope.Db;

        await roles.CreateAsync(new ApplicationRole("Financeiro") { Description = "Aprova faturas" });

        var user = NewUser();
        await users.CreateAsync(user, Password);
        await users.AddToRoleAsync(user, "Financeiro");

        db.ChangeTracker.Clear();

        var loaded = await db.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                    .ThenInclude(r => r.Claims)
            .FirstAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);

        loaded.UserRoles.ShouldHaveSingleItem()
              .Role.Name.ShouldBe("Financeiro");
    }

    private static ApplicationUser NewUser(string email = Email)
    {
        var user = new ApplicationUser
        {
            EmailPii = Pii.Create(PiiScope.Email, email),
            Cpf = Pii.Empty(PiiScope.Cpf),
            DisplayName = "João Silva"
        };

        // O UserName é o Id em texto — um identificador opaco. A convenção do Identity é
        // usar o e-mail, o que aqui vazaria PII em claro na coluna UserName.
        user.UserName = user.Id.ToString();

        return user;
    }

    private async Task<IdentityScope> BuildAsync()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder => builder.AddProvider(NullLoggerProvider.Instance));

        // O AddDefaultTokenProviders() do Identity depende do DataProtection (usa-o para
        // gerar os tokens de reset de senha / confirmação). Em produção o keyring vai para o
        // Redis — sem isso, duas réplicas não conseguem ler o cookie uma da outra.
        services.AddDataProtection();

        services.AddSingleton(TestCrypto.Encryptor);
        services.AddSingleton(TestCrypto.BlindIndex);
        services.AddScoped<ITenantResolver, NoTenant>();

        services.AddDbContext<AppDbContext>((provider, options) => options
            .UseNpgsql(fixture.PostgresConnectionString)
            .AddInterceptors(new AuditingInterceptor(new NoTenant(), TimeProvider.System)));

        services.AddApplicationIdentity();

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        // Cada teste começa com as tabelas vazias, mas SEM recriá-las: um DROP/CREATE
        // concorrente entre testes paralelos derrubaria a tabela debaixo de outro teste
        // ("relation users does not exist").
        await db.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE users, roles, user_roles, user_claims, user_logins,
                     user_tokens, user_tenants, role_claims RESTART IDENTITY CASCADE
            """,
            TestContext.Current.CancellationToken);

        return new IdentityScope(
            scope,
            db,
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>());
    }

    private sealed record IdentityScope(
        AsyncServiceScope Scope,
        AppDbContext Db,
        UserManager<ApplicationUser> UserManager,
        RoleManager<ApplicationRole> RoleManager) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Scope.DisposeAsync();
    }

    private sealed class NoTenant : ITenantResolver
    {
        public Guid? CurrentTenantId => null;
    }
}
