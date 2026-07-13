using System.Text;
using Garius.Core.Security;
using Garius.Core.Tenancy;
using Garius.Infrastructure.Database;
using Garius.Infrastructure.Database.Interceptors;
using Garius.Infrastructure.Security;
using Garius.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Garius.Tests.Security;

/// <summary>
/// O teste que importa: a PII está <b>ilegível no Postgres</b>, e ainda assim a busca exata
/// funciona pelo índice cego.
///
/// <para>
/// Os testes unitários provam que o algoritmo está certo. Este prova que o caminho completo
/// — entidade → ValueConverter → coluna <c>bytea</c> → volta — de fato protege o dado.
/// </para>
/// </summary>
/// <remarks>
/// Não paraleliza: cada teste recria a tabela <c>pii_test_people</c> no Postgres compartilhado
/// do fixture, e rodar isso concorrentemente causaria corrida entre os DROP/CREATE.
/// </remarks>
[Collection("PiiPersistence")]
public class PiiPersistenceTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string Email = "joao.silva@empresa.com";
    private const string Cpf = "12345678901";

    private static readonly EncryptionOptions Options = new()
    {
        Keys = { [1] = "ZFbLDHAltmKIu1ANyNd7XyLre4jRiwYwKWjL8Lrn7nU=" },
        ActiveKeyVersion = 1,
        BlindIndexKey = "ywIgmu+JbmkZ2HMcpLnWgheAF0CxDQlVZrRjT3VpaO4="
    };

    [Fact]
    public async Task O_dado_fica_ILEGIVEL_no_banco()
    {
        var (db, _, _) = await CreateAsync();
        await using var _db = db;

        db.Set<PiiTestPerson>().Add(NewPerson(Guid.CreateVersion7()));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Lê o BYTE CRU da coluna, sem passar pelo EF — é o que um atacante com o dump veria.
        await using var connection = new NpgsqlConnection(fixture.PostgresConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT \"Email\", \"Cpf\" FROM pii_test_people LIMIT 1", connection);

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);

        var emailBytes = (byte[])reader["Email"];
        var cpfBytes = (byte[])reader["Cpf"];

        // O dump do banco não contém o e-mail nem o CPF em lugar nenhum.
        Encoding.UTF8.GetString(emailBytes).ShouldNotContain(Email);
        Encoding.UTF8.GetString(cpfBytes).ShouldNotContain(Cpf);
        Convert.ToBase64String(emailBytes).ShouldNotContain(Email);
    }

    [Fact]
    public async Task Le_de_volta_o_valor_em_claro_atraves_do_EF()
    {
        var (db, _, _) = await CreateAsync();
        await using var _db = db;

        db.Set<PiiTestPerson>().Add(NewPerson(Guid.CreateVersion7()));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var person = await db.Set<PiiTestPerson>()
            .FirstAsync(TestContext.Current.CancellationToken);

        person.Email.Reveal().ShouldBe(Email);
        person.Cpf.Reveal().ShouldBe(Cpf);
    }

    /// <summary>
    /// A razão de existir do índice cego: buscar por e-mail sem decifrar nada. É isto que faz
    /// o login funcionar com o e-mail criptografado.
    /// </summary>
    [Fact]
    public async Task Busca_exata_por_e_mail_funciona_pelo_indice_cego()
    {
        var (db, _, blindIndex) = await CreateAsync();
        await using var _db = db;

        db.Set<PiiTestPerson>().Add(NewPerson(Guid.CreateVersion7()));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        // Exatamente o que o login faz: calcula o HMAC do e-mail digitado e busca por ele.
        // Note que a busca é case-insensitive de graça — a normalização está no índice.
        var lookup = blindIndex.Compute(PiiScope.Email, "JOAO.SILVA@EMPRESA.COM");

        var found = await db.Set<PiiTestPerson>()
            .FirstOrDefaultAsync(p => p.EmailIndex == lookup, TestContext.Current.CancellationToken);

        found.ShouldNotBeNull();
        found.Email.Reveal().ShouldBe(Email);
    }

    [Fact]
    public async Task O_indice_unico_impede_o_mesmo_e_mail_duas_vezes_no_MESMO_tenant()
    {
        var (db, _, _) = await CreateAsync();
        await using var _db = db;

        var tenant = Guid.CreateVersion7();

        db.Set<PiiTestPerson>().Add(NewPerson(tenant));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Mesmo e-mail, grafia diferente. Sem a normalização no índice cego, isto PASSARIA —
        // e teríamos dois cadastros para a mesma pessoa.
        db.Set<PiiTestPerson>().Add(NewPerson(tenant, email: "  JOAO.SILVA@Empresa.com  ", cpf: "99988877766"));

        var exception = await Should.ThrowAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));

        exception.InnerException.ShouldBeOfType<PostgresException>()
            .SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    /// <summary>
    /// O outro lado da moeda: o índice é composto com <c>TenantId</c>, então o mesmo e-mail
    /// PODE existir em tenants diferentes. É o que permite que a mesma pessoa seja cliente de
    /// duas empresas distintas num SaaS.
    /// </summary>
    [Fact]
    public async Task O_mesmo_e_mail_PODE_existir_em_tenants_diferentes()
    {
        var (db, _, _) = await CreateAsync();
        await using var _db = db;

        db.Set<PiiTestPerson>().Add(NewPerson(Guid.CreateVersion7()));
        db.Set<PiiTestPerson>().Add(NewPerson(Guid.CreateVersion7()));

        await Should.NotThrowAsync(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    private static PiiTestPerson NewPerson(Guid tenantId, string email = Email, string cpf = Cpf)
    {
        var blindIndex = new HmacBlindIndex(Microsoft.Extensions.Options.Options.Create(Options));

        return new PiiTestPerson
        {
            TenantId = tenantId,
            Email = Pii.Create(PiiScope.Email, email),
            EmailIndex = blindIndex.Compute(PiiScope.Email, email),
            Cpf = Pii.Create(PiiScope.Cpf, cpf),
            CpfIndex = blindIndex.Compute(PiiScope.Cpf, cpf)
        };
    }

    private async Task<(PiiTestDbContext Db, IFieldEncryptor Encryptor, IBlindIndex BlindIndex)> CreateAsync()
    {
        var wrapped = Microsoft.Extensions.Options.Options.Create(Options);

        IFieldEncryptor encryptor = new AesGcmFieldEncryptor(wrapped);
        IBlindIndex blindIndex = new HmacBlindIndex(wrapped);

        var resolver = new NoTenantResolver();

        var options = new DbContextOptionsBuilder<PiiTestDbContext>()
            .UseNpgsql(fixture.PostgresConnectionString)
            .AddInterceptors(new AuditingInterceptor(resolver, TimeProvider.System))
            .Options;

        var db = new PiiTestDbContext(options, encryptor);

        // Recria só a TABELA. EnsureDeletedAsync tentaria dropar o banco em que a própria
        // conexão está aberta ("cannot drop the currently open database") — e o banco é
        // compartilhado com os outros testes deste fixture.
        await db.Database.ExecuteSqlRawAsync(
            "DROP TABLE IF EXISTS pii_test_people", TestContext.Current.CancellationToken);

        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        return (db, encryptor, blindIndex);
    }

    private sealed class NoTenantResolver : ITenantResolver
    {
        public Guid? CurrentTenantId => null;
    }
}
