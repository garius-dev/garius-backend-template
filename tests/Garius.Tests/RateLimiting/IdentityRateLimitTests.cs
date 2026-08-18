using System.Net;
using Garius.Tests.Infrastructure;

namespace Garius.Tests.RateLimiting;

/// <summary>
/// O rate limit por <b>identidade</b> — a segunda dimensão, aplicada depois da autorização.
///
/// <para>
/// <b>Por que ela existe.</b> Limite só por IP erra dos dois lados: pune o cliente legítimo
/// atrás de CGNAT (milhares de pessoas dividindo um endereço, e portanto uma cota) e não contém
/// o atacante com um <c>/64</c> de IPv6, que tem endereços de sobra para diluir o volume. As
/// duas dimensões são independentes.
/// </para>
///
/// <para>
/// <b>Fábrica própria</b>, e não a <see cref="RateLimitedApiFactory"/> compartilhada: esta
/// camada precisa de um limite baixo para ser exercitada, e mexer no limite da fábrica comum
/// mudaria o comportamento dos outros testes de rate limit — que provam outra coisa.
/// </para>
/// </summary>
[Collection(IdentityRateLimitCollection.Name)]
public class IdentityRateLimitTests(IdentityRateLimitedApiFactory factory)
{
    /// <summary>
    /// Os headers da <b>RFC 9331</b> saem em resposta de <b>sucesso</b>, não só no 429.
    ///
    /// <para>
    /// É a diferença entre um cliente que se comporta e um que martela: sem os headers, o
    /// integrador só descobre o limite ao bater nele, e a única estratégia que lhe resta é
    /// tentar de novo. Com eles, ele sabe quanto lhe resta <b>antes</b> de estourar.
    /// </para>
    ///
    /// <para>
    /// <b>Tem dentes:</b> remova o <c>WriteRateLimitHeaders</c> do middleware e este teste
    /// acusa — enquanto o de bloqueio continua passando.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Os_headers_da_RFC_9331_saem_no_SUCESSO()
    {
        await factory.ResetRateLimitsAsync();

        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        response.Headers.ShouldContain(
            h => h.Key == "RateLimit-Limit",
            "sem RateLimit-Limit, o cliente só descobre o teto batendo nele");

        response.Headers.ShouldContain(h => h.Key == "RateLimit-Remaining");
        response.Headers.ShouldContain(h => h.Key == "RateLimit-Reset");
    }

    /// <summary>
    /// A cota é da <b>credencial</b>, e ela acaba — mesmo com o limite por IP folgado.
    ///
    /// <para>
    /// <b>Tem dentes:</b> tire o <c>UseMiddleware&lt;IdentityRateLimitMiddleware&gt;</c> do
    /// <c>Program.cs</c> e este teste falha, porque o limite global por IP desta fábrica é bem
    /// mais alto — nada barraria a requisição.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_cota_da_identidade_acaba()
    {
        await factory.ResetRateLimitsAsync();

        var client = await factory.CreateAuthenticatedClientAsync();

        HttpResponseMessage? blocked = null;

        // O limite por identidade é 3 nesta fábrica; o global por IP é 100.
        for (var i = 0; i < 10 && blocked is null; i++)
        {
            var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                blocked = response;
            }
        }

        blocked.ShouldNotBeNull(
            "a cota por identidade (3) deveria ter acabado antes da global por IP (100) — " +
            "se não acabou, a camada por identidade não está no pipeline");

        var body = await blocked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.ShouldContain(
            "rate_limit.identity_exceeded",
            Case.Sensitive,
            "o código do erro distingue as duas camadas — sem isso não dá para saber, num " +
            "incidente, se foi o IP ou a credencial que estourou");
    }

    /// <summary>
    /// <b>Anônimo não é contado aqui.</b>
    ///
    /// <para>
    /// Não é brecha: quem não se autenticou já foi contado pela camada de IP, lá na frente do
    /// pipeline. Contar de novo aplicaria dois limites à mesma requisição pelo mesmo motivo — e
    /// o anônimo esgotaria uma cota que não é dele, já que todos compartilham a mesma
    /// "identidade" (nenhuma).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Anonimo_nao_consome_a_cota_de_identidade()
    {
        await factory.ResetRateLimitsAsync();

        var client = factory.CreateClient();

        // Bem mais que o limite por identidade (3), sem autenticação nenhuma.
        for (var i = 0; i < 6; i++)
        {
            var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(
                HttpStatusCode.OK,
                "o anônimo já é contado pela camada de IP; contá-lo aqui também o faria " +
                "esgotar uma cota compartilhada por todos os anônimos");
        }
    }
}
