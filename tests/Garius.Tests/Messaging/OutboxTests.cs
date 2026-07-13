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
/// O outbox transacional, contra Postgres real.
///
/// <para>
/// A garantia que ele existe para dar é uma só, e é a que estes testes provam: <b>o evento e o
/// dado vivem ou morrem juntos</b>. Não há janela em que um exista sem o outro — porque não há
/// dois sistemas no momento da escrita, há uma transação do Postgres.
/// </para>
/// </summary>
[Collection("Outbox")]
public class OutboxTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    /// <summary>
    /// <b>O teste central.</b> Se a transação for desfeita, o evento <b>não existe</b>.
    ///
    /// <para>
    /// É isto que separa o outbox do caminho ingênuo (salvar e depois publicar): ali, um crash
    /// entre as duas coisas deixa um evento publicado sobre um dado que nunca foi commitado — e
    /// o e-mail de boas-vindas chega para um usuário que não existe.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Um_evento_NAO_sobrevive_ao_rollback_da_transacao()
    {
        await using var provider = Build();

        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();

        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await ClearAsync(db);

        await using (var transaction = await db.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken))
        {
            await outbox.EnqueueAsync(
                new TestEvent("valor"), TestContext.Current.CancellationToken);

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            // A transação é DESFEITA — como se a operação tivesse falhado depois de enfileirar.
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        // Nenhum evento. É o Postgres que garante isso, não nós.
        var messages = await db.OutboxMessages
            .IgnoreQueryFilters()
            .ToListAsync(TestContext.Current.CancellationToken);

        messages.ShouldBeEmpty(
            "o evento é gravado na MESMA transação do dado — desfeita ela, ele não existe. " +
            "É toda a razão de o outbox existir.");
    }

    [Fact]
    public async Task Um_evento_enfileirado_e_processado_pelo_handler()
    {
        await using var provider = Build();

        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();

        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await ClearAsync(db);

        TestEventHandler.Handled.Clear();

        await outbox.EnqueueAsync(new TestEvent("olá"), TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await processor.ProcessAsync(TestContext.Current.CancellationToken);

        TestEventHandler.Handled.ShouldContain("olá");

        var message = await db.OutboxMessages
            .IgnoreQueryFilters()
            .SingleAsync(TestContext.Current.CancellationToken);

        message.ProcessedAt.ShouldNotBeNull("uma mensagem publicada precisa ser marcada");
    }

    /// <summary>
    /// A mensagem processada <b>não é apagada</b> — ela é a trilha de que o evento aconteceu.
    /// E não é reprocessada na rodada seguinte.
    /// </summary>
    [Fact]
    public async Task Uma_mensagem_ja_processada_nao_e_reprocessada()
    {
        await using var provider = Build();

        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();

        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await ClearAsync(db);

        TestEventHandler.Handled.Clear();

        await outbox.EnqueueAsync(new TestEvent("uma vez"), TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await processor.ProcessAsync(TestContext.Current.CancellationToken);
        await processor.ProcessAsync(TestContext.Current.CancellationToken);
        await processor.ProcessAsync(TestContext.Current.CancellationToken);

        TestEventHandler.Handled.Count.ShouldBe(
            1,
            "três rodadas do job, uma única entrega — a mensagem processada sai da fila");

        // E continua no banco: é a trilha de auditoria.
        (await db.OutboxMessages.IgnoreQueryFilters().CountAsync(TestContext.Current.CancellationToken))
            .ShouldBe(1, "a mensagem publicada NÃO é apagada — ela prova que o evento aconteceu");
    }

    /// <summary>
    /// Uma mensagem <b>envenenada</b> (o handler sempre estoura) não pode derrubar o lote nem
    /// ser retentada para sempre. Ela para depois de <c>MaxAttempts</c>, e fica visível.
    /// </summary>
    [Fact]
    public async Task Uma_mensagem_envenenada_para_de_ser_tentada_e_nao_derruba_as_outras()
    {
        await using var provider = Build();

        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();

        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await ClearAsync(db);

        TestEventHandler.Handled.Clear();

        // Uma envenenada e uma saudável, no mesmo lote.
        await outbox.EnqueueAsync(new PoisonEvent(), TestContext.Current.CancellationToken);
        await outbox.EnqueueAsync(new TestEvent("saudável"), TestContext.Current.CancellationToken);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Roda o suficiente para esgotar as tentativas da envenenada.
        for (var i = 0; i < OutboxMessage.MaxAttempts + 1; i++)
        {
            await processor.ProcessAsync(TestContext.Current.CancellationToken);
        }

        // A SAUDÁVEL foi entregue — a envenenada não a arrastou junto.
        TestEventHandler.Handled.ShouldContain(
            "saudável",
            "uma mensagem envenenada não pode impedir a publicação das mensagens boas atrás dela");

        var poison = await db.OutboxMessages
            .IgnoreQueryFilters()
            .SingleAsync(m => m.Type == nameof(PoisonEvent), TestContext.Current.CancellationToken);

        poison.ProcessedAt.ShouldBeNull();
        poison.Attempts.ShouldBe(OutboxMessage.MaxAttempts, "ela para no teto, não tenta para sempre");
        poison.LastError.ShouldNotBeNullOrEmpty("o erro fica gravado — é onde se vai olhar");
        poison.IsDead.ShouldBeTrue();
    }

    /// <summary>
    /// Um evento <b>sem handler</b> não é um erro. Ele é marcado como processado, e não fica
    /// sendo retentado até morrer — o que encheria o log de uma "falha" que não é falha.
    /// </summary>
    [Fact]
    public async Task Um_evento_sem_handler_nao_e_tratado_como_falha()
    {
        await using var provider = Build();

        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();

        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await ClearAsync(db);

        await outbox.EnqueueAsync(new OrphanEvent(), TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await processor.ProcessAsync(TestContext.Current.CancellationToken);

        var message = await db.OutboxMessages
            .IgnoreQueryFilters()
            .SingleAsync(TestContext.Current.CancellationToken);

        message.ProcessedAt.ShouldNotBeNull(
            "ninguém se importa com este evento — e tudo bem. Retentá-lo até morrer só encheria " +
            "o log de uma falha que não é falha.");

        message.LastError.ShouldBeNull();
    }

    // --- helpers -------------------------------------------------------------

    private static async Task ClearAsync(AppDbContext db) =>
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE outbox_messages", TestContext.Current.CancellationToken);

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(TestCrypto.Encryptor);
        services.AddScoped<ITenantResolver, NoTenant>();

        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(fixture.PostgresConnectionString)
            .AddInterceptors(new AuditingInterceptor(new NoTenant(), TimeProvider.System)));

        services.AddOutbox();

        services.AddEventHandler<TestEvent, TestEventHandler>();
        services.AddEventHandler<PoisonEvent, PoisonEventHandler>();

        // OrphanEvent NÃO tem handler — de propósito.

        return services.BuildServiceProvider();
    }

    private sealed class NoTenant : ITenantResolver
    {
        public Guid? CurrentTenantId => null;
    }
}

public sealed record TestEvent(string Value) : IDomainEvent;

/// <summary>Um evento cujo handler sempre estoura — a "mensagem envenenada".</summary>
public sealed record PoisonEvent : IDomainEvent;

/// <summary>Um evento que ninguém consome. Não é erro.</summary>
public sealed record OrphanEvent : IDomainEvent;

public sealed class TestEventHandler : IEventHandler<TestEvent>
{
    /// <summary>O que foi entregue. Estático: o handler é instanciado pelo DI a cada evento.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentBag<string> Handled = [];

    public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        Handled.Add(domainEvent.Value);

        return Task.CompletedTask;
    }
}

public sealed class PoisonEventHandler : IEventHandler<PoisonEvent>
{
    public Task HandleAsync(PoisonEvent domainEvent, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Este handler sempre falha, de propósito.");
}
