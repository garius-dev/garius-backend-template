using System.Net;
using Garius.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Garius.Tests;

/// <summary>
/// Testes de fumaça da Fase 0: garantem que o host sobe e que a infra de teste
/// (Testcontainers contra o Docker) funciona. As fases seguintes constroem em cima disto.
/// </summary>
[Collection(ApiCollection.Name)]
public class SmokeTests(ApiFactory factory)
{
    [Fact]
    public async Task Host_sobe_e_responde()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
