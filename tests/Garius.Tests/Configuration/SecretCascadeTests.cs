using Garius.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Garius.Tests.Configuration;

/// <summary>
/// Prova a cascata de configuração contra o <b>Google Secret Manager real</b> — o secret da
/// própria aplicação, lido do <c>appsettings.Development.json</c>.
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
/// Estes testes são <b>ignorados automaticamente</b> quando não há como executá-los: sem
/// credencial do GCP na máquina (<c>GOOGLE_APPLICATION_CREDENTIALS</c>), ou quando o secret
/// configurado <b>ainda não existe</b> — que é o estado de toda aplicação recém-derivada, antes
/// de alguém criar o secret dela.
/// </para>
///
/// <para>
/// ⚠️ <b>Pular, e não falhar.</b> Uma app derivada nasceria com estes dois testes <b>vermelhos</b>
/// até o secret ser criado — e um teste que falha por configuração ausente é um teste que todo
/// mundo aprende a ignorar, inclusive quando ele passar a apontar um problema de verdade.
/// </para>
/// </summary>
public class SecretCascadeTests
{
    private const string ProjectId = "garius-tcm";

    /// <summary>
    /// O secret <b>da aplicação</b> — lido do <c>appsettings.Development.json</c>, e não escrito à
    /// mão aqui.
    ///
    /// <para>
    /// Escrito à mão, o <c>dotnet new</c> o substituiria pelo secret da app derivada (como faz com
    /// todo o resto) e o teste passaria a apontar para um secret que talvez ainda não exista — o
    /// que é exatamente o que acontecia. Lendo da configuração, o teste sempre fala do secret que
    /// a aplicação <b>de fato</b> usa.
    /// </para>
    /// </summary>
    private static string SecretName =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build()["GcpSecrets:SecretName"]
        ?? string.Empty;

    private static bool HasGcpCredentials =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS"));

    /// <summary>
    /// O secret existe de verdade no GCP? Uma aplicação recém-derivada aponta para um secret que
    /// ainda não foi criado — e aí não há o que testar.
    /// </summary>
    private static bool SecretExists()
    {
        if (string.IsNullOrWhiteSpace(SecretName))
        {
            return false;
        }

        try
        {
            var client = Google.Cloud.SecretManager.V1.SecretManagerServiceClient.Create();

            client.AccessSecretVersion(
                new Google.Cloud.SecretManager.V1.SecretVersionName(ProjectId, SecretName, "latest"));

            return true;
        }
        catch (Grpc.Core.RpcException)
        {
            // NotFound (o secret não existe) ou PermissionDenied (a credencial não o alcança).
            // Nos dois casos: não há como rodar o teste, e falhar seria mentir sobre a causa.
            return false;
        }
    }

    /// <summary>Só roda se houver credencial <b>e</b> o secret existir.</summary>
    private static void SkipUnlessSecretIsReachable()
    {
        Assert.SkipUnless(HasGcpCredentials, "Sem credencial do GCP nesta máquina.");

        Assert.SkipUnless(
            SecretExists(),
            $"O secret '{SecretName}' (projeto {ProjectId}) não existe ou não está acessível. " +
            "Numa aplicação recém-derivada, isto é o esperado até você criá-lo — ver o README.");
    }

    [Fact]
    public void Secret_Manager_vence_o_appsettings()
    {
        SkipUnlessSecretIsReachable();

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
        SkipUnlessSecretIsReachable();

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

    /// <param name="secretName">
    /// <c>null</c> = o secret da própria aplicação (o caso normal). Os testes que provam o
    /// comportamento com um secret <b>inexistente</b> passam um nome explícito.
    /// </param>
    private static IConfigurationRoot Build(
        string environment,
        Dictionary<string, string?> appsettings,
        bool gcpEnabled = true,
        string? secretName = null)
    {
        var settings = new Dictionary<string, string?>(appsettings)
        {
            ["GcpSecrets:Enabled"] = gcpEnabled.ToString(),
            ["GcpSecrets:ProjectId"] = ProjectId,
            ["GcpSecrets:SecretName"] = secretName ?? SecretName
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
