using System.Security.Claims;
using Garius.Core.Authorization;
using Garius.Core.Identity;
using Garius.Core.Security;
using Garius.Core.Tenancy;
using Garius.Infrastructure.Authorization;
using Garius.Infrastructure.Caching;
using Garius.Infrastructure.Database;
using Garius.Infrastructure.Database.Interceptors;
using Garius.Infrastructure.Identity;
using Garius.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using StackExchange.Redis;

namespace Garius.Tests.Authorization;

/// <summary>
/// Prova a resolução de permissões efetivas contra o Postgres real: papéis + permissões
/// avulsas, achatadas, e o efeito do soft delete sobre elas.
/// </summary>
[Collection("Authorization")]
public class PermissionResolverTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task As_permissoes_do_papel_chegam_ao_usuario()
    {
        await using var scope = await BuildAsync();

        var user = await CreateUserAsync(scope);
        await GiveRoleAsync(scope, user, "Financeiro",
            [Permissions.Users.Read.Value, Permissions.Roles.Read.Value]);

        var permissions = await scope.Resolver.GetPermissionsAsync(user.Id, null, TestContext.Current.CancellationToken);

        permissions.ShouldContain("users.read");
        permissions.ShouldContain("roles.read");
        permissions.ShouldNotContain("users.delete");
    }

    /// <summary>
    /// A exceção pontual do modelo GCP: "esta pessoa, e só ela, também pode X" — sem criar um
    /// papel novo para um caso único.
    /// </summary>
    [Fact]
    public async Task As_permissoes_avulsas_somam_as_dos_papeis()
    {
        await using var scope = await BuildAsync();

        var user = await CreateUserAsync(scope);
        await GiveRoleAsync(scope, user, "Leitor", [Permissions.Users.Read.Value]);

        scope.Db.UserClaims.Add(new ApplicationUserClaim
        {
            UserId = user.Id,
            ClaimType = Permission.ClaimType,
            ClaimValue = Permissions.Pii.ReadCpf.Value
        });
        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await scope.Resolver.InvalidateAllAsync(TestContext.Current.CancellationToken);

        var permissions = await scope.Resolver.GetPermissionsAsync(user.Id, null, TestContext.Current.CancellationToken);

        permissions.ShouldContain("users.read");   // do papel
        permissions.ShouldContain("pii.cpf.read"); // avulsa
    }

    /// <summary>
    /// <b>O teste que mais importa.</b> Desabilitar um papel tem de revogar as permissões dele
    /// — do contrário "revogar o acesso" não revoga nada.
    ///
    /// <para>
    /// Isto só funciona porque as entidades-filhas do Identity (<c>role_claims</c>,
    /// <c>user_roles</c>) têm query filter sobre o <c>Enabled</c> do pai. Sem esses filtros, o
    /// papel sumiria das consultas mas as permissões continuariam sendo carregadas.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Desabilitar_o_papel_REVOGA_as_permissoes_dele()
    {
        await using var scope = await BuildAsync();

        var user = await CreateUserAsync(scope);
        var role = await GiveRoleAsync(scope, user, "Temporario", [Permissions.Users.Delete.Value]);

        (await scope.Resolver.GetPermissionsAsync(user.Id, null, TestContext.Current.CancellationToken))
            .ShouldContain("users.delete");

        // Soft delete do papel.
        role.Enabled = false;
        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await scope.Resolver.InvalidateAllAsync(TestContext.Current.CancellationToken);

        (await scope.Resolver.GetPermissionsAsync(user.Id, null, TestContext.Current.CancellationToken))
            .ShouldNotContain("users.delete", "desabilitar um papel precisa revogar o acesso");
    }

    [Fact]
    public async Task Um_papel_de_tenant_NAO_vale_em_outro_tenant()
    {
        await using var scope = await BuildAsync();

        var tenantA = await CreateTenantAsync(scope, "empresa-a");
        var tenantB = await CreateTenantAsync(scope, "empresa-b");

        var user = await CreateUserAsync(scope);

        // Papel que só existe na empresa A.
        await GiveRoleAsync(scope, user, "AdminA", [Permissions.Users.Delete.Value], tenantId: tenantA.Id);

        (await scope.Resolver.GetPermissionsAsync(user.Id, tenantA.Id, TestContext.Current.CancellationToken))
            .ShouldContain("users.delete");

        (await scope.Resolver.GetPermissionsAsync(user.Id, tenantB.Id, TestContext.Current.CancellationToken))
            .ShouldNotContain("users.delete", "um papel de tenant não pode vazar para outro");
    }

    [Fact]
    public async Task Um_papel_global_vale_em_qualquer_tenant()
    {
        await using var scope = await BuildAsync();

        var tenant = await CreateTenantAsync(scope, "empresa-x");
        var user = await CreateUserAsync(scope);

        // TenantId = null → papel global.
        await GiveRoleAsync(scope, user, "SuporteGlobal", [Permissions.Users.Read.Value], tenantId: null);

        (await scope.Resolver.GetPermissionsAsync(user.Id, tenant.Id, TestContext.Current.CancellationToken))
            .ShouldContain("users.read");
    }

    [Fact]
    public async Task O_curinga_do_superadministrador_satisfaz_qualquer_permissao()
    {
        await using var scope = await BuildAsync();

        var user = await CreateUserAsync(scope);
        await GiveRoleAsync(scope, user, "SuperAdmin", [Permissions.SuperAdmin]);

        var permissions = await scope.Resolver.GetPermissionsAsync(user.Id, null, TestContext.Current.CancellationToken);

        permissions.Any(p => Permission.Matches(p, "qualquer.coisa")).ShouldBeTrue();
        permissions.Any(p => Permission.Matches(p, "users.delete")).ShouldBeTrue();
    }

    // --- helpers -------------------------------------------------------------

    private static async Task<ApplicationUser> CreateUserAsync(AuthScope scope)
    {
        var email = $"user-{Guid.NewGuid():N}@teste.com";

        var user = new ApplicationUser
        {
            EmailPii = Pii.Create(PiiScope.Email, email),
            Cpf = Pii.Empty(PiiScope.Cpf)
        };
        user.UserName = user.Id.ToString();

        var result = await scope.Users.CreateAsync(user, "SenhaForte123!@#");
        result.Succeeded.ShouldBeTrue(string.Join("; ", result.Errors.Select(e => e.Description)));

        return user;
    }

    private static async Task<Tenant> CreateTenantAsync(AuthScope scope, string slug)
    {
        var tenant = new Tenant { Name = slug, Slug = $"{slug}-{Guid.NewGuid():N}"[..20] };

        scope.Db.Tenants.Add(tenant);
        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return tenant;
    }

    private static async Task<ApplicationRole> GiveRoleAsync(
        AuthScope scope,
        ApplicationUser user,
        string roleName,
        string[] permissions,
        Guid? tenantId = null)
    {
        var name = $"{roleName}-{Guid.NewGuid():N}"[..20];

        var role = new ApplicationRole(name) { TenantId = tenantId };

        (await scope.Roles.CreateAsync(role)).Succeeded.ShouldBeTrue();

        // As permissões chegam como claims do papel.
        foreach (var permission in permissions)
        {
            await scope.Roles.AddClaimAsync(role, new Claim(Permission.ClaimType, permission));
        }

        (await scope.Users.AddToRoleAsync(user, name)).Succeeded.ShouldBeTrue();

        await scope.Resolver.InvalidateAllAsync(TestContext.Current.CancellationToken);

        return role;
    }

    private async Task<AuthScope> BuildAsync()
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddDataProtection();
        services.AddSingleton(TestCrypto.Encryptor);
        services.AddSingleton(TestCrypto.BlindIndex);
        services.AddScoped<ITenantResolver, NoTenant>();

        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(fixture.PostgresConnectionString)
            .AddInterceptors(new AuditingInterceptor(new NoTenant(), TimeProvider.System)));

        services.AddApplicationIdentity();

        // O cache de permissões agora vive no REDIS (Fase 5) — em memória, a invalidação não
        // alcançava as outras réplicas. O InstanceName é único por scope de teste: sem isso,
        // dois testes compartilhariam o contador de geração e o cache um do outro.
        services.AddSingleton<IConnectionMultiplexer>(
            ConnectionMultiplexer.Connect(fixture.RedisConnectionString));
        services.AddSingleton(new RedisOptions
        {
            InstanceName = $"tests-{Guid.NewGuid():N}"
        });

        services.AddPermissionResolver();

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE users, roles, user_roles, user_claims, user_logins,
                     user_tokens, user_tenants, role_claims RESTART IDENTITY CASCADE
            """,
            TestContext.Current.CancellationToken);

        return new AuthScope(
            scope,
            db,
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>(),
            scope.ServiceProvider.GetRequiredService<IPermissionResolver>());
    }

    private sealed record AuthScope(
        AsyncServiceScope Scope,
        AppDbContext Db,
        UserManager<ApplicationUser> Users,
        RoleManager<ApplicationRole> Roles,
        IPermissionResolver Resolver) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Scope.DisposeAsync();
    }

    private sealed class NoTenant : ITenantResolver
    {
        public Guid? CurrentTenantId => null;
    }
}
