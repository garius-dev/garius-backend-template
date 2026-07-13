using System.Text.Json;
using Garius.Core.Messaging;
using Garius.Infrastructure.Database;

namespace Garius.Infrastructure.Messaging;

/// <summary>
/// Enfileira o evento <b>no <c>DbContext</c></b> — e para por aí. O commit é de quem chamou.
/// Ver <see cref="IOutbox"/> para o porquê de isso ser essencial, e não um detalhe.
/// </summary>
internal sealed class Outbox(AppDbContext db) : IOutbox
{
    public Task EnqueueAsync<TEvent>(
        TEvent domainEvent,
        CancellationToken cancellationToken = default)
        where TEvent : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        db.OutboxMessages.Add(new OutboxMessage
        {
            // O nome do TIPO, não o Type: um assembly renomeado (ou um namespace movido) não
            // pode invalidar eventos já gravados no banco. É o mesmo nome que o
            // OutboxProcessor usa para achar o handler.
            Type = typeof(TEvent).Name,
            Payload = JsonSerializer.Serialize(domainEvent)
        });

        // NENHUM SaveChangesAsync aqui. É deliberado — ver IOutbox.
        return Task.CompletedTask;
    }
}
