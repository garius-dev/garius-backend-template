using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Garius.Api.Infrastructure.Observability;

/// <summary>
/// Traços e métricas (OpenTelemetry). O Serilog cobre <b>log</b>, que é outra coisa.
///
/// <para>
/// <b>Log responde "o que aconteceu"; traço responde "onde foi o tempo".</b> Num sistema
/// distribuído, a pergunta que se faz num incidente é "por que este request demorou 3
/// segundos?" — e a resposta está na decomposição: 40ms na API, 2.9s numa query, 60ms no
/// Redis. Sem traço, isso é adivinhação; com ele, é uma tela.
/// </para>
///
/// <para>
/// <b>Degradar aqui é o comportamento certo — e é a única exceção à regra 9 do README.</b>
/// O resto do template falha FECHADO: configuração inválida derruba o boot. Aqui não. Se o
/// endpoint OTLP não estiver configurado, a aplicação sobe e simplesmente não exporta.
/// Telemetria ausente é um problema de operação; derrubar a API por causa dela seria
/// transformar um problema de observabilidade numa indisponibilidade — trocar um prejuízo
/// pequeno por um grande.
/// </para>
///
/// <para>
/// (O Loki é diferente e continua falhando fechado: lá alguém pediu log explicitamente com
/// <c>Enabled: true</c>, e o modo de falha era pior — a aplicação subia parecendo ter
/// observabilidade e o Loki ficava vazio. Ver LoggingSetup.)
/// </para>
/// </summary>
internal static class ObservabilitySetup
{
    /// <summary>
    /// Rotas que <b>não</b> geram traço. As probes são chamadas por cada réplica a cada
    /// poucos segundos: instrumentá-las faria delas a esmagadora maioria dos traços
    /// exportados, afogando os requests reais e inflando o custo do backend.
    /// </summary>
    private static readonly string[] NoiseRoutes =
    [
        "/health",
        "/metrics",
        "/favicon.ico"
    ];

    internal static IServiceCollection AddObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var options = new ObservabilityOptions();
        configuration.GetSection(ObservabilityOptions.SectionName).Bind(options);

        services.AddSingleton(options);

        if (!options.Enabled)
        {
            return services;
        }

        var resource = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: environment.ApplicationName,
                serviceVersion: typeof(ObservabilitySetup).Assembly.GetName().Version?.ToString())
            .AddAttributes(
            [
                new KeyValuePair<string, object>("deployment.environment", environment.EnvironmentName)
            ]);

        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(resource)
                    .AddAspNetCoreInstrumentation(instrumentation =>
                    {
                        // As probes ficam de fora — ver NoiseRoutes.
                        instrumentation.Filter = context =>
                            !NoiseRoutes.Any(route =>
                                context.Request.Path.StartsWithSegments(route));

                        // A exceção vai para o traço, e é isso que liga o span vermelho no
                        // Tempo ao stack trace. Não vaza para o cliente: quem responde ao
                        // cliente é o GlobalExceptionHandler, que devolve ProblemDetails
                        // genérico. São dois canais distintos, de propósito.
                        instrumentation.RecordException = true;
                    })
                    .AddHttpClientInstrumentation();

                // Spans de query do Postgres. É o que decompõe "o request demorou 3s" em
                // "2.9s foram nesta query" — sem isso, o traço mostra só a borda.
                //
                // ⚠️ CHAMADA QUALIFICADA, e não `.AddNpgsql()` na cadeia acima. O nome existe
                // DUAS vezes: aqui (o traço) e no EF (NpgsqlServiceCollectionExtensions, que
                // registra um DbContext). Na forma de extensão o compilador escolhe a do EF e
                // o erro fala de "connectionString faltando", sem nenhuma pista de que o
                // problema é colisão de nome.
                Npgsql.TracerProviderBuilderExtensions.AddNpgsql(tracing);

                // AMOSTRAGEM. Em alta escala, exportar 100% dos traços é caro no backend e na
                // rede — e desnecessário: o valor de um traço está no padrão, não na
                // completude.
                //
                // ParentBased: se o request já chegou com um trace context (o front, ou outro
                // serviço, decidiu amostrar), essa decisão é RESPEITADA. Sem isso, um traço
                // distribuído sairia pela metade — o serviço A amostra, o B descarta, e o
                // resultado é uma cascata truncada que não explica nada.
                tracing.SetSampler(new ParentBasedSampler(
                    new TraceIdRatioBasedSampler(options.SamplingRatio)));

                ConfigureExporter(tracing, options);
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(resource)
                    // RED (rate, errors, duration) por endpoint: as três perguntas que se faz
                    // de um serviço HTTP. Vêm dos meters nativos do ASP.NET Core.
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    // GC, thread pool, exceções. É o que distingue "a aplicação está lenta" de
                    // "a aplicação está sem thread" — dois incidentes com a mesma cara.
                    .AddRuntimeInstrumentation()
                    // Métricas do pool de conexões do Npgsql. É o alarme antecedente do teto
                    // de conexões: réplicas × MaxPoolSize passando do max_connections do
                    // Postgres é uma falha que só aparece no PICO. Ver item 8 do
                    // PLANO-PRODUCAO.md.
                    .AddNpgsqlInstrumentation();

                ConfigureExporter(metrics, options);
            });

        return services;
    }

    /// <summary>
    /// Liga o exportador OTLP, <b>se</b> houver endpoint. Sem endpoint, a instrumentação
    /// continua ativa mas nada sai — útil em teste, e inofensivo em produção.
    /// </summary>
    private static void ConfigureExporter(TracerProviderBuilder tracing, ObservabilityOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OtlpEndpoint))
        {
            return;
        }

        tracing.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.OtlpEndpoint));
    }

    private static void ConfigureExporter(MeterProviderBuilder metrics, ObservabilityOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OtlpEndpoint))
        {
            return;
        }

        metrics.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.OtlpEndpoint));
    }
}

/// <summary>Seção <c>Observability</c>.</summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>
    /// Liga a instrumentação. Desligado, nem os <c>ActivitySource</c> são registrados —
    /// é o que os testes usam para não pagar o custo.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Para onde exportar (o collector, o Tempo, o Jaeger). <b>Vazio = não exporta</b>, e a
    /// aplicação sobe normalmente. Ver a nota sobre degradar em <c>ObservabilitySetup</c>.
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>
    /// Fração dos traços amostrados, de 0 a 1.
    ///
    /// <para>
    /// O default é <b>1.0</b> — tudo — porque um template que amostrasse por padrão faria a
    /// app derivada perder traços em desenvolvimento sem ninguém entender o porquê. Em
    /// produção sob volume alto, baixe para 0.1 ou menos: o valor de um traço está no padrão
    /// que ele revela, não em ter todos.
    /// </para>
    /// </summary>
    public double SamplingRatio { get; set; } = 1.0;
}
