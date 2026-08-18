using System.Net;
using Garius.Tests.Infrastructure;

namespace Garius.Tests.Health;

/// <summary>
/// As três probes do Kubernetes — <c>/health/startup</c>, <c>/health/live</c> e
/// <c>/health/ready</c> — e a diferença de <b>efeito</b> entre elas.
///
/// <para>
/// <b>O que se prova aqui não é "o endpoint responde 200".</b> É que cada probe checa
/// exatamente o que o seu efeito justifica. Trocar uma pela outra tem consequência oposta e
/// severa: um liveness que checasse o Postgres transformaria uma indisponibilidade do banco
/// num restart loop de toda a frota — e reiniciar container não conserta banco de dados.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public class ProbeTests(ApiFactory factory)
{
    [Theory]
    [InlineData("/health/startup")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task As_probes_respondem_sem_autenticacao(string path)
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        // A FallbackPolicy exige autenticação em todo endpoint que não declare o contrário —
        // e o kubelet não carrega cookie de sessão. Sem AllowAnonymous nas probes, o
        // orquestrador receberia 401 e daria o container por morto, em loop.
        response.StatusCode.ShouldNotBe(
            HttpStatusCode.Unauthorized,
            $"{path} exige autenticação: o kubelet não tem sessão, e o pod seria morto em loop");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// O liveness <b>não pode</b> depender de Postgres nem de Redis.
    ///
    /// <para>
    /// Este teste tem dentes de um jeito indireto, e vale explicar: ele afirma que o liveness
    /// responde OK <b>e</b> que a resposta não menciona dependência nenhuma. Se alguém
    /// registrar um check de banco sem tag e ele passar a valer para o liveness, o corpo
    /// deixa de ser "Healthy" puro e o teste acusa.
    /// </para>
    /// </summary>
    [Fact]
    public async Task O_liveness_NAO_checa_dependencias()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        body.ShouldBe(
            "Healthy",
            "o liveness deve ser um predicado vazio: se ele checar o Postgres, uma queda do " +
            "banco vira restart loop de toda a frota");
    }

    [Fact]
    public async Task O_readiness_checa_as_dependencias()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        // Com Postgres e Redis de pé (Testcontainers), o readiness aprova.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// O <c>/health/detail</c> mostra os checks nomeados — é assim que se confirma que o
    /// readiness está de fato olhando Postgres, Redis e o encerramento, e não um conjunto vazio.
    /// </summary>
    [Fact]
    public async Task O_detail_lista_os_checks_de_readiness()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/detail", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        body.ShouldContain("postgres");
        body.ShouldContain("redis");
        body.ShouldContain(
            "shutdown",
            Case.Sensitive,
            "sem o check de shutdown no readiness, o pod continua recebendo tráfego depois do SIGTERM");
    }
}
