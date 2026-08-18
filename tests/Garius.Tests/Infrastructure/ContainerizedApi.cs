using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Garius.Tests.Infrastructure;

/// <summary>
/// A API rodando <b>num container</b>, com Kestrel de verdade — para provar o encerramento
/// gracioso de ponta a ponta.
///
/// <para>
/// <b>Por que não o <see cref="ApiFactory"/>.</b> O <c>WebApplicationFactory</c> usa
/// <c>TestServer</c>, e ele <b>se descarta</b> no <c>StopApplication()</c>: toda requisição
/// seguinte estoura <c>ObjectDisposedException</c>. Ele encerra de imediato — que é exatamente
/// o comportamento que o encerramento gracioso existe para evitar. Não há janela de drenagem
/// para medir, e um teste escrito sobre ele mediria o dublê, não a peça.
/// </para>
///
/// <para>
/// <b>Por que container e não <c>Process.Start</c>.</b> Foi tentado, e falha no Windows: o
/// <c>SIGTERM</c> do POSIX não existe lá, e o equivalente (<c>GenerateConsoleCtrlEvent</c> com
/// <c>CTRL_BREAK</c>) exige que o processo tenha sido criado com <c>CREATE_NEW_PROCESS_GROUP</c>
/// — flag que o <c>ProcessStartInfo</c> do .NET <b>não expõe</b>. Sem ela o sinal não chega:
/// medido, o processo continuava vivo depois de 30 segundos e o log de encerramento nunca saía.
/// </para>
///
/// <para>
/// O <c>docker stop</c> manda um <b>SIGTERM de verdade</b>, com grace period — é literalmente o
/// que o Kubernetes faz. Medido: a aplicação sai com <b>código 0 em ~1 segundo</b>, e o log
/// "Encerrando..." aparece. É o mesmo caminho de código que roda em produção.
/// </para>
/// </summary>
public sealed class ContainerizedApi : IAsyncDisposable
{
    private const string ApplicationName = "ShutdownE2E";

    private readonly INetwork _network = new NetworkBuilder().Build();

    private readonly PostgreSqlContainer _postgres;
    private readonly RedisContainer _redis;

    private IFutureDockerImage? _image;
    private IContainer? _api;

    public ContainerizedApi()
    {
        _postgres = new PostgreSqlBuilder("postgres:17")
            .WithNetwork(_network)
            .WithNetworkAliases("pg")
            .Build();

        _redis = new RedisBuilder("redis:7")
            .WithNetwork(_network)
            .WithNetworkAliases("redis")
            .Build();
    }

    /// <summary>O endereço da API, já com a porta que o Docker mapeou.</summary>
    public string BaseAddress =>
        $"http://{_api!.Hostname}:{_api.GetMappedPublicPort(8080)}";

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _network.CreateAsync(cancellationToken);

        await Task.WhenAll(
            _postgres.StartAsync(cancellationToken),
            _redis.StartAsync(cancellationToken));

        // O banco do Hangfire, que em produção o bootstrap (MIGRATE_ONLY) cria. O nome vem do
        // DatabaseNaming, e não escrito à mão: ele é a fonte única desses nomes, e duplicar a
        // fórmula é o erro que ela existe para impedir.
        await CreateHangfireDatabaseAsync(cancellationToken);

