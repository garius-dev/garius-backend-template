using System.Net;
using System.Net.Http.Json;
using Garius.Tests.Infrastructure;

namespace Garius.Tests.RateLimiting;

/// <summary>
/// O rate limit por IP, contra a API real.
///
/// <para>
/// Ele é a defesa contra <b>password spraying</b> — a mesma senha tentada em milhares de
/// contas. O lockout do Identity <b>não pega isso</b>: ele é por CONTA, e no spraying cada
/// conta erra uma única vez. São duas dimensões independentes, e faltando uma delas um dos dois
/// ataques passa.
/// </para>
/// </summary>
[Collection(RateLimitCollection.Name)]
public class RateLimitTests(RateLimitedApiFactory factory)
{
    /// <summary>
    /// O limite de <c>/auth/login</c> (2 nesta fábrica) morde — <b>e o 429 chega antes de a
    /// senha sequer ser verificada</b>.
    /// </summary>
    [Fact]
    public async Task O_login_e_bloqueado_depois_de_estourar_o_limite()
    {
        await factory.ResetRateLimitsAsync();

        var client = factory.CreateClient();

        var body = new { email = "qualquer@empresa.com", password = "senha-errada" };

        // As duas primeiras passam pelo rate limit (e falham com 401 — a senha está errada,
        // mas isso é outra camada).
        var first = await client.PostAsJsonAsync("/auth/login", body, TestContext.Current.CancellationToken);
        var second = await client.PostAsJsonAsync("/auth/login", body, TestContext.Current.CancellationToken);

        first.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        second.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // A terceira nem chega ao login: é cortada pelo rate limit.
        var third = await client.PostAsJsonAsync("/auth/login", body, TestContext.Current.CancellationToken);

        third.StatusCode.ShouldBe(
            HttpStatusCode.TooManyRequests,
            "sem isto, um atacante testa a mesma senha em milhares de contas — o lockout do " +
            "Identity não pega isso, porque cada conta erra só uma vez");
    }

    /// <summary>
    /// O <c>Retry-After</c> é o que faz um cliente bem-comportado <b>parar de martelar</b>.
    /// Sem ele, o 429 vira só ruído no log: o cliente continua tentando e continua sendo
    /// bloqueado.
    /// </summary>
    [Fact]
    public async Task A_resposta_bloqueada_traz_Retry_After_e_ProblemDetails()
    {
        await factory.ResetRateLimitsAsync();

        var client = factory.CreateClient();

        HttpResponseMessage? blocked = null;

        for (var i = 0; i < 5 && blocked is null; i++)
        {
            var response = await client.PostAsJsonAsync(
                "/auth/login",
                new { email = "outro@empresa.com", password = "x" },
                TestContext.Current.CancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                blocked = response;
            }
        }

        blocked.ShouldNotBeNull("o limite tinha de ter sido atingido");

        blocked.Headers.RetryAfter.ShouldNotBeNull(
            "sem Retry-After o cliente não sabe quando voltar, e continua martelando");

        // 429 sai em ProblemDetails, como TODO erro da API — o front não precisa de um caso
        // especial para ele.
        blocked.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        var body = await blocked.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(
            TestContext.Current.CancellationToken);

        body.GetProperty("code").GetString().ShouldBe("rate_limit.exceeded");
        body.GetProperty("status").GetInt32().ShouldBe(429);
        body.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
    }

    /// <summary>
    /// O <c>/auth/token</c> (M2M) tem o <b>próprio</b> limite. Ele nasceu na Fase 4c como uma
    /// superfície de brute force <b>sem nenhuma proteção</b> — um client não é uma conta, e
    /// portanto não tem lockout. Este limite é a única que ele tem.
    /// </summary>
    [Fact]
    public async Task O_endpoint_de_token_M2M_tem_o_proprio_limite()
    {
        await factory.ResetRateLimitsAsync();

        var client = factory.CreateClient();

        var body = new
        {
            grant_type = "client_credentials",
            client_id = "cid_inexistente",
            client_secret = "qualquer"
        };

        await client.PostAsJsonAsync("/auth/token", body, TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync("/auth/token", body, TestContext.Current.CancellationToken);

        var third = await client.PostAsJsonAsync(
            "/auth/token", body, TestContext.Current.CancellationToken);

        third.StatusCode.ShouldBe(
            HttpStatusCode.TooManyRequests,
            "um client não tem lockout — sem rate limit, /auth/token aceita tentativas infinitas");
    }

    /// <summary>
    /// Endpoints diferentes têm <b>partições diferentes</b>: estourar o limite do login não
    /// pode derrubar o resto da API. Do contrário, um atacante bloquearia a aplicação inteira
    /// só martelando o login.
    /// </summary>
    [Fact]
    public async Task Estourar_o_limite_de_um_endpoint_nao_bloqueia_os_outros()
    {
        await factory.ResetRateLimitsAsync();

        var client = factory.CreateClient();

        // Estoura o login (limite 2).
        for (var i = 0; i < 4; i++)
        {
            await client.PostAsJsonAsync(
                "/auth/login",
                new { email = "spam@empresa.com", password = "x" },
                TestContext.Current.CancellationToken);
        }

        // O ping continua respondendo: ele está na partição GLOBAL, que é outra.
        var ping = await client.GetAsync("/", TestContext.Current.CancellationToken);

        ping.StatusCode.ShouldNotBe(
            HttpStatusCode.TooManyRequests,
            "as partições são independentes — senão, martelar o login derrubaria a API inteira");
    }

    /// <summary>
    /// A <b>segunda camada</b>: um teto geral, que existe para conter um cliente descontrolado
    /// (um retry-loop mal escrito, um scraper) mesmo fora dos endpoints de autenticação.
    /// </summary>
    [Fact]
    public async Task O_limite_GLOBAL_tambem_morde()
    {
        await factory.ResetRateLimitsAsync();

        var client = factory.CreateClient();

        HttpStatusCode? last = null;

        // O global é 100 nesta fábrica. A 101ª tem de ser cortada.
        for (var i = 0; i < 101; i++)
        {
            var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

            last = response.StatusCode;

            if (last == HttpStatusCode.TooManyRequests)
            {
                break;
            }
        }

        last.ShouldBe(
            HttpStatusCode.TooManyRequests,
            "o teto global existe para conter um cliente descontrolado — um retry-loop mal " +
            "escrito, um scraper — mesmo fora dos endpoints de autenticação");
    }
}
