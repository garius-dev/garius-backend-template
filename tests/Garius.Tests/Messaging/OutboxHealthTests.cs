using Garius.Core.Messaging;
using Garius.Core.Tenancy;
using Garius.Infrastructure.Database;
using Garius.Infrastructure.Database.Interceptors;
using Garius.Infrastructure.Messaging;
using Garius.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Garius.Tests.Messaging;

/// <summary>
/// O health check do outbox — o que faz uma fila parada <b>aparecer</b>.
///
/// <para>
/// <b>O buraco que ele tapa.</b> O <c>OutboxProcessor</c> engole exceções de propósito (uma
/// mensagem envenenada não pode derrubar o lote). Mas quando uma mensagem esgota as tentativas,
/// ela sai do <c>WHERE</c> do drenador e <b>some</b>: fica um <c>Error</c> no Loki e nada mais.
/// O evento nunca aconteceu, e o sistema segue como se estivesse tudo bem — até alguém
/// perceber, dias depois, que um e-mail nunca chegou.
/// </para>
/// </summary>
[Collection("Outbox")]
public class OutboxHealthTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    /// <summary>Fila vazia é fila saudável — o caso normal, e a linha de base dos outros testes.</summary>
    [Fact]
    public async Task Fila_vazia_e_saudavel()
    {
        await using var provider = Build();

        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await ClearAsync(db);

        var result = await CheckAsync(scope);

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    /// <summary>
    /// <b>Mensagem morta é falha, não degradação.</b>
    ///
    /// <para>
    /// Uma mensagem que esgotou as tentativas é um evento <b>perdido</b> — o dado foi gravado e
    /// o efeito dele nunca vai acontecer. Isso quebra a única garantia que o outbox existe para
    /// dar (o evento e o dado vivem ou morrem juntos), então precisa gritar, não sussurrar.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Mensagem_que_esgotou_as_tentativas_deixa_o_check_Unhealthy()
    {
        await using var provider = Build();

        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await ClearAsync(db);

        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = nameof(TestEvent),
            Payload = """{"Value":"morta"}""",
            Attempts = OutboxMessage.MaxAttempts
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CheckAsync(scope);

        result.Status.ShouldBe(
            HealthStatus.Unhealthy,
            "uma mensagem morta é um evento que nunca vai acontecer — hoje isso some em " +
            "silêncio, e é exatamente o que este check existe para impedir");

        result.Data["deadMessages"].ShouldBe(1);
    }

    /// <summary>
    /// Fila parada há tempo demais é <b>degradação</b>, não falha: pode ser só carga. Mas
    /// precisa aparecer, porque a idade da mensagem mais antiga é o indicador <b>antecedente</b>
    /// — ela cresce antes de qualquer sintoma visível ao usuário.
    /// </summary>
    [Fact]
    public async Task Fila_parada_ha_tempo_demais_fica_Degraded()
    {
        var time = new FakeTimeProvider();

        await using var provider = Build(time);

        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await ClearAsync(db);

        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = nameof(TestEvent),
            Payload = """{"Value":"parada"}"""
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Bem além do StaleAfterMinutes (15). O tempo avança por chamada de método — um teste
        // que esperasse de verdade seria inviável.
        time.Advance(TimeSpan.FromHours(1));

        var result = await CheckAsync(scope, time);

        result.Status.ShouldBe(
            HealthStatus.Degraded,
            "a idade da mensagem mais antiga é o indicador antecedente: ela cresce ANTES de o " +
            "cliente perceber que um evento não chegou");
    }

    /// <summary>
    /// Uma mensagem recém-enfileirada <b>não</b> alarma. Sem isto, o check gritaria a cada
    /// evento publicado — e um alarme que toca sempre é um alarme que ninguém olha.
    /// </summary>
    [Fact]
    public async Task Mensagem_recente_nao_alarma()
    {
        var time = new FakeTimeProvider();

        await using var provider = Build(time);

        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await ClearAsync(db);

        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = nameof(TestEvent),
            Payload = """{"Value":"recente"}"""
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(1));

        (await CheckAsync(scope, time)).Status.ShouldBe(HealthStatus.Healthy);
    }

    // --- helpers -------------------------------------------------------------

    private static async Task<HealthCheckResult> CheckAsync(
        IServiceScope scope,
        TimeProvider? time = null)
    {
        var check = new OutboxHealthCheck(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            scope.ServiceProvider.GetRequiredService<OutboxOptions>(),
            time ?? TimeProvider.System);

        return await check.CheckHealthAsync(
            new HealthCheckContext(), TestContext.Current.CancellationToken);
    }

    private static async Task ClearAsync(AppDbContext db) =>
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE outbox_messages", TestContext.Current.CancellationToken);

    private ServiceProvider Build(TimeProvider? time = null)
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton(time ?? TimeProvider.System);
        services.AddSingleton(TestCrypto.Encryptor);
        services.AddScoped<ITenantResolver, NoTenantForHealth>();

        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(fixture.PostgresConnectionString)
            .AddInterceptors(new AuditingInterceptor(
                new NoTenantForHealth(), time ?? TimeProvider.System)));

        services.AddOutbox();

        return services.BuildServiceProvider();
    }

    private sealed class NoTenantForHealth : ITenantResolver
    {
        public Guid? CurrentTenantId => null;
    }
}
