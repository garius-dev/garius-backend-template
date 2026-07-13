using Garius.Core.Authorization;
using Garius.Infrastructure.Database;
using Garius.Infrastructure.Messaging;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;

namespace Garius.Api.Infrastructure.Jobs;

/// <summary>
/// Jobs de background (Hangfire), no <b>banco próprio</b> que o bootstrap já cria
/// (<c>hangfire_{slug}</c>) — ver <c>DatabaseNaming</c>.
///
/// <para>
/// <b>Um banco separado, não um schema.</b> O Hangfire cria e migra as próprias tabelas em
/// runtime, e precisa de DDL para isso. Deixá-lo no banco de negócio significaria dar DDL ao
/// usuário da aplicação <b>no banco de negócio</b> — e aí um bug (ou um SQL injection) poderia
/// derrubar tabelas de dado real. Separado, o estrago fica contido nas tabelas de job.
/// </para>
/// </summary>
internal static class HangfireSetup
{
    internal static IServiceCollection AddBackgroundJobs(
        this IServiceCollection services,
        DatabaseNaming naming)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(naming);

        services.AddHangfire(configuration => configuration
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(options =>
                options.UseNpgsqlConnection(naming.HangfireConnectionString)));

        services.AddHangfireServer(options =>
        {
            // O nome da fila é o da aplicação: várias apps podem compartilhar a infra sem que
            // uma pegue os jobs da outra.
            options.Queues = ["default"];

            // Conservador de propósito. O padrão do Hangfire é 20 × núcleos — o que, num
            // container pequeno, abre dezenas de conexões de banco simultâneas e esgota o pool
            // da APLICAÇÃO (que divide o mesmo Postgres). O sintoma é a API ficando lenta
            // quando um job roda, e ninguém liga uma coisa à outra.
            options.WorkerCount = Math.Min(Environment.ProcessorCount * 2, 8);
        });

        return services;
    }

    /// <summary>
    /// Agenda os jobs recorrentes e expõe o dashboard.
    /// </summary>
    internal static void UseBackgroundJobs(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // ⚠️ O DASHBOARD DO HANGFIRE É UMA PORTA DOS FUNDOS.
        //
        // Ele não só MOSTRA os jobs: ele permite DISPARÁ-LOS e APAGÁ-LOS. Exposto sem
        // autenticação — que é o comportamento PADRÃO dele fora de localhost — qualquer um na
        // internet reexecuta qualquer job da aplicação. É uma das falhas mais comuns em
        // aplicações .NET expostas, e ela é silenciosa: o dashboard simplesmente funciona.
        //
        // Aqui ele exige a permissão jobs.read, pelo mesmo modelo de autorização de todo o
        // resto (ver IDashboardAuthorizationFilter abaixo).
        app.UseHangfireDashboard("/jobs", new DashboardOptions
        {
            Authorization = [new PermissionDashboardFilter(Permissions.Jobs.Read)],

            // Sem isto, o dashboard é somente-leitura para quem não é "local" — e o admin
            // legítimo não conseguiria reprocessar um job falho.
            IsReadOnlyFunc = _ => false
        });

        // Drena o outbox. A cada minuto: é o intervalo em que um evento perdido ainda parece
        // "imediato" para um humano, sem martelar o banco.
        //
        // O job é IDEMPOTENTE por construção (FOR UPDATE SKIP LOCKED — ver OutboxProcessor):
        // várias réplicas rodando-o ao mesmo tempo não processam a mesma mensagem duas vezes.
        RecurringJob.AddOrUpdate<OutboxProcessor>(
            "outbox-drain",
            processor => processor.ProcessAsync(CancellationToken.None),
            Cron.Minutely);
    }
}

/// <summary>
/// Protege o dashboard do Hangfire com o <b>mesmo modelo de permissão</b> do resto da API.
///
/// <para>
/// O Hangfire tem o próprio conceito de autorização (<c>IDashboardAuthorizationFilter</c>), que
/// não conhece o ASP.NET Core Authorization. Este filtro é a ponte: ele lê o principal já
/// autenticado (cookie ou JWT) e consulta o <see cref="IPermissionResolver"/> — ou seja, a
/// mesma fonte de verdade que o <c>.RequirePermission(...)</c> usa.
/// </para>
/// </summary>
internal sealed class PermissionDashboardFilter(Permission permission) : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var http = context.GetHttpContext();

        if (http.User.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var resolver = http.RequestServices.GetRequiredService<IPermissionResolver>();

        // O filtro do Hangfire é SÍNCRONO, e o resolver é assíncrono (ele pode ir ao Redis e
        // ao banco). O GetAwaiter().GetResult() é bloqueio de thread — normalmente uma má
        // ideia, e proibido no caminho de request da API.
        //
        // Aqui é aceitável, e não há alternativa: a interface do Hangfire não oferece uma
        // versão assíncrona. O dashboard é acessado por um punhado de administradores, algumas
        // vezes por dia — não é o caminho quente que causaria thread starvation.
        var userId = GetUserId(http);

        if (userId is null)
        {
            return false;
        }

        var granted = resolver
            .GetPermissionsAsync(userId.Value, GetTenantId(http))
            .GetAwaiter()
            .GetResult();

        return granted.Any(g => Permission.Matches(g, permission.Value));
    }

    private static Guid? GetUserId(HttpContext http) =>
        Guid.TryParse(
            http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
            out var id)
            ? id
            : null;

    private static Guid? GetTenantId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirst(AppClaims.TenantId)?.Value, out var id) ? id : null;
}
