using Garius.Core.Entities;
using Garius.Core.Security;
using Garius.Core.Tenancy;
using Garius.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace Garius.Tests.Security;

/// <summary>
/// Entidade de teste que exercita o caminho completo da PII. É também o <b>exemplo canônico</b>
/// de como declarar um campo pessoal: um par <c>Pii</c> + <c>byte[]</c> índice.
/// </summary>
internal sealed class PiiTestPerson : BaseEntity, ITenantEntity
{
    public Guid TenantId { get; set; }

    /// <summary>Cifrado (AES-256-GCM) na coluna <c>bytea</c>.</summary>
    public Pii Email { get; set; }

    /// <summary>Índice cego (HMAC-SHA256) — é por aqui que se busca.</summary>
    public byte[] EmailIndex { get; set; } = [];

    public Pii Cpf { get; set; }

    public byte[] CpfIndex { get; set; } = [];
}

/// <summary>
/// Contexto isolado para os testes de PII: só a entidade de teste, sem as tabelas reais.
/// </summary>
internal sealed class PiiTestDbContext(
    DbContextOptions<PiiTestDbContext> options,
    IFieldEncryptor encryptor) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var entity = modelBuilder.Entity<PiiTestPerson>();

        entity.ToTable("pii_test_people");

        entity.Property(p => p.CreatedAt).HasColumnType("timestamptz");
        entity.Property(p => p.UpdatedAt).HasColumnType("timestamptz");

        // É esta linha que declara "isto é dado pessoal". Ela configura a coluna cifrada E o
        // índice cego de uma vez — cifrar sem indexar produziria um campo em que não se pode
        // buscar, e o login pararia de funcionar.
        entity.HasPii(p => p.Email, p => p.EmailIndex, PiiScope.Email, encryptor, unique: true);
        entity.HasPii(p => p.Cpf, p => p.CpfIndex, PiiScope.Cpf, encryptor, unique: true);

        entity.HasQueryFilter(p => p.Enabled);
    }
}
