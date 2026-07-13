using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Garius.Infrastructure.Database;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Garius.Api.Infrastructure.Health;

/// <summary>
/// Três endpoints, com públicos diferentes:
///
/// <list type="bullet">
///   <item><c>/health/live</c> — o processo está vivo? Para o Docker/Traefik saberem se
///         reiniciam o container. Não toca em dependências (um Postgres fora não deve
///         causar um restart loop).</item>
///   <item><c>/health/ready</c> — pronto para receber tráfego? Checa as dependências.
///         É o que o Traefik usa para tirar a instância do balanceamento.</item>
///   <item><c>/health/detail</c> — diagnóstico, com o estado de cada dependência.
///         <b>Exige chave</b> e, sem chave configurada em produção, <b>não é mapeado</b>.</item>
/// </list>
/// </summary>
internal static class HealthSetup
{
    private const string ApiKeyHeader = "X-Health-Key";

    /// <summary>Dependências (Postgres, Redis) se registram com esta tag nas fases seguintes.</summary>
    internal const string ReadyTag = "ready";

    internal static IServiceCollection AddConfiguredHealthChecks(
        this IServiceCollection services,
        DatabaseNaming naming)
    {
        ArgumentNullException.ThrowIfNull(naming);

        var builder = services.AddHealthChecks();

        // A connection string vem do DatabaseNaming — a MESMA fonte que o DbContext usa.
        //
        // No template anterior, o health check montava a sua própria string com uma fórmula
        // diferente da do DbContext, e tentava autenticar com um usuário que não existia.
        // O health check do Postgres NUNCA funcionou, e ninguém percebeu.
        builder.AddNpgSql(naming.AppConnectionString, name: "postgres", tags: [ReadyTag]);

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

        // Vivo: sem checar dependências.
        // AllowAnonymous: o Docker e o Traefik não carregam cookie de sessão. A
        // FallbackPolicy exigiria autenticação e o container seria dado como morto.
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false
        }).AllowAnonymous();

        // Pronto: checa as dependências marcadas com a tag "ready".
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag)
        }).AllowAnonymous();

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
