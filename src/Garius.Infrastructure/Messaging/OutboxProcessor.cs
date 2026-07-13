using System.Text.Json;
using Garius.Core.Messaging;
using Garius.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Garius.Infrastructure.Messaging;

/// <summary>
/// Drena o outbox: pega as mensagens pendentes, chama os handlers, marca como publicadas.
/// Roda como job recorrente do Hangfire.
/// </summary>
public sealed class OutboxProcessor(
    AppDbContext db,
    IServiceProvider services,
    EventTypeRegistry registry,
    TimeProvider timeProvider,
    ILogger<OutboxProcessor> logger)
{
    /// <summary>
    /// Quantas mensagens por rodada. Um lote grande seguraria a transação (e os locks) por
    /// muito tempo; um lote pequeno faria o job nunca alcançar a fila sob carga.
    /// </summary>
    private const int BatchSize = 100;

    /// <summary>
    /// Uma rodada.
    ///
    /// <para>
    /// <b>O <c>FOR UPDATE SKIP LOCKED</c> é o que faz isto funcionar com N réplicas.</b> Sem
    /// ele, duas réplicas rodando o job ao mesmo tempo leriam as <b>mesmas</b> mensagens
    /// pendentes e as processariam <b>as duas</b> — cada evento entregue em dobro, e a
    /// idempotência do handler virando a única coisa entre o usuário e dois e-mails.
    /// O <c>SKIP LOCKED</c> faz a segunda réplica simplesmente <b>pular</b> as linhas que a
    /// primeira travou, e pegar as seguintes: as duas trabalham em paralelo, sem sobreposição
    /// e sem uma esperar pela outra.
    /// </para>
    /// </summary>
    public async Task ProcessAsync(CancellationToken cancellationToken = default)
    {
        // Transação explícita: os locks do FOR UPDATE só valem dentro dela.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // ⚠️ FromSqlRaw IGNORA os global query filters do EF — inclusive o de soft delete.
        // Por isso o `Enabled` entra explicitamente no WHERE. Esquecê-lo faria o drenador
        // processar mensagens logicamente apagadas.
        var pending = await db.OutboxMessages
            .FromSqlRaw(
                $"""
                 SELECT * FROM outbox_messages
                 WHERE "ProcessedAt" IS NULL
                   AND "Enabled" = true
                   AND "Attempts" < {OutboxMessage.MaxAttempts}
                 ORDER BY "CreatedAt"
                 LIMIT {BatchSize}
                 FOR UPDATE SKIP LOCKED
                 """)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return;
        }

        foreach (var message in pending)
        {
            await ProcessOneAsync(message, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Outbox: {Count} mensagem(ns) processada(s).", pending.Count);
    }

    private async Task ProcessOneAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        message.Attempts++;

        try
        {
            var eventType = registry.Resolve(message.Type);

            if (eventType is null)
            {
                // Nenhum handler registrado para este tipo. NÃO é erro: um evento pode ser
                // publicado sem que ninguém (ainda) se importe com ele. Marcar como processado
                // evita que ele fique sendo retentado para sempre até morrer por MaxAttempts —
                // uma "falha" que não é falha nenhuma, e que encheria o log de ruído.
                message.ProcessedAt = timeProvider.GetUtcNow();

                logger.LogDebug(
                    "Outbox: nenhum handler para {Type} — mensagem descartada como processada.",
                    message.Type);

                return;
            }

            var domainEvent = JsonSerializer.Deserialize(message.Payload, eventType);

            if (domainEvent is null)
            {
                throw new InvalidOperationException(
                    $"O payload da mensagem {message.Id} não desserializa para {eventType.Name}.");
            }

            await InvokeHandlersAsync(eventType, domainEvent, cancellationToken);

            message.ProcessedAt = timeProvider.GetUtcNow();
            message.LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A exceção NÃO sobe: uma mensagem envenenada não pode derrubar o lote inteiro e
            // impedir as outras (saudáveis) de serem publicadas. O erro fica gravado na
            // própria linha, e é ali que se vai olhar.
            message.LastError = ex.Message;

            var level = message.Attempts >= OutboxMessage.MaxAttempts
                ? LogLevel.Error      // morreu de vez: alguém precisa ver
                : LogLevel.Warning;   // ainda vai tentar de novo

            logger.Log(
                level,
                ex,
                "Outbox: falha ao processar {Type} (mensagem {MessageId}, tentativa {Attempts}/{Max}).",
                message.Type,
                message.Id,
                message.Attempts,
                OutboxMessage.MaxAttempts);
        }
    }

    /// <summary>
    /// Chama <b>todos</b> os handlers registrados para o evento.
    ///
    /// <para>
    /// A resolução é por reflexão (<c>IEventHandler&lt;T&gt;</c>) porque o tipo só é conhecido
    /// em runtime — ele veio como uma <b>string</b> do banco. É o preço de ter um outbox
    /// genérico, e ele é pago uma vez por mensagem, num job de background.
    /// </para>
    /// </summary>
    private async Task InvokeHandlersAsync(
        Type eventType,
        object domainEvent,
        CancellationToken cancellationToken)
    {
        var handlerType = typeof(IEventHandler<>).MakeGenericType(eventType);

        var handlers = services.GetServices(handlerType).ToList();

        foreach (var handler in handlers)
        {
            var method = handlerType.GetMethod(nameof(IEventHandler<IDomainEvent>.HandleAsync))!;

            var task = (Task)method.Invoke(handler, [domainEvent, cancellationToken])!;

            await task;
        }
    }
}
