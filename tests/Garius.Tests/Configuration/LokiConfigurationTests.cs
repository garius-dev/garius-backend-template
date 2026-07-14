using System.Text.Json;
using Garius.Api.Infrastructure.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Garius.Tests.Configuration;

/// <summary>
/// O Loki tem de <b>realmente</b> receber os logs — ou a aplicação não sobe.
///
/// <para>
/// <b>Estes testes nasceram de um silêncio.</b> O <c>appsettings.Production.json</c> ligava
/// <c>Serilog:Loki:Enabled = true</c> e <b>nunca definia a Url</b>. O
/// <see cref="LoggingSetup.ConfigureSerilog"/> exigia as duas coisas para registrar o sink —
/// então, faltando a Url, ele simplesmente <b>não registrava nada</b>.
/// </para>
///
/// <para>
/// A aplicação subia. O health check passava. O deploy passava. Não havia um erro, um warning,
/// nem uma linha de log dizendo que o log não estava indo a lugar nenhum. O Loki ficava
/// <b>vazio</b>, e a única pista era a <i>ausência</i> de algo — que ninguém procura, até
/// precisar do log de um incidente e descobrir que ele não existe.
/// </para>
///
/// <para>
/// <b>Observabilidade que falha em silêncio é pior que não ter observabilidade:</b> você
/// <i>acha</i> que tem. É o mesmo padrão do <c>MIGRATE_ONLY</c> que não rodava — o componente
/// estava certo, mas o caminho real nunca era exercitado.
/// </para>
/// </summary>
public class LokiConfigurationTests
{
    /// <summary>
    /// O arquivo REAL de produção — o que quebrou. Não adianta o código estar certo se o
    /// appsettings que vai para o container não tem a Url.
    /// </summary>
    [Fact]
    public void O_appsettings_de_PRODUCAO_define_a_Url_do_Loki()
    {
        var production = Path.Combine(FindApiProjectDirectory(), "appsettings.Production.json");

        File.Exists(production).ShouldBeTrue();

        using var document = JsonDocument.Parse(File.ReadAllText(production));

        var loki = document.RootElement
            .GetProperty("Serilog")
            .GetProperty("Loki");

        loki.GetProperty("Enabled").GetBoolean().ShouldBeTrue(
            "produção manda os logs para o Loki");

        var url = loki.TryGetProperty("Url", out var value) ? value.GetString() : null;

        url.ShouldNotBeNullOrWhiteSpace(
            "Enabled=true SEM Url faz o sink do Loki não ser registrado — e a app sobe " +
            "logando só no console, em silêncio, com o Loki vazio");

        // Dentro de um container, `localhost` é o PRÓPRIO container: o log iria para o vazio.
        url.ShouldNotContain(
            "localhost",
            customMessage:
                "em produção o endereço é o NOME DO CONTAINER na rede docker (http://loki:3100) — " +
                "localhost, dentro de um container, é o próprio container");
    }

    /// <summary>
    /// A falha FECHADA: pedir Loki sem dizer onde ele está derruba o boot, em vez de degradar
    /// silenciosamente para "só console".
    /// </summary>
    [Fact]
    public void Loki_LIGADO_e_SEM_Url_derruba_o_boot()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Serilog:Loki:Enabled"] = "true",
            ["Serilog:Loki:Url"] = "",
        });

        var exception = Should.Throw<InvalidOperationException>(
            () => builder.ConfigureSerilog());

        exception.Message.ShouldContain(
            "Serilog:Loki:Url",
            customMessage: "a mensagem tem de dizer QUAL configuração falta");
    }

    /// <summary>
    /// E o contrário continua valendo: quem <b>desliga</b> o Loki de propósito (um teste, um
    /// dev sem a stack de observabilidade rodando) sobe normalmente, no console.
    /// </summary>
    [Fact]
    public void Loki_DESLIGADO_sobe_normalmente()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Serilog:Loki:Enabled"] = "false",
            ["Serilog:Loki:Url"] = "",
        });

        Should.NotThrow(() => builder.ConfigureSerilog());
    }

    private static string FindApiProjectDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine(directory.FullName, "src", "Garius.Api");
    }
}
