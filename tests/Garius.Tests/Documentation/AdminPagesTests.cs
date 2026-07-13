using System.Net;
using System.Net.Http.Json;
using Garius.Core.Authorization;
using Garius.Core.Identity;
using Garius.Core.Security;
using Garius.Core.Tenancy;
using Garius.Infrastructure.Database;
using Garius.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Garius.Tests.Documentation;

/// <summary>
/// As páginas administrativas — <c>/jobs</c> (Hangfire) e <c>/scalar</c> (documentação) —
/// e o login que dá acesso a elas.
///
/// <para>
/// O que se prova aqui é uma coisa só, e ela é a razão de toda a Fase 6 existir: <b>as duas
/// páginas ficam acessíveis em produção sem ficarem abertas</b>. Um dashboard do Hangfire
/// exposto é execução remota de código; uma documentação exposta é reconhecimento pronto.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public class AdminPagesTests(ApiFactory factory)
{
    private const string Password = "SenhaForte123!@#";

    /// <summary>
    /// Um navegador. Por padrão <b>não segue</b> redirects, para que os testes possam
    /// inspecioná-los (é a metade do que se está provando: para onde o anônimo é mandado).
    /// </summary>
    private HttpClient CreateBrowser(bool followRedirects = false) => factory.CreateClient(
        new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = followRedirects
        });

    // --- anônimo ------------------------------------------------------------

    [Theory]
    [InlineData("/jobs")]
    [InlineData("/scalar")]
    [InlineData("/openapi/v1.json")]
    public async Task Um_anonimo_NAO_acessa_as_paginas_administrativas(string path)
    {
        var client = CreateBrowser();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldNotBe(
            HttpStatusCode.OK,
            $"{path} aberto ao mundo: o dashboard do Hangfire é execução remota de código, e a " +
            "documentação é o mapa completo da API");
    }

    /// <summary>
    /// Um <b>navegador</b> anônimo é <b>redirecionado</b> para o login — não recebe um 401 sem
    /// corpo, que no navegador é uma tela em branco sem nenhuma explicação.
    /// </summary>
    [Fact]
    public async Task Um_navegador_anonimo_e_redirecionado_para_o_login()
    {
        var client = CreateBrowser();

        var request = new HttpRequestMessage(HttpMethod.Get, "/jobs");
        request.Headers.Add("Accept", "text/html");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        var location = response.Headers.Location!.ToString();

        location.ShouldStartWith("/admin/login");
        location.ShouldContain("returnUrl", Case.Insensitive,
            "depois de logar, o admin tem de voltar para onde estava tentando ir");
    }

    /// <summary>
    /// Um cliente de <b>API</b> (que pede JSON) continua recebendo <b>401</b>, não um redirect.
    /// O comportamento da API não mudou por causa das páginas HTML — a distinção é pelo
    /// <c>Accept</c>.
    /// </summary>
    [Fact]
    public async Task Um_cliente_de_API_anonimo_continua_recebendo_401_e_nao_um_redirect()
    {
        var client = CreateBrowser();

        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        request.Headers.Add("Accept", "application/json");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "uma API JSON não redireciona para tela de login — o front receberia HTML onde " +
            "espera um erro");
    }

    // --- autenticado, mas sem a permissão -----------------------------------

    [Fact]
    public async Task Quem_NAO_tem_jobs_read_recebe_403_no_painel_de_jobs()
    {
        // Ele até vê a documentação — mas não o painel de jobs. As permissões são separadas.
        var client = await LoginAsync(Permissions.Docs.Read.Value);

        var response = await client.GetAsync("/jobs", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "estar logado não basta: o dashboard dispara e apaga jobs");
    }

    [Fact]
    public async Task Quem_NAO_tem_docs_read_recebe_403_na_documentacao()
    {
        var client = await LoginAsync(Permissions.Jobs.Read.Value);

        (await client.GetAsync("/scalar", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // E o JSON do OpenAPI também — proteger só a página seria teatro: bastaria pedir o
        // JSON direto para obter exatamente a mesma informação.
        (await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(
                HttpStatusCode.Forbidden,
                "o JSON tem a MESMA informação que a página — proteger só a página é teatro");
    }

    /// <summary>
    /// Um usuário autenticado sem a permissão recebe <b>403</b>, e <b>não</b> é redirecionado
    /// para o login. Logar de novo não daria a permissão que falta — ele ficaria num laço entre
    /// a página e o formulário, sem entender por quê.
    /// </summary>
    [Fact]
    public async Task Sem_a_permissao_o_navegador_recebe_403_e_NAO_um_laco_de_login()
    {
        var client = await LoginAsync(Permissions.Users.Read.Value);

        var request = new HttpRequestMessage(HttpMethod.Get, "/jobs");
        request.Headers.Add("Accept", "text/html");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "redirecionar para o login aqui criaria um laço: logar de novo não concede a " +
            "permissão que falta");
    }

    // --- autenticado e autorizado -------------------------------------------

    [Fact]
    public async Task Com_jobs_read_o_painel_de_jobs_abre()
    {
        var client = await LoginAsync(Permissions.Jobs.Read.Value);

        var response = await client.GetAsync("/jobs", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Com_docs_read_a_documentacao_abre()
    {
        // O Scalar redireciona /scalar → scalar/ (barra final) antes de servir a página. É
        // comportamento DELE, não da autorização — por isso este cliente SEGUE o redirect, que
        // é o que um navegador faz. Exigir 200 no primeiro request provaria apenas que o Scalar
        // não usa barra final, e não que a documentação abre.
        var browser = await LoginAsync(followRedirects: true, Permissions.Docs.Read.Value);

        (await browser.GetAsync("/scalar", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await browser.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// <b>Os três esquemas de autenticação estão no documento OpenAPI.</b>
    ///
    /// <para>
    /// Sem eles o Scalar é uma vitrine: a página fica bonita, mas <b>nenhum endpoint protegido
    /// pode ser testado</b> — não há onde colar o token, e todo "Send Request" volta 401. O
    /// gerador do .NET não descobre os esquemas sozinho a partir dos handlers registrados.
    /// </para>
    /// </summary>
    [Fact]
    public async Task O_documento_OpenAPI_declara_os_TRES_esquemas_de_autenticacao()
    {
        var client = await LoginAsync(Permissions.Docs.Read.Value);

        var document = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            "/openapi/v1.json", TestContext.Current.CancellationToken);

        var schemes = document.GetProperty("components").GetProperty("securitySchemes");

        schemes.TryGetProperty("cookie", out _).ShouldBeTrue("falta o esquema de cookie (pessoa)");
        schemes.TryGetProperty("Bearer", out _).ShouldBeTrue("falta o esquema Bearer (M2M)");
        schemes.TryGetProperty("ApiKey", out _).ShouldBeTrue("falta o esquema X-Api-Key (terceiro)");

        // Os três são ALTERNATIVAS, não cumulativos: três itens na lista, não um item com as
        // três chaves. Um item só significaria "as três credenciais AO MESMO TEMPO", e o
        // Scalar exigiria todas para deixar qualquer requisição sair.
        document.GetProperty("security").GetArrayLength().ShouldBe(
            3,
            "cookie OU Bearer OU X-Api-Key — não os três juntos");
    }

    // --- o login em si -------------------------------------------------------

    [Fact]
    public async Task A_pagina_de_login_e_publica_e_devolve_HTML()
    {
        var client = CreateBrowser();

        var response = await client.GetAsync("/admin/login", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
    }

    [Fact]
    public async Task Senha_errada_NAO_autentica_e_a_mensagem_nao_diz_se_a_conta_existe()
    {
        var email = await SeedUserAsync(Permissions.Jobs.Read.Value);

        var client = CreateBrowser();

        var response = await PostLoginAsync(client, email, "senha-errada");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // A mensagem aparece na página, dentro do bloco de erro.
        html.ShouldContain("class=\"error\"");

        // Procura o texto SEM o acento: o HtmlEncode escapa "inválidos" para "inv&#225;lidos"
        // — o que é a defesa contra XSS refletido funcionando (todo texto vindo de fora é
        // escapado antes de entrar na página).
        html.ShouldContain("inv&#225;lidos");

        // E não diz se a conta existe: a mensagem vem do AuthService, que é deliberadamente
        // genérica — do contrário o login viraria um oráculo que enumera contas.
        html.ShouldNotContain("cadastrado");
    }

    /// <summary>
    /// <b>Open redirect.</b> Um <c>returnUrl</c> apontando para fora é ignorado.
    ///
    /// <para>
    /// Sem esta checagem, um atacante manda <c>/admin/login?returnUrl=https://site-do-mal.com</c>:
    /// a vítima vê um domínio legítimo, faz login de verdade, e é jogada num clone que pede a
    /// senha "de novo". O link é confiável — só o destino não é.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("https://site-do-mal.com")]
    [InlineData("//site-do-mal.com")]
    public async Task Um_returnUrl_para_FORA_da_aplicacao_e_ignorado(string evil)
    {
        var email = await SeedUserAsync(Permissions.Jobs.Read.Value);

        var client = CreateBrowser();

        var response = await PostLoginAsync(client, email, Password, evil);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        var location = response.Headers.Location!.ToString();

        location.Contains("site-do-mal", StringComparison.Ordinal).ShouldBeFalse(
            "open redirect: a vítima loga num domínio legítimo e é jogada num clone que pede a " +
            "senha de novo");

        location.StartsWith('/').ShouldBeTrue("o destino tem de ser um caminho DESTA aplicação");
    }

    /// <summary>
    /// <b>XSS refletido.</b> O <c>returnUrl</c> volta <i>dentro</i> da página (num campo
    /// hidden), então tudo que vem de fora precisa ser escapado.
    ///
    /// <para>
    /// Sem o escape, isto seria um XSS servido pela <b>própria API</b> — na mesma origem que
    /// guarda o cookie de sessão. O <c>HttpOnly</c> impediria o roubo do cookie, mas o script
    /// rodaria autenticado e poderia simplesmente <b>usar</b> a sessão: disparar jobs, criar um
    /// client M2M com escopo <c>*</c>, ler o que quisesse.
    /// </para>
    /// </summary>
    [Fact]
    public async Task O_returnUrl_e_ESCAPADO_na_pagina_e_nao_vira_XSS()
    {
        var client = CreateBrowser();

        // Um returnUrl que tenta fechar o atributo e injetar um script. Note que ele COMEÇA com
        // "/" — ou seja, passa pela checagem de open redirect, e chega até a renderização.
        const string Payload = "/jobs\"><script>alert(1)</script>";

        var response = await client.GetAsync(
            $"/admin/login?returnUrl={Uri.EscapeDataString(Payload)}",
            TestContext.Current.CancellationToken);

        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        html.ShouldNotContain(
            "<script>alert(1)</script>",
            Case.Insensitive,
            "o returnUrl é refletido na página — sem escape, a própria API serviria o script, " +
            "na mesma origem que guarda o cookie de sessão");

        // O payload continua lá, mas ESCAPADO (inerte).
        html.ShouldContain("&lt;script&gt;");
    }

    [Fact]
    public async Task Um_login_bem_sucedido_redireciona_e_a_sessao_abre_o_painel()
    {
        var email = await SeedUserAsync(Permissions.Jobs.Read.Value);

        var client = CreateBrowser();

        var login = await PostLoginAsync(client, email, Password, "/jobs");

        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        login.Headers.Location!.ToString().ShouldBe("/jobs");

        // O cookie ficou no handler (HandleCookies) — o painel abre.
        var jobs = await client.GetAsync("/jobs", TestContext.Current.CancellationToken);

        jobs.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // --- helpers -------------------------------------------------------------

    private static Task<HttpResponseMessage> PostLoginAsync(
        HttpClient client,
        string email,
        string password,
        string returnUrl = "/jobs") =>
            client.PostAsync(
                "/admin/login",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["email"] = email,
                    ["password"] = password,
                    ["returnUrl"] = returnUrl
                }),
                TestContext.Current.CancellationToken);

    /// <summary>Cria um usuário com as permissões dadas, e devolve um cliente já logado.</summary>
    private Task<HttpClient> LoginAsync(params string[] permissions) =>
        LoginAsync(followRedirects: false, permissions);

    private async Task<HttpClient> LoginAsync(bool followRedirects, params string[] permissions)
    {
        var email = await SeedUserAsync(permissions);

        // O login sempre é feito com um cliente que NÃO segue redirects, e por uma razão
        // prática: seguindo-o, o cliente iria para o returnUrl — e se o usuário não tiver a
        // permissão DAQUELA página, o resultado do login seria um 403, indistinguível de uma
        // falha de autenticação. O teste falharia por um motivo que não tem nada a ver com o
        // que ele mede.
        var loginClient = CreateBrowser(followRedirects: false);

        var response = await PostLoginAsync(loginClient, email, Password);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect, "o login tinha de ter funcionado");

        if (!followRedirects)
        {
            return loginClient;
        }

        // Para os testes que precisam SEGUIR redirects (o Scalar manda /scalar → scalar/),
        // repassa o cookie de sessão para um cliente que os segue.
        var browser = CreateBrowser(followRedirects: true);

        foreach (var cookie in response.Headers.GetValues("Set-Cookie"))
        {
            browser.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        }

        return browser;
    }

    private async Task<string> SeedUserAsync(params string[] permissions)
    {
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();

        var email = $"admin-{Guid.NewGuid():N}@empresa.com";

        var user = new ApplicationUser
        {
            EmailPii = Pii.Create(PiiScope.Email, email),
            Cpf = Pii.Empty(PiiScope.Cpf),
            DisplayName = "Administrador"
        };
        user.UserName = user.Id.ToString();

        (await users.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();

        // UM tenant só: com vários, o login emitiria um cookie parcial e as páginas admin
        // (que não sabem escolher tenant) recusariam — ver AdminEndpoints.
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

        await resolver.InvalidateAllAsync(TestContext.Current.CancellationToken);

        return email;
    }
}
