using Garius.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.Internal;
using Shouldly;

namespace Garius.Tests.Configuration;

/// <summary>
/// O que faz <b>um secret só</b> servir a máquina do dev e o servidor.
///
/// <para>
/// O Secret Manager guarda os endereços de <b>produção</b> (<c>postgres-prod</c>,
/// <c>redis-prod</c>) — dentro de um container, o endereço do outro serviço é o <b>nome do
/// container</b>. Mas o secret é o <b>mesmo</b> em dev: rodando com <c>dotnet run</c>, esses
/// nomes não resolvem, e o Postgres/Redis estão em <c>localhost</c>.
/// </para>
///
/// <para>
/// A aplicação troca o host <b>sozinha</b> — e só quando as DUAS condições valem: é
/// <c>Development</c> <b>e</b> não está num container. Sem isso, seriam dois secrets (ou dois
/// appsettings) para manter em sincronia — e a cópia esquecida é o bug clássico.
/// </para>
/// </summary>
public class DockerAwareHostTests
{
    /// <summary>
    /// O caso que motiva tudo: <c>dotnet run</c> na máquina do desenvolvedor. O host de produção
    /// que veio do secret precisa virar <c>localhost</c>.
    /// </summary>
    [Fact]
    public void Dev_FORA_do_Docker_troca_o_host_por_localhost()
    {
        var resolved = DockerAwareHost.Resolve("postgres-prod", Config(dockerRun: null), Env("Development"));

        resolved.ShouldBe("localhost");
    }

    /// <summary>
    /// <b>Dentro de um container, o nome do container é o endereço CERTO</b> — mesmo em
    /// Development (um compose de dev existe, e ali `localhost` seria o próprio container).
    /// </summary>
    [Fact]
    public void Dev_DENTRO_do_Docker_mantem_o_host()
    {
        var resolved = DockerAwareHost.Resolve("postgres-prod", Config(dockerRun: "true"), Env("Development"));

        resolved.ShouldBe("postgres-prod");
    }

    /// <summary>
    /// <b>Produção NUNCA é tocada.</b> Se um dia esta troca vazasse para produção, a aplicação
    /// procuraria o Postgres em <c>localhost</c> — dentro do próprio container — e o deploy
    /// morreria. É a asserção mais importante do arquivo.
    /// </summary>
    [Theory]
    [InlineData("Production", null)]
    [InlineData("Production", "true")]
    [InlineData("Staging", null)]
    public void Producao_NUNCA_troca_o_host(string environmentName, string? dockerRun)
    {
        var resolved = DockerAwareHost.Resolve("postgres-prod", Config(dockerRun), Env(environmentName));

        resolved.ShouldBe("postgres-prod");
    }

    /// <summary>
    /// No Redis, troca-se <b>só o host</b>: a porta e as opções (<c>abortConnect</c> etc.) seguem
    /// intactas. Perder a porta apontaria para a 6379 quando o Redis não estivesse nela.
    /// </summary>
    [Theory]
    [InlineData("redis-prod:6379", "localhost:6379")]
    [InlineData("redis-prod:6380", "localhost:6380")]
    [InlineData("redis-prod:6379,abortConnect=false", "localhost:6379,abortConnect=false")]
    public void O_Redis_troca_o_host_e_PRESERVA_porta_e_opcoes(string original, string expected)
    {
        var resolved = DockerAwareHost.ResolveRedis(original, Config(dockerRun: null), Env("Development"));

        resolved.ShouldBe(expected);
    }

    [Fact]
    public void O_Redis_em_producao_fica_INTACTO()
    {
        const string original = "redis-prod:6379,abortConnect=false";

        var resolved = DockerAwareHost.ResolveRedis(original, Config(dockerRun: null), Env("Production"));

        resolved.ShouldBe(original);
    }

    private static IConfiguration Config(string? dockerRun) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DockerAwareHost.DockerRunKey] = dockerRun,
            })
            .Build();

    private static HostingEnvironment Env(string name) => new() { EnvironmentName = name };
}
