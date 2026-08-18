using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Garius.Infrastructure.Database;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Garius.Api.Infrastructure.Health;

/// <summary>
/// Quatro endpoints, com públicos diferentes:
///
/// <list type="bullet">
///   <item><c>/health/startup</c> — já terminou de subir? É o <b>startup probe</b> do
///         Kubernetes. Enquanto ele não passa, o liveness nem é chamado — sem isso, um boot
///         lento (Secret Manager, Redis, EF) é confundido com processo travado e o pod é
///         morto em loop, sem nunca chegar a subir.</item>
///   <item><c>/health/live</c> — o processo está vivo? Para o orquestrador saber se
///         <b>reinicia</b> o container. Não toca em dependências: um Postgres fora não deve
///         causar restart loop (reiniciar não conserta banco de dados).</item>
///   <item><c>/health/ready</c> — pronto para receber tráfego? É o que tira a instância do
///         balanceamento. Checa dependências, <b>com cache</b>, e sabe quando a aplicação
///         está encerrando (ver <see cref="ShutdownState"/>).</item>
///   <item><c>/health/detail</c> — diagnóstico, com o estado de cada dependência.
///         <b>Exige chave</b> e, sem chave configurada em produção, <b>não é mapeado</b>.</item>
/// </list>
///
/// <para>
/// <b>A distinção liveness × readiness não é burocracia.</b> Trocar uma pela outra tem
/// consequência oposta e severa: um liveness que checa o Postgres transforma uma indisponibilidade
/// do banco num <i>restart loop</i> de toda a frota (e o <c>CrashLoopBackOff</c> atrasa a
/// recuperação mesmo depois de o banco voltar). Um readiness que <i>não</i> checa nada mantém
/// no balanceamento um pod que não consegue servir. Cada um checa o que o seu efeito justifica.
/// </para>
/// </summary>
internal static class HealthSetup
{
    private const string ApiKeyHeader = "X-Health-Key";

    /// <summary>Dependências (Postgres, Redis) se registram com esta tag.</summary>
    internal const string ReadyTag = "ready";

    /// <summary>
    /// Checks que entram no <c>/health/detail</c> mas <b>não</b> tiram o pod do balanceamento.
    /// É o caso de coisas cuja degradação não impede servir HTTP — o outbox, por exemplo.
    /// </summary>
    internal const string DiagnosticTag = "diagnostic";

    internal static IServiceCollection AddConfiguredHealthChecks(
        this IServiceCollection services,
        DatabaseNaming naming)
    {
        ArgumentNullException.ThrowIfNull(naming);

        services.AddSingleton<ShutdownState>();
        services.AddSingleton<ReadinessCache>();

        // O filtro é resolvido pelo container (AddEndpointFilter<T> exige isso).
        services.AddSingleton<ReadinessCacheFilter>();

        var builder = services.AddHealthChecks();

        // Encerrando? Então NÃO está pronto — mesmo com todas as dependências de pé.
        // Primeiro da lista de propósito: é o mais barato e o que decide sozinho.
        builder.AddCheck<ShutdownHealthCheck>("shutdown", tags: [ReadyTag]);

        // A connection string vem do DatabaseNaming — a MESMA fonte que o DbContext usa.
        //
        // No template anterior, o health check montava a sua própria string com uma fórmula
        // diferente da do DbContext, e tentava autenticar com um usuário que não existia.
        // O health check do Postgres NUNCA funcionou, e ninguém percebeu.
        //
        // ⚠️ FailureStatus = Degraded, e não Unhealthy (o default). Ver ReadinessCache: um
        // Postgres que pisca por 10s não pode tirar TODAS as réplicas do balanceamento ao
        // mesmo tempo.
        builder.AddNpgSql(
            naming.AppConnectionString,
            name: "postgres",
            failureStatus: HealthStatus.Degraded,
            tags: [ReadyTag]);

        // O MESMO multiplexer que a aplicação usa — resolvido do DI, não uma conexão nova.
        //
        // ⚠️ NÃO monte a conexão a partir de `Redis:ConnectionString` aqui. Essa string NÃO tem
        // a senha (ela vem separada, em `Redis:Password`, porque é segredo e vive no Secret
        // Manager). Um health check que a use crua nunca autentica: o Redis responde
        // "NOAUTH Authentication required", o /health/ready fica Unhealthy PARA SEMPRE — e a
        // aplicação, que monta a conexão certa (ver RedisExtensions.AddRedis), funciona
        // perfeitamente. O sintoma é um container saudável que o orquestrador mata em loop.
        //
        // É EXATAMENTE o bug que o comentário do Postgres, logo acima, descreve: duas fórmulas
        // divergentes para a mesma conexão. Aqui ele aconteceu de novo, no Redis.
        //
        // Aqui o failureStatus é Unhealthy (o default), e a assimetria com o Postgres é
        // deliberada: sem Redis a aplicação não consegue LER O COOKIE (o keyring do
        // DataProtection mora nele), então ela não serve praticamente nada. Sem Postgres,
        // ainda serve o que estiver em cache e os endpoints que não tocam o banco.
        //
        // A factory adia a resolução para depois do Build() — o AddRedis (que registra o
        // multiplexer) roda DEPOIS deste método, então em tempo de registro ele ainda não existe.
        builder.AddRedis(
            sp => sp.GetRequiredService<IConnectionMultiplexer>(),
            name: "redis",
            tags: [ReadyTag]);

        return services;
    }