        // Builda a MESMA imagem do Dockerfile de produção — chiseled, não-root, digest pinado.
        // Testar uma imagem diferente da que vai para produção provaria a coisa errada.
        _image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetSolutionDirectory(), string.Empty)
            .WithDockerfile("Dockerfile")
            .WithName($"garius-shutdown-e2e:{Guid.NewGuid():N}")
            .WithCleanUp(true)
            .Build();

        await _image.CreateAsync(cancellationToken);

        _api = new ContainerBuilder(_image)
            .WithNetwork(_network)
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithEnvironment(BuildEnvironment())
            // Espera o startup probe aprovar — é para isso que ele existe. Sem esperar, o teste
            // dispararia carga contra uma porta que ainda não escuta e falharia por um motivo
            // que não tem nada a ver com encerramento.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request
                    .ForPath("/health/startup")
                    .ForPort(8080)))
            .Build();

        await _api.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Manda <b>SIGTERM</b> e espera. É o que o Kubernetes faz ao remover um pod.
    /// </summary>
    /// <param name="graceSeconds">
    /// O grace period, equivalente ao <c>terminationGracePeriodSeconds</c>. Passado o prazo, o
    /// Docker manda <c>SIGKILL</c> — e o container sai com 137, que é a assinatura de uma
    /// drenagem que não coube no tempo.
    /// </param>
    public async Task<ShutdownOutcome> StopWithSigtermAsync(int graceSeconds)
    {
        var started = DateTimeOffset.UtcNow;

        // O Testcontainers usa `docker stop` por baixo: SIGTERM, espera o grace, depois SIGKILL.
        await _api!.StopAsync();

        var elapsed = DateTimeOffset.UtcNow - started;

        var logs = await ReadLogsAsync();

        return new ShutdownOutcome(elapsed, logs);
    }

    /// <summary>
    /// Manda o <c>SIGTERM</c> e, <b>enquanto a aplicação drena</b>, fica perguntando ao
    /// <c>/health/ready</c> se ela ainda se diz pronta.
    ///
    /// <para>
    /// <b>Isto é o que prova a corrida do orquestrador.</b> Sem observar o readiness DURANTE a
    /// janela, um teste de encerramento passa mesmo com o <c>ShutdownState</c> desligado — foi
    /// medido: neutralizar o <c>MarkAsShuttingDown()</c> não fazia nenhum dos outros testes
    /// falhar, porque eles só olham o resultado final.
    /// </para>
    ///
    /// <para>
    /// O que se quer é o instante intermediário: o pod <b>ainda servindo</b> e <b>já dizendo
    /// que não está pronto</b>. É essa combinação que faz o balanceador parar de mandar
    /// tráfego novo sem derrubar o que já estava em voo.
    /// </para>
    /// </summary>
    public async Task<DrainObservation> StopAndWatchReadinessAsync()
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri(BaseAddress),
            Timeout = TimeSpan.FromSeconds(2)
        };

        var statuses = new System.Collections.Concurrent.ConcurrentQueue<System.Net.HttpStatusCode>();
        var polling = true;

        // ⚠️ A ENQUETE COMEÇA ANTES DO SINAL, e sem intervalo entre as tentativas.
        //
        // A primeira versão dormia 50ms entre as chamadas e só via UMA resposta — a janela de
        // drenagem fecha em menos que isso quando há pouca coisa em voo, e o teste falhava por
        // não ter observado nada (não por a defesa estar quebrada). Um teste que perde o
        // instante que deveria medir é pior que nenhum: ele reprova código correto.
        //
        // Sem Task.Delay: o custo de uma chamada HTTP local já é o intervalo natural, e aqui
        // interessa a resolução máxima.
        var watcher = Task.Run(async () =>
        {
            while (Volatile.Read(ref polling))
            {
                try
                {
                    var response = await client.GetAsync("/health/ready");

                    statuses.Enqueue(response.StatusCode);
                }
                catch (HttpRequestException)
                {
                    // A porta fechou: a drenagem terminou. Não é falha — é o fim esperado.
                    break;
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        });

        // Um instante para a enquete pegar o ritmo antes de o sinal chegar.
        await Task.Delay(200);

        var outcome = await StopWithSigtermAsync(graceSeconds: 30);

        Volatile.Write(ref polling, false);

        await watcher;

        return new DrainObservation(outcome, [.. statuses]);
    }

    public async Task<string> ReadLogsAsync()
    {
        if (_api is null)
        {
            return string.Empty;
        }

        var (stdout, stderr) = await _api.GetLogsAsync(timestampsEnabled: false);

        return new StringBuilder(stdout).AppendLine(stderr).ToString();
    }

    private Dictionary<string, string> BuildEnvironment() => new()
    {
        ["ASPNETCORE_ENVIRONMENT"] = "Development",

        // Diz à aplicação que ela está DENTRO de um container: os endereços valem como estão
        // (fora do Docker ela os trocaria por localhost — ver DockerAwareHost).
        ["DOCKER_RUN"] = "true",

        ["Database__Host"] = "pg",
        ["Database__Port"] = "5432",
        ["Database__RootUsername"] = "postgres",
        ["Database__RootPassword"] = "postgres",
        ["Database__AppUsername"] = "postgres",
        ["Database__AppPassword"] = "postgres",
        ["Database__DatabaseName"] = "postgres",
        ["Database__ApplicationName"] = ApplicationName,

        ["Redis__ConnectionString"] = "redis:6379",
        ["Redis__Password"] = string.Empty,
        ["Redis__InstanceName"] = $"e2e-{Guid.NewGuid():N}",

        ["Encryption__Keys__1"] = "ZFbLDHAltmKIu1ANyNd7XyLre4jRiwYwKWjL8Lrn7nU=",
        ["Encryption__ActiveKeyVersion"] = "1",
        ["Encryption__BlindIndexKey"] = "ywIgmu+JbmkZ2HMcpLnWgheAF0CxDQlVZrRjT3VpaO4=",

        ["Jwt__SigningKey"] = "3Vk8pQjXzL2mNfYtRbHcWdEgUaSoI7xKvJ4nZq1eTpM=",
        ["Jwt__Issuer"] = "https://tests.garius.local",
        ["Jwt__Audience"] = "https://tests.garius.local",

        ["GcpSecrets__Enabled"] = "false",
        ["Serilog__Loki__Enabled"] = "false",
        ["RateLimit__Enabled"] = "false",
        ["Observability__Enabled"] = "false",

        // Curto de propósito: o teste não pode esperar 30s de drenagem. O que se mede é o
        // COMPORTAMENTO (sai do balanceamento, termina o que está em voo), não a duração.
        ["Host__ShutdownTimeoutSeconds"] = "15"
    };

    private async Task CreateHangfireDatabaseAsync(CancellationToken cancellationToken)
    {
        var naming = new Garius.Infrastructure.Database.DatabaseNaming(
            new Garius.Infrastructure.Database.DatabaseOptions { ApplicationName = ApplicationName },
            typeof(ContainerizedApi).Assembly);

        await using var connection = new Npgsql.NpgsqlConnection(_postgres.GetConnectionString());

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText = $"CREATE DATABASE {naming.HangfireDatabaseName}";

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        await Task.WhenAll(
            _postgres.DisposeAsync().AsTask(),
            _redis.DisposeAsync().AsTask());

        if (_image is not null)
        {
            await _image.DisposeAsync();
        }

        await _network.DisposeAsync();
    }
}

/// <param name="Duration">Quanto tempo o container levou para sair depois do SIGTERM.</param>
/// <param name="Logs">A saída do container, onde se lê se a drenagem começou.</param>
public sealed record ShutdownOutcome(TimeSpan Duration, string Logs);

/// <param name="Outcome">O resultado do encerramento.</param>
/// <param name="ReadinessDuringDrain">
/// Cada resposta do <c>/health/ready</c> observada durante a drenagem. Um <c>503</c> aqui é a
/// prova de que o pod se declarou fora do balanceamento ANTES de parar de servir.
/// </param>
public sealed record DrainObservation(
    ShutdownOutcome Outcome,
    IReadOnlyList<System.Net.HttpStatusCode> ReadinessDuringDrain);
