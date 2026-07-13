using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Garius.Core.Identity;
using Garius.Core.Security;
using Garius.Core.Tenancy;
using Garius.Infrastructure.Database;
using Garius.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Garius.Tests.Auth;

/// <summary>
/// O fluxo de autenticação de ponta a ponta, contra a API real (Postgres + Redis via
/// Testcontainers). Testa o que um frontend de verdade faria.
/// </summary>
[Collection(ApiCollection.Name)]
public class AuthFlowTests(ApiFactory factory)
{
    private const string Password = "SenhaForte123!@#";

    [Fact]
    public async Task Login_com_um_unico_tenant_cria_a_sessao_direto()
    {
        var email = await SeedUserAsync(tenantCount: 1);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/login", new { email, password = Password }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var data = body.GetProperty("data");

        data.GetProperty("authenticated").GetBoolean().ShouldBeTrue(
            "com um único tenant não há o que escolher — a sessão nasce no login");

        // O e-mail volta MASCARADO. A máscara preserva o domínio (e a 1ª letra) de propósito:
        // é o bastante para o titular reconhecer o próprio e-mail, sem identificá-lo a terceiros.
        var masked = data.GetProperty("user").GetProperty("email").GetString()!;
        masked.ShouldStartWith("u***@");
        masked.ShouldNotContain(email, Case.Sensitive, "o e-mail em claro não pode voltar no login");

        var cookies = response.Headers.GetValues("Set-Cookie").ToList();

        // O cookie de SESSÃO é HttpOnly: o JavaScript não pode lê-lo, então um XSS não rouba
        // a sessão.
        var session = cookies
            .Single(c => c.StartsWith("__Host-garius.auth=", StringComparison.Ordinal));
        session.ShouldContain("HttpOnly");

        // O request token de CSRF NÃO é HttpOnly — de propósito: o front precisa lê-lo para
        // reenviá-lo no header. Ele não autentica ninguém; só prova a mesma origem.
        var csrf = cookies
            .Single(c => c.StartsWith("garius.csrf-token=", StringComparison.Ordinal));
        csrf.ShouldNotContain("HttpOnly");
    }

    /// <summary>
    /// A consequência do vínculo N:N: com vários tenants o login <b>não</b> cria a sessão —
    /// devolve a lista e espera a escolha.
    /// </summary>
    [Fact]
    public async Task Login_com_varios_tenants_exige_a_selecao()
    {
        var email = await SeedUserAsync(tenantCount: 3);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/login", new { email, password = Password }, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var data = body.GetProperty("data");

        data.GetProperty("authenticated").GetBoolean().ShouldBeFalse();
        data.GetProperty("tenants").GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task Senha_errada_e_e_mail_inexistente_dao_a_MESMA_resposta()
    {
        var email = await SeedUserAsync(tenantCount: 1);
        var client = factory.CreateClient();

        var wrongPassword = await client.PostAsJsonAsync(
            "/auth/login", new { email, password = "senha-errada" }, TestContext.Current.CancellationToken);

        var unknownEmail = await client.PostAsJsonAsync(
            "/auth/login",
            new { email = "ninguem@lugar-nenhum.com", password = Password },
            TestContext.Current.CancellationToken);

        // Respostas idênticas. Uma mensagem específica ("e-mail não cadastrado") transformaria
        // o login num oráculo de enumeração de contas.
        wrongPassword.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknownEmail.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var a = await wrongPassword.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var b = await unknownEmail.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        a.GetProperty("code").GetString().ShouldBe(b.GetProperty("code").GetString());
        a.GetProperty("detail").GetString().ShouldBe(b.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Um_endpoint_protegido_exige_sessao()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/auth/me", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// O ciclo completo que um frontend executa: login → me → refresh → logout.
    ///
    /// <para>
    /// Note o <b>header de CSRF</b> nos POSTs: é exatamente o que o front tem de fazer. O
    /// cookie de sessão é <c>HttpOnly</c> e o navegador o envia sozinho — inclusive numa
    /// requisição disparada por um site malicioso. O token de CSRF é a prova de que a chamada
    /// partiu de código rodando na mesma origem.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Ciclo_completo_login_me_refresh_logout()
    {
        var email = await SeedUserAsync(tenantCount: 1);

        // HandleCookies: o HttpClient guarda e reenvia os cookies, como um navegador.
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true
        });

        // 1. login
        var login = await client.PostAsJsonAsync(
            "/auth/login", new { email, password = Password }, TestContext.Current.CancellationToken);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);

        // O login devolve o cookie de CSRF (legível pelo JS). O front o lê e passa a enviá-lo
        // no header em toda requisição que altera estado.
        var csrf = ExtractCookie(login, "garius.csrf-token");
        csrf.ShouldNotBeNullOrEmpty("o login precisa emitir o token de CSRF legível pelo front");

        client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);

        // 2. /me — a sessão funciona (GET não exige CSRF: não altera estado)
        var me = await client.GetAsync("/auth/me", TestContext.Current.CancellationToken);
        me.StatusCode.ShouldBe(HttpStatusCode.OK);

