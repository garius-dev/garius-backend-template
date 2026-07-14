using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Garius.Core.Authorization;
using Garius.Core.Identity;
using Garius.Core.Security;
using Garius.Core.Tenancy;
using Garius.Infrastructure.Database;
using Garius.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Garius.Tests.Authorization;

/// <summary>
/// <b>A armadilha mais séria do M2M</b>, e a defesa contra ela.
///
/// <para>
/// Quem pode criar um client escolhe os escopos dele. Se pudesse escolher <b>qualquer</b>
/// escopo, teria em mãos uma escalada de privilégio em dois passos: cria um client com escopo
/// <c>*</c>, autentica-se com ele, e passa a ser superadministrador — usando uma permissão
/// (<c>clients.create</c>) que parecia inócua.
/// </para>
///
/// <para>
/// A regra: <b>ninguém delega a uma máquina um poder que não tem</b>.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public class MachineScopeEscalationTests(ApiFactory factory)
{
    private const string Password = "SenhaForte123!@#";

    /// <summary>Um escopo com erro de digitação — não existe no catálogo.</summary>
    private static readonly string[] TypoScope = ["invocies.read"];

    [Fact]
    public async Task Nao_e_possivel_criar_um_client_SUPERADMIN_sem_ser_superadmin()
    {
        // O usuário pode criar clients, e pode ler usuários. Só isso.
        var client = await LoginAsync(
            Permissions.Clients.Create.Value,
            Permissions.Users.Read.Value);

        var response = await client.PostAsJsonAsync(
            "/machine/clients",
            new
            {
                name = "Client do Mal",
                scopes = new[] { Permissions.SuperAdmin }
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "criar um client com escopo '*' seria virar superadministrador em dois passos");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        body.GetProperty("code").GetString().ShouldBe("client.scope_escalation");
    }

    [Fact]
    public async Task Nao_e_possivel_conceder_a_uma_maquina_um_escopo_que_o_criador_nao_tem()
    {
        // Ele pode criar clients e LER usuários — mas não pode APAGÁ-LOS.
        var client = await LoginAsync(
            Permissions.Clients.Create.Value,
            Permissions.Users.Read.Value);

        var response = await client.PostAsJsonAsync(
            "/machine/clients",
            new
            {
                name = "Client",
                scopes = new[] { Permissions.Users.Delete.Value }
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Um_escopo_que_o_criador_TEM_e_concedido_normalmente()
    {
        var client = await LoginAsync(
            Permissions.Clients.Create.Value,
            Permissions.Users.Read.Value);

        var response = await client.PostAsJsonAsync(
            "/machine/clients",
            new
            {
                name = "Client Legítimo",
                scopes = new[] { Permissions.Users.Read.Value }
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        var data = body.GetProperty("data");

        // O segredo vem AQUI, e nunca mais — o banco só guarda o hash.
        data.GetProperty("clientSecret").GetString().ShouldNotBeNullOrEmpty();
        data.GetProperty("clientId").GetString().ShouldStartWith("cid_");
    }

    /// <summary>
    /// O curinga do criador é respeitado: quem tem <c>users.*</c> pode conceder
    /// <c>users.delete</c> — mas continua sem poder conceder <c>roles.delete</c>.
    /// </summary>
    [Fact]
    public async Task Um_criador_com_curinga_pode_conceder_dentro_do_curinga_e_so_dentro_dele()
    {
        var client = await LoginAsync(Permissions.Clients.Create.Value, "users.*");

        var dentro = await client.PostAsJsonAsync(
            "/machine/clients",
            new { name = "A", scopes = new[] { Permissions.Users.Delete.Value } },
            TestContext.Current.CancellationToken);

        dentro.StatusCode.ShouldBe(HttpStatusCode.OK, "users.* satisfaz users.delete");

        var fora = await client.PostAsJsonAsync(
            "/machine/clients",
            new { name = "B", scopes = new[] { Permissions.Roles.Delete.Value } },
            TestContext.Current.CancellationToken);

        fora.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "users.* não satisfaz roles.delete");
    }

    /// <summary>
    /// Um escopo inexistente é barrado <b>antes de tocar o banco</b> — agora pelo VALIDATOR, e
    /// não mais pelo service.
    ///
    /// <para>
    /// O <c>code</c> mudou de <c>client.unknown_scope</c> para <c>validation.failed</c>, e é uma
    /// melhora: o erro passa a vir <b>por campo</b> (<c>errors.scopes</c>), dizendo QUAL escopo
    /// está errado — em vez de uma mensagem genérica sobre "algum" escopo. É o formato que um
    /// formulário consome.
    /// </para>
    ///
    /// <para>
    /// A checagem no <c>MachineAuthService.ValidateScopesAsync</c> <b>continua existindo</b>: ela
    /// é a defesa contra ESCALADA (você não delega um poder que não tem), precisa saber QUEM está
    /// criando, e é exercitada pelos outros testes desta classe. O validator cobre outra coisa —
    /// o escopo que simplesmente não existe.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Um_escopo_inexistente_e_rejeitado_antes_de_chegar_ao_banco()
    {
        var client = await LoginAsync(Permissions.Clients.Create.Value, Permissions.SuperAdmin);

        var response = await client.PostAsJsonAsync(
            "/machine/clients",
            // Erro de digitação. Gravado, daria um client que nunca autoriza nada — e ninguém
            // entenderia por quê.
            new { name = "Client", scopes = TypoScope },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        body.GetProperty("code").GetString().ShouldBe("validation.failed");

        // O erro aponta o CAMPO — e o campo é `scopes`.
        body.GetProperty("errors").TryGetProperty("scopes[0]", out var scopeErrors).ShouldBeTrue(
            "o erro tem de dizer QUAL escopo está errado, não só que 'algum' está");

        scopeErrors.GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Um_client_sem_escopo_nenhum_e_rejeitado()
    {
        var client = await LoginAsync(Permissions.Clients.Create.Value, Permissions.SuperAdmin);

        var response = await client.PostAsJsonAsync(
            "/machine/clients",
            new { name = "Client Inútil", scopes = Array.Empty<string>() },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Quem_nao_tem_clients_create_nao_cria_client_nenhum()
    {
        // Ele é até superadmin de USUÁRIOS — mas não pode administrar credenciais de máquina.
        var client = await LoginAsync(Permissions.Users.Read.Value);

        var response = await client.PostAsJsonAsync(
            "/machine/clients",
            new { name = "Client", scopes = new[] { Permissions.Users.Read.Value } },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // --- helpers -------------------------------------------------------------

    /// <summary>
    /// Cria um usuário com as permissões dadas (concedidas <b>diretamente</b>, sem papel), faz
    /// login e devolve um cliente HTTP já com o cookie e o header de CSRF prontos.
    /// </summary>
    private async Task<HttpClient> LoginAsync(params string[] permissions)
    {
        var email = await SeedUserAsync(permissions);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true
        });

        var login = await client.PostAsJsonAsync(
            "/auth/login",
            new { email, password = Password },
            TestContext.Current.CancellationToken);

        login.StatusCode.ShouldBe(HttpStatusCode.OK);

        var csrf = login.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith("garius.csrf-token=", StringComparison.Ordinal))
            .Split(';')[0]
            .Split('=', 2)[1];

        client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);

        return client;
    }

    private async Task<string> SeedUserAsync(string[] permissions)
    {
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();

        var email = $"user-{Guid.NewGuid():N}@empresa.com";

        var user = new ApplicationUser
        {
            EmailPii = Pii.Create(PiiScope.Email, email),
            Cpf = Pii.Empty(PiiScope.Cpf),
            DisplayName = "Admin de Teste"
        };
        user.UserName = user.Id.ToString();

        (await users.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();

        var tenant = new Tenant
        {
            Name = "Empresa",
            Slug = $"emp-{Guid.NewGuid():N}"[..20]
        };

        db.Tenants.Add(tenant);
        db.UserTenants.Add(new ApplicationUserTenant
        {
            UserId = user.Id,
            TenantId = tenant.Id,
            IsDefault = true
        });

        // Permissões avulsas (sem papel): é o caminho mais curto para o teste, e exercita o
        // mesmo resolver.
        foreach (var permission in permissions)
        {
            db.UserClaims.Add(new ApplicationUserClaim
            {
                UserId = user.Id,
                ClaimType = Permission.ClaimType,
                ClaimValue = permission
            });
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // O resolver cacheia por 5 minutos. Sem invalidar, um usuário criado agora poderia
        // herdar a entrada de cache de um teste anterior.
        await resolver.InvalidateAllAsync(TestContext.Current.CancellationToken);

        return email;
    }
}
