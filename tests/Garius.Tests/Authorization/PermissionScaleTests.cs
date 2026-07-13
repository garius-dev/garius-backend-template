using System.Security.Claims;
using System.Text;
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
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using StackExchange.Redis;

namespace Garius.Tests.Authorization;

/// <summary>
/// <b>Escala da autorização.</b> Um usuário pode ter centenas de permissões (dezenas de
/// papéis, cada um com dezenas de permissões). O modelo precisa aguentar isso sem estourar
/// nada — e, sobretudo, <b>sem depender do tamanho do cookie ou do JWT</b>.
///
/// <para>
/// <b>A decisão que estes testes travam:</b> permissões <b>NÃO</b> vão para dentro do
/// cookie/token. Lá vão apenas <c>userId</c> e <c>tenantId</c>; as permissões são resolvidas
/// do banco (com cache) a cada requisição.
/// </para>
///
/// <para>
/// O caminho oposto — gravar cada permissão como uma claim — é comum e quebra em produção de
/// duas formas: o cookie estoura o limite de ~4 KB do navegador (e é <b>silenciosamente
/// descartado</b>, derrubando o login), e o JWT estoura o limite de header do proxy
/// (Traefik/nginx: 8 KB), devolvendo <c>431 Request Header Fields Too Large</c>.
/// </para>
/// </summary>
[Collection("Authorization")]
public class PermissionScaleTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    /// <summary>50 papéis × 20 permissões = 1000 permissões. Muito além de qualquer uso real.</summary>
    private const int RoleCount = 50;
    private const int PermissionsPerRole = 20;

    /// <summary>Limite prático de um cookie por domínio nos navegadores.</summary>
    private const int BrowserCookieLimitBytes = 4096;

    [Fact]
    public async Task Mil_permissoes_resolvem_sem_problema()
    {
        await using var scope = await BuildAsync();

        var user = await CreateUserWithManyPermissionsAsync(scope);

        var permissions = await scope.Resolver.GetPermissionsAsync(
            user.Id, null, TestContext.Current.CancellationToken);

        permissions.Count.ShouldBe(RoleCount * PermissionsPerRole);

        // E a checagem de uma permissão específica continua funcionando.
        permissions.Any(p => Permission.Matches(p, "recurso7.acao3")).ShouldBeTrue();
        permissions.Any(p => Permission.Matches(p, "recurso999.inexistente")).ShouldBeFalse();
    }

    /// <summary>
    /// <b>O teste central.</b> O cookie de autenticação carrega apenas a identidade — nunca as
    /// permissões. Seu tamanho é <b>constante</b>, independentemente de o usuário ter 5 ou
    /// 1000 permissões.
    /// </summary>
    [Fact]
    public async Task O_cookie_NAO_cresce_com_o_numero_de_permissoes()
    {
        await using var scope = await BuildAsync();

        var poorUser = await CreateUserAsync(scope);
        var richUser = await CreateUserWithManyPermissionsAsync(scope);

        var poorCookie = ProtectTicket(scope, BuildPrincipal(poorUser.Id));
        var richCookie = ProtectTicket(scope, BuildPrincipal(richUser.Id));

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"cookie com 0 permissões:    {poorCookie.Length} bytes\n" +
            $"cookie com {RoleCount * PermissionsPerRole} permissões: {richCookie.Length} bytes\n" +
            $"limite do navegador:        {BrowserCookieLimitBytes} bytes");

        // O usuário com 1000 permissões produz um cookie do MESMO tamanho.
        richCookie.Length.ShouldBe(poorCookie.Length);

        richCookie.Length.ShouldBeLessThan(
            BrowserCookieLimitBytes,
            "um cookie acima de ~4 KB é descartado em silêncio pelo navegador — o login simplesmente para de funcionar");
    }

    /// <summary>
    /// O contraponto, para deixar o risco explícito: <b>se</b> as permissões fossem claims, o
    /// cookie estouraria. É por isso que a decisão é não colocá-las lá.
    /// </summary>
    [Fact]
    public async Task Se_as_permissoes_FOSSEM_claims_o_cookie_estouraria()
    {
        await using var scope = await BuildAsync();

        var user = await CreateUserWithManyPermissionsAsync(scope);

        var permissions = await scope.Resolver.GetPermissionsAsync(
            user.Id, null, TestContext.Current.CancellationToken);

        // Monta o principal do jeito ERRADO: uma claim por permissão.
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));

        foreach (var permission in permissions)
        {
            identity.AddClaim(new Claim(Permission.ClaimType, permission));
        }

        var bloated = ProtectTicket(scope, new ClaimsPrincipal(identity));

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"cookie SE as permissões fossem claims: {bloated.Length} bytes " +
            $"({bloated.Length / (double)BrowserCookieLimitBytes:F1}x o limite do navegador)");

        bloated.Length.ShouldBeGreaterThan(
            BrowserCookieLimitBytes,
            "confirma o risco que a arquitetura evita: com as permissões dentro, o cookie passa do limite do navegador");
    }

    /// <summary>
    /// O tamanho do <b>header HTTP</b> importa tanto quanto o do cookie: o Traefik e o nginx
    /// rejeitam headers acima de 8 KB com <c>431</c>. O Kestrel deste template limita em 32 KB
    /// (ver KestrelSetup), então o cookie precisa caber com folga.
    /// </summary>
    [Fact]
    public async Task O_header_Cookie_cabe_com_folga_no_limite_dos_proxies()
    {
        await using var scope = await BuildAsync();

        var user = await CreateUserWithManyPermissionsAsync(scope);

        var cookie = ProtectTicket(scope, BuildPrincipal(user.Id));
        var header = $"__Host-garius.auth={cookie}";

        Encoding.UTF8.GetByteCount(header).ShouldBeLessThan(
            8192,
            "acima de 8 KB o Traefik/nginx responde 431 Request Header Fields Too Large");
    }

    /// <summary>
    /// <b>A armadilha real, desarmada.</b> O <c>AddRoles&lt;&gt;()</c> do Identity registra um
    /// factory de principal que copia <b>todas</b> as roles do usuário e <b>todas</b> as claims
    /// delas para o cookie. Como as permissões deste template são claims de papel, isso
    /// produziria o cookie de 50 KB do teste anterior — no fluxo de login, sem ninguém pedir.
    ///
    /// <para>
    /// Este teste garante que o <c>LeanClaimsPrincipalFactory</c> está de fato substituindo o
    /// padrão no DI. Não basta a classe existir: se o registro sair de ordem, o factory padrão
    /// volta a valer e o login quebra em produção — em silêncio, só para os usuários com muitos
    /// papéis.
    /// </para>
    /// </summary>
    [Fact]
    public async Task O_principal_gerado_pelo_Identity_NAO_carrega_papeis_nem_permissoes()
    {
        await using var scope = await BuildAsync();

        var user = await CreateUserWithManyPermissionsAsync(scope);

        // Exatamente o que o SignInManager fará no login (Fase 4c).
        var principal = await scope.PrincipalFactory.CreateAsync(user);

        var claims = principal.Claims.ToList();

        claims.ShouldNotContain(
            c => c.Type == Permission.ClaimType,
            "permissão no cookie: ele inchará e ficará obsoleto até o próximo login");

        claims.ShouldNotContain(
            c => c.Type == ClaimTypes.Role,
            "papel no cookie: com muitos papéis, o cookie estoura o limite do navegador");

        // Só a identidade.
        principal.FindFirstValue(ClaimTypes.NameIdentifier).ShouldBe(user.Id.ToString());

        var cookie = ProtectTicket(scope, principal);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"cookie real do login, com {RoleCount * PermissionsPerRole} permissões: {cookie.Length} bytes");

        cookie.Length.ShouldBeLessThan(BrowserCookieLimitBytes);
    }

    /// <summary>
    /// O cache <b>evita o banco</b> — provado sem cronômetro.
    ///
    /// <para>
    /// A prova é direta: depois da primeira leitura, <b>apagamos as permissões do banco por SQL
    /// cru</b> (sem passar pelo resolver, e portanto sem invalidar nada). Se a segunda leitura
    /// ainda as devolve, ela não foi ao banco — porque no banco elas já não existem.
    /// </para>
    ///
    /// <para>
    /// Este teste comparava <b>tempos</b> (100 leituras quentes contra uma fria) enquanto o
    /// cache era em memória e uma leitura quente custava ~0 ms. Na Fase 5 o cache foi para o
    /// <b>Redis</b> (para que a invalidação alcance todas as réplicas — ver
    /// <c>RedisPermissionResolver</c>), e uma leitura quente passou a custar uma ida à rede.
    /// A asserção de tempo virou frágil, e — pior — <b>media a coisa errada</b>: o valor do
    /// cache nunca foi "ser mais rápido que o relógio", foi <b>não bater no Postgres a cada
    /// request</b>. É isso que se testa agora.
    /// </para>
    /// </summary>
    [Fact]
    public async Task O_cache_evita_a_ida_ao_banco()
    {
        await using var scope = await BuildAsync();

        var user = await CreateUserWithManyPermissionsAsync(scope);

        var first = await scope.Resolver.GetPermissionsAsync(
            user.Id, null, TestContext.Current.CancellationToken);

        first.Count.ShouldBe(RoleCount * PermissionsPerRole);

        // Apaga TUDO do banco, por baixo do resolver — ele não fica sabendo.
        await scope.Db.Database.ExecuteSqlRawAsync(
            "DELETE FROM role_claims; DELETE FROM user_claims;",
            TestContext.Current.CancellationToken);

        var second = await scope.Resolver.GetPermissionsAsync(
            user.Id, null, TestContext.Current.CancellationToken);

        second.Count.ShouldBe(
            first.Count,
            "as permissões já não existem no banco — se ainda vêm, é porque a leitura NÃO foi " +
            "ao banco. É exatamente o que o cache existe para fazer.");

        // E a invalidação REALMENTE invalida: depois dela, o banco (agora vazio) é consultado.
        await scope.Resolver.InvalidateAllAsync(TestContext.Current.CancellationToken);

        var afterInvalidation = await scope.Resolver.GetPermissionsAsync(
            user.Id, null, TestContext.Current.CancellationToken);

        afterInvalidation.ShouldBeEmpty(
            "depois de invalidar, a leitura tem de ir ao banco — e lá não há mais nada");
    }

    // --- helpers -------------------------------------------------------------

    /// <summary>
    /// O principal REAL da aplicação: só identidade. É o que o login vai gravar no cookie
    /// (Fase 4c) — nenhuma permissão aqui dentro.
    /// </summary>
    private static ClaimsPrincipal BuildPrincipal(Guid userId)
    {
        var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);

        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
        identity.AddClaim(new Claim(AppClaims.TenantId, Guid.Empty.ToString()));

        return new ClaimsPrincipal(identity);
    }

    /// <summary>Cifra o ticket exatamente como o handler de cookie faz, e devolve o valor do cookie.</summary>
    private static string ProtectTicket(ScaleScope scope, ClaimsPrincipal principal)
    {
        var format = new Microsoft.AspNetCore.Authentication.TicketDataFormat(
            scope.DataProtection.CreateProtector("cookie-size-test"));

        var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(
            principal, CookieAuthenticationDefaults.AuthenticationScheme);

        return format.Protect(ticket);
    }

    private static async Task<ApplicationUser> CreateUserAsync(ScaleScope scope)
    {
        var user = new ApplicationUser
        {
            EmailPii = Pii.Create(PiiScope.Email, $"user-{Guid.NewGuid():N}@teste.com"),
            Cpf = Pii.Empty(PiiScope.Cpf)
        };
        user.UserName = user.Id.ToString();

        (await scope.Users.CreateAsync(user, "SenhaForte123!@#")).Succeeded.ShouldBeTrue();

        return user;
    }

    private static async Task<ApplicationUser> CreateUserWithManyPermissionsAsync(ScaleScope scope)
    {
        var user = await CreateUserAsync(scope);

        for (var r = 0; r < RoleCount; r++)
        {
            var name = $"papel{r}-{Guid.NewGuid():N}"[..20];
            var role = new ApplicationRole(name);

            (await scope.Roles.CreateAsync(role)).Succeeded.ShouldBeTrue();

            for (var p = 0; p < PermissionsPerRole; p++)
            {
                await scope.Roles.AddClaimAsync(
                    role, new Claim(Permission.ClaimType, $"recurso{r}.acao{p}"));
            }

            (await scope.Users.AddToRoleAsync(user, name)).Succeeded.ShouldBeTrue();
        }

        await scope.Resolver.InvalidateAllAsync(TestContext.Current.CancellationToken);

        return user;
    }

    private async Task<ScaleScope> BuildAsync()
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

        return new ScaleScope(
            scope,
            db,
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>(),
            scope.ServiceProvider.GetRequiredService<IPermissionResolver>(),
            scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>(),
            scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>());
    }

    private sealed record ScaleScope(
        AsyncServiceScope Scope,
        AppDbContext Db,
        UserManager<ApplicationUser> Users,
        RoleManager<ApplicationRole> Roles,
        IPermissionResolver Resolver,
        IDataProtectionProvider DataProtection,
        IUserClaimsPrincipalFactory<ApplicationUser> PrincipalFactory) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Scope.DisposeAsync();
    }

    private sealed class NoTenant : ITenantResolver
    {
        public Guid? CurrentTenantId => null;
    }
}
