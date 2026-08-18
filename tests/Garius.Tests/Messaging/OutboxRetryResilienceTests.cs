using Garius.Core.Messaging;
using Garius.Core.Tenancy;
using Garius.Infrastructure.Database;
using Garius.Infrastructure.Database.Interceptors;
using Garius.Infrastructure.Messaging;
using Garius.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Garius.Tests.Messaging;

/// <summary>
/// O outbox rodando com <c>EnableRetryOnFailure</c> ligado — <b>como em produção</b>.
///
/// <para>
/// <b>Esta classe existe por causa de uma lacuna real na suíte.</b> O <c>Build()</c> dos
/// <see cref="OutboxTests"/> monta o <c>DbContext</c> <i>sem</i> o retry, enquanto o
/// <c>PersistenceExtensions</c> — que é o que a aplicação de verdade usa — o liga. As duas
/// configurações divergem, e a divergência importa: com o retry ligado, o EF <b>proíbe</b>
/// transação explícita, e o <c>OutboxProcessor</c> abre uma.
/// </para>
///
/// <para>
/// Ou seja: sem estes testes, a suíte inteira passaria VERDE enquanto o drenador do outbox
/// estouraria no primeiro job em produção — <c>"The configured execution strategy
/// 'NpgsqlRetryingExecutionStrategy' does not support user-initiated transactions"</c>. É
/// exatamente o padrão do bug do <c>MIGRATE_ONLY</c>: testar o componente não é testar o
/// caminho que roda de verdade.
/// </para>
/// </summary>
[Collection("Outbox")]
public class OutboxRetryResilienceTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    /// <summary>
    /// <b>O teste central.</b> Com o retry ligado, o drenador funciona.
    ///
    /// <para>
    /// <b>Tem dentes:</b> tire o <c>CreateExecutionStrategy</c> do
    /// <see cref="OutboxProcessor.ProcessAsync"/> e este teste falha com a exceção acima —
    /// enquanto todos os outros testes de outbox continuam passando.
    /// </para>
    /// </summary>
    [Fact]
    public async Task O_drenador_funciona_com_o_retry_ligado()
    {
        await using var provider = BuildWithRetry();

        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();

        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await ClearAsync(db);

        TestEventHandler.Handled.Clear();

        await outbox.EnqueueAsync(
            new TestEvent("com-retry"), TestContext.Current.CancellationToken);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Sem a execution strategy, esta linha estoura.
        await processor.ProcessAsync(TestContext.Current.CancellationToken);

        TestEventHandler.Handled.ShouldContain(
            "com-retry",
            "o drenador precisa funcionar com a MESMA configuração de DbContext que a " +
            "aplicação usa — e a aplicação liga EnableRetryOnFailure");

        var message = await db.OutboxMessages
            .IgnoreQueryFilters()
            .SingleAsync(TestContext.Current.CancellationToken);

        message.ProcessedAt.ShouldNotBeNull();
    }

    /// <summary>
    /// A configuração de produção realmente tem a execution strategy retentante.
    ///
    /// <para>
    /// Sem esta asserção, o teste acima passaria por vacuidade se alguém desligasse o
    /// <c>EnableRetryOnFailure</c>: o <c>OutboxProcessor</c> voltaria a funcionar (a proibição
    /// de transação some junto), e nada acusaria que a resiliência a failover foi perdida.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_configuracao_de_producao_usa_estrategia_retentante()
    {
        await using var provider = BuildWithRetry();

        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var strategy = db.Database.CreateExecutionStrategy();

        strategy.RetriesOnFailure.ShouldBeTrue(
            "sem retry, todo failover do Postgres gerenciado (que é rotina) vira uma janela " +
            "de 500 para o cliente");

        await Task.CompletedTask;
    }

    /// <summary>
    /// Uma reexecução da rodada <b>não</b> infla o contador de tentativas.
    ///
    /// <para>
    /// É o efeito colateral não óbvio do <c>ChangeTracker</c>: as entidades da tentativa
    /// anterior seguem rastreadas e modificadas, e sem limpá-las o novo <c>Attempts++</c>
    /// somaria por cima do antigo. Uma mensagem chegaria ao teto na metade das tentativas
    /// reais e seria descartada cedo demais — falha rara (só sob failover) e silenciosa (a
    /// mensagem só some).
    /// </para>
    ///
    /// <para>
    /// Aqui a rodada é chamada duas vezes sobre a MESMA instância de <c>DbContext</c>, que é o
    /// que a reexecução da strategy faz. Com o <c>ChangeTracker.Clear()</c> no lugar, a
    /// segunda rodada relê do banco e o contador anda de um em um.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Reexecutar_a_rodada_nao_infla_o_contador_de_tentativas()
    {
        await using var provider = BuildWithRetry();

        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();

        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await ClearAsync(db);

        // Uma envenenada: ela falha, então Attempts sobe a cada rodada e dá para observar.
        await outbox.EnqueueAsync(new PoisonEvent(), TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await processor.ProcessAsync(TestContext.Current.CancellationToken);
        await processor.ProcessAsync(TestContext.Current.CancellationToken);

        var message = await db.OutboxMessages
            .IgnoreQueryFilters()
            .SingleAsync(TestContext.Current.CancellationToken);

        message.Attempts.ShouldBe(
            2,
            "duas rodadas = duas tentativas. Se o ChangeTracker carregar o incremento da " +
            "rodada anterior, a mensagem morre na metade do tempo previsto");
    }

    // --- helpers -------------------------------------------------------------

    private static async Task ClearAsync(AppDbContext db) =>
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE outbox_messages", TestContext.Current.CancellationToken);

    /// <summary>
    /// Monta o <c>DbContext</c> <b>como o PersistenceExtensions monta</b> — com
    /// <c>EnableRetryOnFailure</c>. É a diferença entre esta classe e a <see cref="OutboxTests"/>.
    /// </summary>
    private ServiceProvider BuildWithRetry()
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(TestCrypto.Encryptor);
        services.AddScoped<ITenantResolver, NoTenantResolver>();

        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(fixture.PostgresConnectionString, npgsql => npgsql.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorCodesToAdd: null))
            .AddInterceptors(new AuditingInterceptor(new NoTenantResolver(), TimeProvider.System)));

        services.AddOutbox();

        services.AddEventHandler<TestEvent, TestEventHandler>();
        services.AddEventHandler<PoisonEvent, PoisonEventHandler>();

        return services.BuildServiceProvider();
    }

    private sealed class NoTenantResolver : ITenantResolver
    {
        public Guid? CurrentTenantId => null;
    }
}