        var meBody = await me.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        meBody.GetProperty("data").GetProperty("permissions").ValueKind.ShouldBe(JsonValueKind.Array);

        // 3. refresh — a sessão é renovada
        var refresh = await client.PostAsync("/auth/refresh", null, TestContext.Current.CancellationToken);
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);

        // 4. logout
        var logout = await client.PostAsync("/auth/logout", null, TestContext.Current.CancellationToken);
        logout.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// A proteção de CSRF, provada: um POST com o cookie de sessão mas <b>sem</b> o header é
    /// rejeitado. É exatamente o que um site malicioso conseguiria montar — o navegador envia
    /// o cookie sozinho, mas a same-origin policy o impede de <i>ler</i> o valor para montar o
    /// header.
    /// </summary>
    [Fact]
    public async Task Um_POST_com_cookie_mas_SEM_token_de_CSRF_e_rejeitado()
    {
        var email = await SeedUserAsync(tenantCount: 1);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true
        });

        await client.PostAsJsonAsync(
            "/auth/login", new { email, password = Password }, TestContext.Current.CancellationToken);

        // Sem o header X-CSRF-Token — como faria um site de terceiro.
        var response = await client.PostAsync("/auth/refresh", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "o cookie HttpOnly viaja sozinho; sem o token de CSRF, um site malicioso poderia " +
            "disparar ações em nome do usuário logado");
    }

    /// <summary>
    /// <b>Detecção de reuso, ponta a ponta.</b> Um atacante rouba o refresh token e o usa. O
    /// usuário legítimo tenta usar o dele (já consumido) → a sessão inteira cai, e o token do
    /// atacante morre junto.
    /// </summary>
    [Fact]
    public async Task Reusar_um_refresh_token_derruba_a_sessao_inteira()
    {
        var email = await SeedUserAsync(tenantCount: 1);

        var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync(
            "/auth/login", new { email, password = Password }, TestContext.Current.CancellationToken);

        var refreshToken = ExtractCookie(login, "__Host-garius.refresh");
        refreshToken.ShouldNotBeNullOrEmpty();

        // Sem cookie de SESSÃO, só o de refresh: o CSRF não se aplica (não há sessão ambiente
        // a ser explorada — ver CsrfProtection.RequiresCsrfValidation).
        //
        // O ATACANTE roubou o token e o usa primeiro. Ele funciona — é um token válido.
        var attacker = factory.CreateClient();
        attacker.DefaultRequestHeaders.Add("Cookie", $"__Host-garius.refresh={refreshToken}");

        var attackerRefresh = await attacker.PostAsync(
            "/auth/refresh", null, TestContext.Current.CancellationToken);

        attackerRefresh.StatusCode.ShouldBe(HttpStatusCode.OK);

        var attackerNewToken = ExtractCookie(attackerRefresh, "__Host-garius.refresh");

        // A VÍTIMA tenta renovar com o token dela — o mesmo, agora já consumido. Reuso.
        var victim = factory.CreateClient();
        victim.DefaultRequestHeaders.Add("Cookie", $"__Host-garius.refresh={refreshToken}");

        var victimRefresh = await victim.PostAsync(
            "/auth/refresh", null, TestContext.Current.CancellationToken);

        victimRefresh.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "um token já consumido não pode ser aceito");

        // E o token que o ATACANTE conquistou morreu junto: a família inteira foi revogada.
        var attackerAgain = factory.CreateClient();
        attackerAgain.DefaultRequestHeaders.Add("Cookie", $"__Host-garius.refresh={attackerNewToken}");

        var attackerSecondTry = await attackerAgain.PostAsync(
            "/auth/refresh", null, TestContext.Current.CancellationToken);

        attackerSecondTry.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "detectar o reuso precisa derrubar a sessão INTEIRA — do contrário o atacante " +
            "continua com um token válido em mãos");
    }

    // --- helpers -------------------------------------------------------------

    private static string? ExtractCookie(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return null;
        }

        var cookie = cookies.FirstOrDefault(c => c.StartsWith($"{name}=", StringComparison.Ordinal));

        return cookie?.Split(';')[0].Split('=', 2)[1];
    }

    private async Task<string> SeedUserAsync(int tenantCount)
    {
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var email = $"user-{Guid.NewGuid():N}@empresa.com";

        var user = new ApplicationUser
        {
            EmailPii = Pii.Create(PiiScope.Email, email),
            Cpf = Pii.Empty(PiiScope.Cpf),
            DisplayName = "Usuário de Teste"
        };
        user.UserName = user.Id.ToString();

        (await users.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();

        for (var i = 0; i < tenantCount; i++)
        {
            var tenant = new Tenant
            {
                Name = $"Empresa {i}",
                Slug = $"emp-{Guid.NewGuid():N}"[..20]
            };

            db.Tenants.Add(tenant);
            db.UserTenants.Add(new ApplicationUserTenant
            {
                UserId = user.Id,
                TenantId = tenant.Id,
                IsDefault = i == 0
            });
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return email;
    }
}