    internal static void MapConfiguredHealthChecks(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Liga a flag de encerramento no SIGTERM. O ApplicationStopping dispara ANTES de o
        // servidor parar de aceitar conexões — que é justamente a janela que se quer cobrir.
        var shutdown = app.Services.GetRequiredService<ShutdownState>();

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            shutdown.MarkAsShuttingDown();

            Serilog.Log.Information(
                "Encerrando: /health/ready passa a reprovar para sair do balanceamento. " +
                "As requisições em andamento continuam sendo servidas.");
        });

        // Subiu: o startup probe passa a aprovar. Enquanto o ApplicationStarted não dispara,
        // /health/startup responde 503 e o orquestrador continua esperando.
        //
        // AllowAnonymous nos três: o kubelet não carrega cookie de sessão, e a FallbackPolicy
        // exigiria autenticação — o container seria dado como morto.
        var started = false;

        app.Lifetime.ApplicationStarted.Register(() => started = true);

        app.MapGet("/health/startup", (HttpContext http) =>
        {
            http.Response.StatusCode = started
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable;

            return http.Response.WriteAsync(started ? "Started" : "Starting");
        }).AllowAnonymous().ExcludeFromDescription();

        // Vivo: sem checar dependências.
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false
        }).AllowAnonymous();

        // Pronto: checa as dependências marcadas com "ready", com cache.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),

            // Degraded continua sendo 200 (o pod SEGUE no balanceamento) — ver ReadinessCache.
            ResultStatusCodes = new Dictionary<HealthStatus, int>
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
            }
        })
        .AllowAnonymous()
        // A sobrecarga com lambda, e não AddEndpointFilter<T>: o MapHealthChecks devolve um
        // IEndpointConventionBuilder, e a versão tipada exige um RouteHandlerBuilder.
        .AddEndpointFilter(async (context, next) =>
        {
            var filter = context.HttpContext.RequestServices
                .GetRequiredService<ReadinessCacheFilter>();

            return await filter.InvokeAsync(context, next);
        });

        MapDetailedHealthCheck(app);
    }

    private static void MapDetailedHealthCheck(WebApplication app)
    {
        var apiKey = app.Configuration["Health:ApiKey"];

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (app.Environment.IsProduction())
            {
                // NÃO mapeia. O template anterior deixava este endpoint público quando a
                // chave não estava configurada — expondo nomes de dependências, tempos de
                // resposta e mensagens de erro do Postgres/Redis (que carregam host e usuário).
                Serilog.Log.Information(
                    "/health/detail não foi mapeado: Health:ApiKey não está configurado em Production.");
                return;
            }

            // Em Development, sem chave, é liberado — é uma ferramenta de diagnóstico local.
            app.MapHealthChecks("/health/detail", new HealthCheckOptions
            {
                ResponseWriter = WriteDetailedResponse
            }).AllowAnonymous();

            return;
        }

        // Anônimo para o ASP.NET, mas protegido pela chave no filtro abaixo — quem monitora
        // não tem sessão de usuário.
        app.MapHealthChecks("/health/detail", new HealthCheckOptions
        {
            ResponseWriter = WriteDetailedResponse
        })
        .AllowAnonymous()
        .AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;

            // Só via header. Query string vaza em log de proxy e em histórico de browser.
            if (!http.Request.Headers.TryGetValue(ApiKeyHeader, out var provided)
                || !IsValidKey(provided.ToString(), apiKey))
            {
                return Results.NotFound();
            }

            return await next(context);
        });
    }

    /// <summary>Comparação em tempo constante: uma comparação de string comum vaza a chave por timing.</summary>
    private static bool IsValidKey(string provided, string expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    private static async Task WriteDetailedResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = entry.Value.Duration.TotalMilliseconds,
                // A descrição é nossa. A mensagem da exceção NÃO vai — ela carrega
                // host, usuário e schema do banco.
                description = entry.Value.Description
            })
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}
