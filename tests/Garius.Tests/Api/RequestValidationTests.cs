using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Garius.Tests.Infrastructure;

namespace Garius.Tests.Api;

/// <summary>
/// Validação de request, ponta a ponta.
///
/// <para>
/// <b>Estes testes nasceram de dois 500 reais.</b> Uma sonda contra a API mostrou que
/// <c>POST /auth/login</c> com o corpo <c>{}</c> — e com JSON malformado — respondia
/// <b>500 server.unexpected</b>. Um request que o CLIENTE montou errado era reportado como
/// falha do SERVIDOR:
/// </para>
///
/// <list type="bullet">
///   <item>o <c>GetLevel</c> do Serilog marca 5xx como <c>Error</c> — então qualquer scanner
///         batendo na API gerava <b>alarme falso</b> no Grafana;</item>
///   <item>o erro <b>mentia sobre a causa</b>, mandando procurar o bug no servidor;</item>
///   <item>e o corpo vazio ainda <b>percorria o fluxo de login</b>, incluindo o
///         <c>FakePasswordCheckAsync()</c> — ~100ms de CPU queimados de propósito. Mandar
///         corpos vazios era um jeito barato de gastar CPU da API.</item>
/// </list>
///
/// <para>
/// ⚠️ <b>Em Minimal API, Data Annotations não fazem nada.</b> Não existe o <c>[ApiController]</c>
/// do MVC, que era quem ligava a validação automática. Decorar o record com <c>[Required]</c>
/// seria decoração: <i>parece</i> proteger, e não protege. É o <c>ValidationFilter</c> que ocupa
/// esse lugar.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public class RequestValidationTests(ApiFactory factory)
{
    private static readonly string[] InvalidScopes = ["escopo.que.nao.existe"];

    /// <summary>
    /// O corpo <c>{}</c> deixava <c>Email</c> e <c>Password</c> nulos, e o
    /// <c>FindByEmailAsync(null)</c> estourava. <b>500.</b>
    /// </summary>
    [Fact]
    public async Task Corpo_VAZIO_no_login_devolve_400_e_nao_500()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/login", new { }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "corpo inválido é culpa do CLIENTE (400) — um 500 aqui vira alarme falso no Grafana");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        body.GetProperty("code").GetString().ShouldBe("validation.failed");

        // Os erros vêm POR CAMPO, e TODOS de uma vez — é o que o front usa para pintar de
        // vermelho os dois campos, em vez de um por vez.
        var errors = body.GetProperty("errors");

        errors.TryGetProperty("email", out _).ShouldBeTrue("o e-mail faltando tem de aparecer");
        errors.TryGetProperty("password", out _).ShouldBeTrue("a senha faltando tem de aparecer");

        // E o contrato de erro da API continua valendo.
        body.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
    }

    /// <summary>JSON quebrado estourava na DESSERIALIZAÇÃO — antes de existir objeto para validar.</summary>
    [Fact]
    public async Task JSON_malformado_devolve_400_e_nao_500()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/auth/login",
            new StringContent("{ isso nao e json", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "JSON quebrado nem chega ao validator: estoura na desserialização, e quem trata é o " +
            "GlobalExceptionHandler");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        body.GetProperty("code").GetString().ShouldBe("request.invalid_body");
    }

    /// <summary>
    /// Um e-mail sintaticamente inválido é barrado <b>antes</b> do fluxo de login — e não vira
    /// um 401 genérico depois de gastar CPU no hash falso.
    /// </summary>
    [Fact]
    public async Task Email_invalido_e_barrado_ANTES_de_tocar_o_fluxo_de_login()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/login",
            new { email = "isto-nao-e-um-email", password = "qualquer-coisa" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "antes, isto respondia 401 — depois de queimar ~100ms de CPU no FakePasswordCheck");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        body.GetProperty("errors").TryGetProperty("email", out _).ShouldBeTrue();
    }

    /// <summary>
    /// <b>A validação NÃO pode virar um oráculo.</b> Um e-mail bem formado mas inexistente tem de
    /// continuar respondendo <c>401 auth.invalid_credentials</c> — o mesmo que uma senha errada.
    ///
    /// <para>
    /// Se o validator checasse a EXISTÊNCIA do e-mail, ele responderia 400 para "não cadastrado"
    /// e 401 para "senha errada" — e a diferença entre os dois status enumeraria as contas da
    /// aplicação. Seria destruir, pela porta dos fundos, a defesa que o
    /// <c>InvalidCredentials()</c> genérico construiu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_validacao_NAO_vira_um_oraculo_de_enumeracao()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/login",
            new { email = "ninguem-tem-este-email@exemplo.com", password = "uma-senha-qualquer" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "o formato é VÁLIDO — quem decide é o fluxo de login, e ele não diz se a conta existe");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        body.GetProperty("code").GetString().ShouldBe("auth.invalid_credentials");
    }

    /// <summary>
    /// A validação roda <b>DEPOIS</b> da autorização. Um anônimo num endpoint protegido leva
    /// <b>401</b>, e não 400.
    ///
    /// <para>
    /// Não é preciosismo de status code: se a validação rodasse ANTES, um anônimo qualquer faria
    /// a API executar os validators — que podem <b>ir ao banco</b> (ver
    /// <c>ValidationRules.MustExist</c>). Validar viraria um vetor de DoS: consultas gratuitas ao
    /// banco, sem autenticação nenhuma.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Um_ANONIMO_leva_401_antes_de_qualquer_validacao_rodar()
    {
        var client = factory.CreateClient();

        // Corpo deliberadamente INVÁLIDO (nome vazio, escopo inexistente) num endpoint protegido.
        var response = await client.PostAsJsonAsync(
            "/machine/api-keys",
            new { name = "", scopes = InvalidScopes },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "a autorização vem PRIMEIRO — validar antes deixaria um anônimo disparar queries " +
            "ao banco de graça");
    }
}
