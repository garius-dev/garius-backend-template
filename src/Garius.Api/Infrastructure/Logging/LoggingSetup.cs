using System.Security.Claims;
using Garius.Api.Infrastructure.Networking;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.Grafana.Loki;

namespace Garius.Api.Infrastructure.Logging;

/// <summary>
/// Serilog → console (dev) e Loki (produção).
///
/// <para>
/// <b>Logs limpos</b> é requisito, não preferência: no Loki, ruído custa dinheiro e
/// esconde o incidente real. Os níveis mínimos vêm de <c>Serilog:MinimumLevel:Override</c>
/// no appsettings.
/// </para>
///
/// <para>
/// <b>Por que <c>Microsoft.EntityFrameworkCore.Database.Command</c> = <c>Fatal</c></b>
/// (e não <c>Warning</c>, que pareceria mais natural):
/// </para>
/// <list type="bullet">
///   <item><c>Fatal</c> é o nível mais alto do Serilog, e o EF nunca loga nele — na prática
///         isso <b>silencia a categoria por completo</b>, garantindo que nenhuma query SQL
///         (com os parâmetros: e-mails, CPFs, hashes) chegue ao log.</item>
///   <item>No primeiro boot, o EF consulta <c>__EFMigrationsHistory</c> <b>antes</b> de a
///         tabela existir e loga a falha esperada como <c>ERROR</c>. Com qualquer nível
///         menor, todo deploy inicial dispararia um alerta falso.</item>
/// </list>
///
/// <para>
/// Não é possível documentar isso no próprio appsettings: o Serilog lê <b>toda</b> chave
/// de <c>Override</c> como um namespace de log, então um <c>"_comment"</c> ali quebra o boot.
/// </para>
/// </summary>
internal static class LoggingSetup
{
    /// <summary>Rotas que não geram log de acesso: ruído de health check e scraping.</summary>
    private static readonly string[] NoiseRoutes =
    [
        "/health",
        "/healthz",
        "/metrics",
        "/favicon.ico"
    ];

    internal static void ConfigureSerilog(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var configuration = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration)
            .Enrich.FromLogContext()
            .Enrich.WithEnvironmentName()
            .Enrich.WithProperty("Application", builder.Environment.ApplicationName)
            // Redige valores sensíveis por nome, em qualquer evento. Ver SensitiveDataMasker.
            .Enrich.With(new SensitiveDataMasker())
            .WriteTo.Console();

        var lokiEnabled = builder.Configuration.GetValue<bool>("Serilog:Loki:Enabled");
        var lokiUrl = builder.Configuration["Serilog:Loki:Url"];

        // ⚠️ FALHA FECHADA: Loki ligado e sem Url derruba o boot.
        //
        // Antes, a condição do `if` abaixo exigia as duas coisas e simplesmente NÃO registrava o
        // sink quando a Url faltava. Foi exatamente o que aconteceu em produção: o
        // appsettings.Production.json ligava `Enabled: true` e nunca definia a `Url` — a
        // aplicação subia, logava só no console, e o Loki ficava VAZIO.
        //
        // Sem um erro. Sem um aviso. O deploy passava, o health check passava, e a única pista
        // era a ausência de algo — que ninguém procura até precisar do log de um incidente e
        // descobrir que não existe log nenhum.
        //
        // Observabilidade que falha em silêncio é PIOR que não ter observabilidade: você acha
        // que tem. Se alguém pediu Loki, ou ele funciona, ou a aplicação não sobe.
        if (lokiEnabled && string.IsNullOrWhiteSpace(lokiUrl))
        {
            throw new InvalidOperationException(
                "Serilog:Loki:Enabled=true, mas Serilog:Loki:Url não foi configurada. " +
                "Sem a URL, o sink do Loki NÃO é registrado e a aplicação subiria sem " +
                "observabilidade nenhuma, em silêncio. Configure a URL (em produção, o nome do " +
                "container: http://loki:3100) ou desligue o Loki explicitamente.");
        }

        if (lokiEnabled)
        {
            configuration.WriteTo.GrafanaLoki(
                lokiUrl,
                labels:
                [
                    new LokiLabel { Key = "app", Value = builder.Environment.ApplicationName },
                    new LokiLabel { Key = "env", Value = builder.Environment.EnvironmentName }
                ],
                // Estas propriedades viram labels do Loki (indexadas, boas para filtrar).
                // Nunca colocar aqui algo de alta cardinalidade (traceId, userId, IP):
                // cada valor distinto cria um stream novo e degrada o Loki.
                propertiesAsLabels: ["app", "env", "level"]);
        }

        Log.Logger = configuration.CreateLogger();

        builder.Services.AddSerilog();
    }

    /// <summary>
    /// Log de acesso a endpoint: <b>uma linha por request</b>, com o que importa.
    ///
    /// Isto atende o requisito "gerenciar acesso a endpoint, resultados, falhas e avisos
    /// críticos". É indispensável: como silenciamos <c>Microsoft.AspNetCore</c>, o
    /// "Request finished" do framework some — sem isto não haveria <b>nenhum</b> log de acesso.
    /// </summary>
    internal static void UseRequestLogging(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate =
                "{RequestMethod} {RequestPath} respondeu {StatusCode} em {Elapsed:0.0}ms";

            // Falhas e avisos críticos aparecem no nível certo, para o Grafana alertar.
            options.GetLevel = (httpContext, _, exception) =>
            {
                if (exception is not null || httpContext.Response.StatusCode >= 500)
                {
                    return LogEventLevel.Error;
                }

                if (IsNoise(httpContext.Request.Path))
                {
                    return LogEventLevel.Verbose; // abaixo do mínimo: não é emitido
                }

                return httpContext.Response.StatusCode >= 400
                    ? LogEventLevel.Warning
                    : LogEventLevel.Information;
            };

            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                // IP REAL (CF-Connecting-IP validado contra os ranges da Cloudflare).
                // Nunca RemoteIpAddress cru: atrás do Traefik ele é o IP do proxy.
                diagnosticContext.Set("ClientIp", httpContext.GetClientIp());

                var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (userId is not null)
                {
                    diagnosticContext.Set("UserId", userId);
                }
            };
        });
    }

    private static bool IsNoise(PathString path)
    {
        foreach (var route in NoiseRoutes)
        {
            if (path.StartsWithSegments(route, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
