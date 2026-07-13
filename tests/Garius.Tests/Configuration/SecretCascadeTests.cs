using Garius.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Garius.Tests.Configuration;

/// <summary>
/// Prova a cascata de configuração contra o <b>Google Secret Manager real</b> (projeto
/// garius-tcm, secret gariustech-backend-template-secrets).
///
/// <para>
/// A ordem de precedência é o requisito:
/// <c>Secret Manager &gt; variável de ambiente &gt; appsettings</c>.
/// </para>
///
/// <para>
/// E o requisito inverso importa tanto quanto: <b>a aplicação não pode DEPENDER do GCP</b>.
/// Se um dia ela for vendida, o comprador remove o token e a configuração deve cair
/// sozinha para env var ou appsettings. Isso também é testado aqui.
/// </para>
///
/// <para>
/// Estes testes são <b>ignorados automaticamente</b> se não houver credencial do GCP na
/// máquina (GOOGLE_APPLICATION_CREDENTIALS) — em CI sem credencial a suíte não quebra.
/// </para>
/// </summary>
public class SecretCascadeTests
{
    private const string ProjectId = "garius-tcm";
    private const string SecretName = "gariustech-backend-template-secrets";

    private static bool HasGcpCredentials =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS"));

    [Fact]
    public void Secret_Manager_vence_o_appsettings()
    {
        Assert.SkipUnless(HasGcpCredentials, "Sem credencial do GCP nesta máquina.");

        var config = Build(
            environment: "Development",
            appsettings: new Dictionary<string, string?>
            {
                ["Database:AppPassword"] = "valor-do-appsettings"
            });

        var password = config["Database:AppPassword"];

        password.ShouldNotBe("valor-do-appsettings");
        password.ShouldNotBeNullOrWhiteSpace();
        password!.Length.ShouldBe(40, "a senha real do secret tem 40 caracteres");
    }

    [Fact]
    public void Secret_Manager_vence_a_variavel_de_ambiente()
    {
        Assert.SkipUnless(HasGcpCredentials, "Sem credencial do GCP nesta máquina.");

        // No .NET, "__" é o separador de seção em variável de ambiente.
        Environment.SetEnvironmentVariable("Database__AppPassword", "valor-da-env-var");

        try
        {
            var config = Build(environment: "Development", appsettings: []);

            config["Database:AppPassword"].ShouldNotBe("valor-da-env-var");
        }
        finally
        {
            Environment.SetEnvironmentVariable("Database__AppPassword", null);
        }
    }

    [Fact]
    public void Variavel_de_ambiente_vence_o_appsettings()
    {
        // Não depende do GCP: é a segunda camada da cascata.
        Environment.SetEnvironmentVariable("Database__Host", "host-da-env-var");

        try
        {
            var config = Build(
                environment: "Development",
                appsettings: new Dictionary<string, string?> { ["Database:Host"] = "host-do-appsettings" },
                gcpEnabled: false);

            config["Database:Host"].ShouldBe("host-da-env-var");
        }
        finally
        {
            Environment.SetEnvironmentVariable("Database__Host", null);
        }
    }

    /// <summary>
    /// O cenário do comprador: sem GCP, a aplicação continua funcionando e lê tudo das
    /// camadas de baixo. O template PREFERE o Secret Manager, mas não DEPENDE dele.
    /// </summary>
    [Fact]
    public void Sem_o_GCP_a_configuracao_cai_para_as_camadas_de_baixo()
    {
        var config = Build(
            environment: "Development",
            appsettings: new Dictionary<string, string?>
            {
                ["Database:AppPassword"] = "senha-sem-gcp"
            },
            gcpEnabled: false);

        config["Database:AppPassword"].ShouldBe("senha-sem-gcp");
    }

    /// <summary>
    /// Fora de Production, um GCP indisponível (ou secret inexistente) degrada para as
    /// camadas de baixo em vez de derrubar o boot.
    /// </summary>
    [Fact]
    public void Secret_inexistente_fora_de_producao_nao_derruba_o_boot()
    {
        Assert.SkipUnless(HasGcpCredentials, "Sem credencial do GCP nesta máquina.");

        var config = Build(
            environment: "Development",
            appsettings: new Dictionary<string, string?> { ["Database:AppPassword"] = "fallback" },
            secretName: "secret-que-nao-existe");

        config["Database:AppPassword"].ShouldBe("fallback");
    }

    /// <summary>
    /// Em Production é o oposto: subir com metade da configuração é pior do que não subir.
    /// A app ficaria de pé, aceitaria tráfego e falharia de formas obscuras.
    /// </summary>
    [Fact]
    public void Secret_inexistente_em_producao_derruba_o_boot()
    {
        Assert.SkipUnless(HasGcpCredentials, "Sem credencial do GCP nesta máquina.");

        Should.Throw<SecretsLoadException>(() => Build(
            environment: "Production",
            appsettings: [],
            secretName: "secret-que-nao-existe"));
    }

    private static IConfigurationRoot Build(
        string environment,
        Dictionary<string, string?> appsettings,
        bool gcpEnabled = true,
        string secretName = SecretName)
    {
        var settings = new Dictionary<string, string?>(appsettings)
        {
            ["GcpSecrets:Enabled"] = gcpEnabled.ToString(),
            ["GcpSecrets:ProjectId"] = ProjectId,
            ["GcpSecrets:SecretName"] = secretName
        };

        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)   // faz o papel do appsettings
            .AddEnvironmentVariables();        // segunda camada

        // Terceira camada (maior precedência): o Secret Manager.
        builder.AddSecretSources(new TestHostEnvironment(environment));

        return builder.Build();
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Garius.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
