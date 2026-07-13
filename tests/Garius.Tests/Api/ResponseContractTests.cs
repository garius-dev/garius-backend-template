using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Garius.Core.Results;
using Garius.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Garius.Tests.Api;

/// <summary>
/// Trava o contrato de resposta com o frontend: sucesso sai no envelope, erro sai em
/// ProblemDetails (RFC 9457), e os dois carregam o mesmo <c>traceId</c> — que é a chave
/// para achar o log correspondente no Grafana.
/// </summary>
[Collection(ApiCollection.Name)]
public class ResponseContractTests(ApiFactory factory)
{
    [Fact]
    public async Task Sucesso_vem_no_envelope_com_traceId()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        json.GetProperty("success").GetBoolean().ShouldBeTrue();
        json.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        json.TryGetProperty("data", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Excecao_nao_tratada_vira_ProblemDetails_sem_vazar_o_erro_interno()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/__test/boom", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = JsonSerializer.Deserialize<JsonElement>(body);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

        // Formato RFC 9457
        json.GetProperty("status").GetInt32().ShouldBe(500);
        json.GetProperty("title").GetString().ShouldNotBeNullOrWhiteSpace();
        json.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        json.GetProperty("code").GetString().ShouldBe("server.unexpected");

        // O detalhe da exceção NUNCA chega ao cliente — só ao log, via traceId.
        body.ShouldNotContain("segredo-que-nao-pode-vazar");
        body.ShouldNotContain("StackTrace");
        body.ShouldNotContain("InvalidOperationException");
    }

    [Fact]
    public async Task Erro_de_negocio_vira_ProblemDetails_com_status_e_codigo_corretos()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/__test/not-found", TestContext.Current.CancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        json.GetProperty("status").GetInt32().ShouldBe(404);
        json.GetProperty("code").GetString().ShouldBe("user.not_found");
    }

    [Fact]
    public async Task Erro_de_validacao_traz_os_campos_invalidos()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/__test/validation", TestContext.Current.CancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        json.GetProperty("code").GetString().ShouldBe("validation.failed");

        var fields = json.GetProperty("errors");
        fields.GetProperty("email")[0].GetString().ShouldBe("E-mail já cadastrado.");
    }

    [Fact]
    public async Task Headers_de_seguranca_estao_presentes()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        response.Headers.GetValues("X-Frame-Options").ShouldContain("DENY");
        response.Headers.GetValues("Referrer-Policy").ShouldContain("no-referrer");

        // Não entrega a stack de graça.
        response.Headers.Contains("Server").ShouldBeFalse();
    }

    [Fact]
    public async Task Health_live_responde_sem_tocar_em_dependencias()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
