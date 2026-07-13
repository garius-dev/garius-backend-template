using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Garius.Tests.Infrastructure;

namespace Garius.Tests.Idempotency;

/// <summary>
/// Idempotência ponta a ponta.
///
/// <para>
/// O que se prova aqui não é que a <b>resposta</b> é a mesma — isso poderia ser coincidência.
/// Prova-se que o <b>efeito colateral não aconteceu de novo</b>: o endpoint
/// <c>/__test/side-effect</c> incrementa um contador, e é o contador que responde à pergunta
/// "a operação foi reexecutada?".
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public class IdempotencyTests(ApiFactory factory)
{
    [Fact]
    public async Task Repetir_com_a_MESMA_chave_nao_reexecuta_a_operacao()
    {
        var client = factory.CreateClient();

        await ResetAsync(client);

        var key = Guid.NewGuid().ToString();

        var first = await PostAsync(client, key);
        var second = await PostAsync(client, key);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        // O CONTADOR é a prova. Se a operação tivesse rodado duas vezes, ele seria 2.
        (await CountAsync(client)).ShouldBe(
            1,
            "a segunda requisição devolveu a resposta guardada — a operação NÃO rodou de novo");

        // E a resposta é literalmente a mesma, não uma nova.
        (await ExecutionCountOf(second)).ShouldBe(await ExecutionCountOf(first));

        // O header diz ao integrador que ele está vendo um replay. Sem isso, quem depura um
        // retry não teria como distinguir replay de execução nova.
        second.Headers.Contains("Idempotency-Replayed").ShouldBeTrue();
    }

    [Fact]
    public async Task Chaves_DIFERENTES_executam_normalmente()
    {
        var client = factory.CreateClient();

        await ResetAsync(client);

        await PostAsync(client, Guid.NewGuid().ToString());
        await PostAsync(client, Guid.NewGuid().ToString());

        (await CountAsync(client)).ShouldBe(
            2,
            "duas chaves diferentes são duas intenções diferentes — as duas têm de executar");
    }

    /// <summary>
    /// <b>Sem a chave, nada muda.</b> A idempotência é opt-in: o middleware não infere uma
    /// chave a partir do corpo ou do usuário. Uma chave inferida transformaria duas operações
    /// legitimamente iguais (comprar o mesmo item duas vezes, de propósito) numa só — em
    /// silêncio.
    /// </summary>
    [Fact]
    public async Task Sem_a_chave_a_requisicao_passa_direto_e_executa_sempre()
    {
        var client = factory.CreateClient();

        await ResetAsync(client);

        await client.PostAsync("/__test/side-effect", null, TestContext.Current.CancellationToken);
        await client.PostAsync("/__test/side-effect", null, TestContext.Current.CancellationToken);
        await client.PostAsync("/__test/side-effect", null, TestContext.Current.CancellationToken);

        (await CountAsync(client)).ShouldBe(
            3,
            "sem Idempotency-Key o middleware não se mete — só o cliente sabe se duas " +
            "requisições idênticas são a mesma intenção ou duas intenções");
    }

    /// <summary>
    /// <b>Um erro NÃO é gravado como resposta idempotente.</b>
    ///
    /// <para>
    /// Se um 500 (banco fora do ar, por exemplo) ficasse guardado, o cliente receberia aquele
    /// mesmo erro a cada retry — <b>por 24 horas</b>, mesmo depois de o problema ter sido
    /// resolvido. A operação ficaria envenenada pela chave, e nada no sistema explicaria por quê.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Uma_falha_NAO_e_gravada_e_a_chave_pode_ser_reusada()
    {
        var client = factory.CreateClient();

        await ResetAsync(client);

        var key = Guid.NewGuid().ToString();

        var failed = await PostAsync(client, key, "/__test/side-effect/fail");

        failed.IsSuccessStatusCode.ShouldBeFalse();

        // A MESMA chave, agora num endpoint que funciona. Se o erro tivesse sido gravado, esta
        // requisição receberia o erro de volta em vez de executar.
        var retried = await PostAsync(client, key);

        retried.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "a reserva é liberada quando a requisição falha — do contrário a chave ficaria " +
            "envenenada, devolvendo o mesmo erro por 24h");
    }

    /// <summary>
    /// <b>Concorrência.</b> Vinte requisições simultâneas com a mesma chave: a operação executa
    /// <b>uma única vez</b>.
    ///
    /// <para>
    /// É o cenário que a idempotência existe para cobrir, e o único em que ela pode falhar em
    /// silêncio: sem o <c>SET NX</c> atômico, todas as vinte leriam "esta chave não existe" e
    /// todas executariam. E como isso só acontece sob concorrência, <b>nunca aparece em teste
    /// manual</b> — só em produção.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Vinte_requisicoes_SIMULTANEAS_com_a_mesma_chave_executam_UMA_vez()
    {
        var client = factory.CreateClient();

        await ResetAsync(client);

        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => PostAsync(client, key)));

        // Cada uma ou executou (200), ou foi replayada (200), ou pegou a operação em andamento
        // (409). O que NENHUMA pode ter feito é executar uma segunda vez.
        (await CountAsync(client)).ShouldBe(
            1,
            "sem o SET NX atômico, as 20 leriam 'chave não existe' e as 20 executariam — " +
            "exatamente a duplicação que a idempotência deveria impedir");

        // Pelo menos uma teve sucesso (a que ganhou a corrida).
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBeGreaterThan(0);

        // E nenhuma recebeu erro de servidor.
        responses.ShouldAllBe(r => (int)r.StatusCode < 500);
    }

    // --- helpers -------------------------------------------------------------

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string key,
        string path = "/__test/side-effect")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);

        request.Headers.Add("Idempotency-Key", key);

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> ResetAsync(HttpClient client) =>
        client.PostAsync("/__test/side-effect/reset", null, TestContext.Current.CancellationToken);

    private static async Task<int> CountAsync(HttpClient client)
    {
        var response = await client.GetAsync(
            "/__test/side-effect-count", TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        return body.GetProperty("data").GetProperty("executionCount").GetInt32();
    }

    private static async Task<int> ExecutionCountOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        return body.GetProperty("data").GetProperty("executionCount").GetInt32();
    }
}
