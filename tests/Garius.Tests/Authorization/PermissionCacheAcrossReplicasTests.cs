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
/// <b>A dívida técnica que a Fase 5 pagou</b>, e a prova de que ela está paga.
///
/// <para>
/// Até a Fase 5, o cache de permissões era um <c>IMemoryCache</c> — <b>um por processo</b>. Com
/// duas réplicas, revogar o acesso de alguém funcionava só na réplica que atendeu o request de
/// revogação; nas outras o usuário continuava entrando até o TTL expirar. Com o balanceador
/// jogando o usuário de um lado para o outro, o acesso revogado funcionava de forma
/// <b>intermitente</b> por minutos — a pior forma de um bug de segurança se manifestar, porque
/// parece flakiness e não parece falha.
/// </para>
///
/// <para>
/// Estes testes simulam duas réplicas com <b>dois <see cref="IServiceProvider"/> independentes</b>
/// (que é exatamente o que dois processos são), apontando para o <b>mesmo</b> Postgres e o
/// <b>mesmo</b> Redis.
/// </para>
/// </summary>
[Collection("PermissionCache")]
public class PermissionCacheAcrossReplicasTests(DatabaseFixture fixture)
    : IClassFixture<DatabaseFixture>
{
    /// <summary>
    /// O teste que <b>falharia</b> com o cache em memória: a réplica A revoga, e a réplica B
    /// enxerga a revogação <b>na hora</b>.
    ///
    /// <para>
    /// ⚠️ Note que a permissão é <b>trocada</b> (por outra), e não simplesmente apagada. Isso é
    /// deliberado, e é o que dá dentes ao teste: se ela fosse apagada, um cache <b>não</b>
    /// compartilhado (o bug que estamos testando) ainda passaria — a réplica B, sem cache
    /// próprio válido, iria ao banco e acharia vazio, dando o resultado esperado <b>pela razão
    /// errada</b>. Trocando o valor, só há um jeito de a réplica B enxergar o valor NOVO:
    /// o cache dela ter sido de fato invalidado.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Uma_invalidacao_numa_replica_alcanca_a_OUTRA()
    {
        // O mesmo InstanceName nas duas: é o que as faz compartilhar o Redis, como duas
        // réplicas da MESMA aplicação (e não de aplicações diferentes).
        var instance = $"tests-{Guid.NewGuid():N}";

        await using var replicaA = BuildReplica(instance);
        await using var replicaB = BuildReplica(instance);

        var userId = await SeedUserWithPermissionAsync(replicaA, "invoices.read");

        // As DUAS réplicas leem e cacheiam.
        (await ResolverOf(replicaA).GetPermissionsAsync(userId, null, TestContext.Current.CancellationToken))
            .ShouldContain("invoices.read");

        (await ResolverOf(replicaB).GetPermissionsAsync(userId, null, TestContext.Current.CancellationToken))
            .ShouldContain("invoices.read");

        // A permissão é TROCADA no banco (ver o comentário do XML doc sobre por que trocar, e
        // não apagar)...
        await ReplacePermissionAsync(replicaA, userId, "invoices.approve");

        // ...e a réplica A invalida o cache (é ela que atendeu o request da mudança).
        await ResolverOf(replicaA).InvalidateAllAsync(TestContext.Current.CancellationToken);

        // A RÉPLICA B — que não sabe de nada — precisa enxergar a mudança.
        //
        // É AQUI que o cache em memória falhava: a réplica B continuaria devolvendo
        // "invoices.read" do seu próprio cache, e o usuário continuaria com a permissão antiga
        // (revogada) por ela, até o TTL expirar.
        var fromB = await ResolverOf(replicaB)
            .GetPermissionsAsync(userId, null, TestContext.Current.CancellationToken);

        fromB.ShouldContain(
            "invoices.approve",
            "a invalidação na réplica A tem de alcançar a réplica B — é para isso que o cache " +
            "foi para o Redis");

        fromB.ShouldNotContain(
            "invoices.read",
            "a permissão antiga foi revogada; se a réplica B ainda a devolve, ela está servindo " +
            "o próprio cache obsoleto — o bug que a Fase 5 pagou");
    }

    /// <summary>
    /// O outro lado da moeda: uma réplica <b>aproveita</b> o cache que a outra populou. Não é
    /// só correção — é o que faz N réplicas não multiplicarem por N a carga no Postgres.
    /// </summary>
    [Fact]
    public async Task Uma_replica_aproveita_o_cache_populado_pela_outra()
    {
        var instance = $"tests-{Guid.NewGuid():N}";

        await using var replicaA = BuildReplica(instance);
        await using var replicaB = BuildReplica(instance);

        var userId = await SeedUserWithPermissionAsync(replicaA, "invoices.read");

        // A réplica A lê e popula o cache COMPARTILHADO.
        (await ResolverOf(replicaA).GetPermissionsAsync(userId, null, TestContext.Current.CancellationToken))
            .ShouldContain("invoices.read");

        // Apaga do banco por baixo, sem invalidar.
        await RevokeAllPermissionsAsync(replicaA);

        // A réplica B, que NUNCA leu este usuário, ainda assim acha no cache — porque o cache
        // é um só. Se ela fosse ao banco, viria vazio.
        (await ResolverOf(replicaB).GetPermissionsAsync(userId, null, TestContext.Current.CancellationToken))
            .ShouldContain(
                "invoices.read",
                "o cache é COMPARTILHADO: a réplica B aproveita o que a A populou, e o " +
                "Postgres não é consultado N vezes");
    }

    /// <summary>
    /// Duas aplicações <b>diferentes</b> compartilhando o mesmo Redis não podem enxergar o
    /// cache uma da outra — é o que o <c>InstanceName</c> isola.
    /// </summary>
    [Fact]
    public async Task Aplicacoes_diferentes_nao_compartilham_o_cache()
    {
        await using var appA = BuildReplica($"app-a-{Guid.NewGuid():N}");
        await using var appB = BuildReplica($"app-b-{Guid.NewGuid():N}");

        var userId = await SeedUserWithPermissionAsync(appA, "invoices.read");

        await ResolverOf(appA).GetPermissionsAsync(userId, null, TestContext.Current.CancellationToken);

        await RevokeAllPermissionsAsync(appA);

        // A app B tem outro prefixo de chave: o cache da A não existe para ela. Vai ao banco —
        // que está vazio.
        (await ResolverOf(appB).GetPermissionsAsync(userId, null, TestContext.Current.CancellationToken))
            .ShouldBeEmpty(
                "sem o isolamento por InstanceName, duas aplicações no mesmo Redis leriam o " +
                "cache de permissões uma da outra — um vazamento entre sistemas");
    }

    // --- helpers -------------------------------------------------------------

    /// <summary>
    /// Uma "réplica": um <see cref="ServiceProvider"/> independente, como um segundo processo.
    /// Mesmo Postgres, mesmo Redis.
    /// </summary>
    private ServiceProvider BuildReplica(string instanceName)
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddDataProtection();
        services.AddSingleton(TestCrypto.Encryptor);
        services.AddSingleton(TestCrypto.BlindIndex);
        services.AddScoped<ITenantResolver, NoTenantResolver>();

        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(fixture.PostgresConnectionString)
            .AddInterceptors(new AuditingInterceptor(new NoTenantResolver(), TimeProvider.System)));

        services.AddApplicationIdentity();

        services.AddSingleton<IConnectionMultiplexer>(
            ConnectionMultiplexer.Connect(fixture.RedisConnectionString));

        services.AddSingleton(new RedisOptions { InstanceName = instanceName });

        services.AddPermissionResolver();

        return services.BuildServiceProvider();
    }

    private static IPermissionResolver ResolverOf(IServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IPermissionResolver>();

    private static async Task<Guid> SeedUserWithPermissionAsync(
        IServiceProvider provider,
        string permission)
    {
        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var user = new ApplicationUser
        {
            EmailPii = Pii.Create(PiiScope.Email, $"user-{Guid.NewGuid():N}@empresa.com"),
            Cpf = Pii.Empty(PiiScope.Cpf),
            DisplayName = "Usuário de Teste"
        };
        user.UserName = user.Id.ToString();

        (await users.CreateAsync(user, "SenhaForte123!@#")).Succeeded.ShouldBeTrue();

        db.UserClaims.Add(new ApplicationUserClaim
        {
            UserId = user.Id,
            ClaimType = Permission.ClaimType,
            ClaimValue = permission
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return user.Id;
    }

    private static async Task RevokeAllPermissionsAsync(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM user_claims; DELETE FROM role_claims;",
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Troca a permissão do usuário por outra. Ver o XML doc de
    /// <c>Uma_invalidacao_numa_replica_alcanca_a_OUTRA</c> para o porquê de trocar (e não
    /// apagar) — é o que impede o teste de passar pela razão errada.
    /// </summary>
    private static async Task ReplacePermissionAsync(
        IServiceProvider provider,
        Guid userId,
        string permission)
    {
        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM user_claims", TestContext.Current.CancellationToken);

        db.UserClaims.Add(new ApplicationUserClaim
        {
            UserId = userId,
            ClaimType = Permission.ClaimType,
            ClaimValue = permission
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private sealed class NoTenantResolver : ITenantResolver
    {
        public Guid? CurrentTenantId => null;
    }
}
