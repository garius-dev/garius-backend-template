using Garius.Core.Tenancy;
using Garius.Infrastructure.Database;
using Garius.Infrastructure.Database.Interceptors;
using Garius.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Garius.Tests.Database;

/// <summary>
/// Trava o comportamento do <see cref="AppDbContext"/> contra um Postgres real: soft
/// delete, índice único parcial, timestamps automáticos e UUID v7.
///
/// <para>
/// Nada disso pode ser validado com mock — depende de comportamento específico do Postgres
/// (índice parcial, timestamptz, ordenação de uuid).
/// </para>
/// </summary>
public class SoftDeleteAndTenancyTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task Soft_delete_esconde_o_registro_das_consultas()
    {
        await using var db = await CreateContextAsync(tenant: null);

        var tenant = new Tenant { Name = "Para Excluir", Slug = "para-excluir" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Remove() NÃO apaga: o interceptor converte em Enabled = false.
        db.Tenants.Remove(tenant);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Some das consultas normais...
        var visible = await db.Tenants
            .AnyAsync(t => t.Id == tenant.Id, TestContext.Current.CancellationToken);
        visible.ShouldBeFalse();

        // ...mas continua no banco (a LGPD exige a trilha de auditoria).
        var stillThere = await db.Tenants
            .IgnoreQueryFilters()
            .SingleAsync(t => t.Id == tenant.Id, TestContext.Current.CancellationToken);

        stillThere.Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task O_indice_unico_e_parcial_libera_o_slug_apos_o_soft_delete()
    {
        await using var db = await CreateContextAsync(tenant: null);

        var original = new Tenant { Name = "Cliente", Slug = "cliente-que-saiu" };
        db.Tenants.Add(original);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Tenants.Remove(original);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // O cliente saiu e voltou 6 meses depois. Com um índice único TOTAL, isto
        // explodiria: o slug do registro apagado continuaria ocupado para sempre.
        db.Tenants.Add(new Tenant { Name = "Cliente (voltou)", Slug = "cliente-que-saiu" });

        await Should.NotThrowAsync(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task O_timestamp_e_preenchido_automaticamente()
    {
        await using var db = await CreateContextAsync(tenant: null);

        var tenant = new Tenant { Name = "Auditado", Slug = $"auditado-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        tenant.CreatedAt.ShouldNotBe(default);
        tenant.UpdatedAt.ShouldNotBe(default);

        var created = tenant.CreatedAt;

        tenant.Name = "Auditado (alterado)";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // CreatedAt é imutável; UpdatedAt acompanha.
        tenant.CreatedAt.ShouldBe(created);
        tenant.UpdatedAt.ShouldBeGreaterThanOrEqualTo(created);
    }

    [Fact]
    public async Task O_Id_e_um_UUID_v7_sequencial()
    {
        await using var db = await CreateContextAsync(tenant: null);

        var first = new Tenant { Name = "A", Slug = $"a-{Guid.NewGuid():N}" };
        await Task.Delay(10, TestContext.Current.CancellationToken);
        var second = new Tenant { Name = "B", Slug = $"b-{Guid.NewGuid():N}" };

        db.Tenants.AddRange(first, second);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // UUID v7 embute o timestamp: ordenar por Id ordena por criação. É isso que evita
        // fragmentar o índice B-tree do Postgres, ao contrário de um GUID aleatório.
        first.Id.CompareTo(second.Id).ShouldBeLessThan(0);
    }

    private async Task<AppDbContext> CreateContextAsync(Guid? tenant)
    {
        var resolver = new StubTenantResolver(tenant);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.PostgresConnectionString)
            .AddInterceptors(new AuditingInterceptor(resolver, TimeProvider.System))
            .Options;

        var context = new AppDbContext(options, resolver, Infrastructure.TestCrypto.Encryptor);

        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        return context;
    }

    private sealed class StubTenantResolver(Guid? tenantId) : ITenantResolver
    {
        public Guid? CurrentTenantId => tenantId;
    }
}
