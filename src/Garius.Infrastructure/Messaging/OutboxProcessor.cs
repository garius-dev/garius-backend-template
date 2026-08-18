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
    OutboxOptions options,
    ILogger<OutboxProcessor> logger)
{
    /// <summary>
    /// Quantas mensagens por rodada. Um lote grande seguraria a transação (e os locks) por
    /// muito tempo; um lote pequeno faria o job nunca alcançar a fila sob carga.
    ///
    /// <para>
    /// Configurável porque define um <b>teto de throughput</b>: com o job de minuto em minuto,
    /// são <c>BatchSize × 60</c> mensagens por hora, no máximo. Ver <see cref="OutboxOptions"/>.
    /// </para>
    /// </summary>
    private int BatchSize => options.BatchSize;

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
        // ⚠️ A EXECUTION STRATEGY NÃO É OPCIONAL AQUI — e a razão é sutil.
        //
        // O DbContext liga EnableRetryOnFailure (resiliência a failover de banco, ver
        // PersistenceExtensions). Com o retry ligado, o EF PROÍBE abrir transação à mão: ele
        // não tem como reexecutar um bloco que começou fora do controle dele, e recusa com
        // "The configured execution strategy 'NpgsqlRetryingExecutionStrategy' does not
        // support user-initiated transactions".
        //
        // Pedir a strategy e rodar a transação DENTRO dela devolve esse controle: se a conexão
        // cair no meio, o EF reexecuta o delegate INTEIRO — nova transação, novo SELECT ... FOR
        // UPDATE, novo lote.
        //
        // Reexecutar é seguro porque o corpo relê tudo do banco: o lote não é estado carregado
        // de fora, ele vem do próprio SELECT. E como o Attempts++ só vale se a transação
        // commitar, uma tentativa que falhou no meio não deixa contador inflado para trás.
        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(() => ProcessBatchAsync(cancellationToken));
    }

    /// <summary>
    /// Uma tentativa de rodada. Pode ser <b>reexecutado</b> pela execution strategy — ver
    /// <see cref="ProcessAsync"/>.
    /// </summary>
    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        // ⚠️ LIMPAR O CHANGE TRACKER É O QUE TORNA A REEXECUÇÃO CORRETA.
        //
        // Se a tentativa anterior falhou DEPOIS de incrementar Attempts (mas antes de
        // commitar), aquelas entidades continuam no tracker, modificadas. Sem esta linha, a
        // nova tentativa carregaria o incremento antigo e somaria o novo por cima: uma
        // mensagem contaria DUAS tentativas por uma falha de infraestrutura que não é culpa
        // dela — e chegaria a MaxAttempts na metade do tempo, sendo descartada cedo demais.
        //
        // A falha seria rara (só sob failover) e silenciosa (a mensagem só some), que é a
        // combinação mais difícil de diagnosticar depois.
        db.ChangeTracker.Clear();

        // Transação explícita: os locks do FOR UPDATE só valem dentro dela.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // ⚠️ FromSqlRaw IGNORA os global query filters do EF — inclusive o de soft delete.
        // Por isso o `Enabled` entra explicitamente no WHERE. Esquecê-lo faria o drenador
        // processar mensagens logicamente apagadas.
        //
        // FromSql (interpolado), e não FromSqlRaw: o EF transforma cada {valor} em PARÂMETRO
        // do comando, não em concatenação de string. Com o BatchSize vindo de configuração,
        // o FromSqlRaw seria injeção de SQL por construção — e o analisador do EF (EF1002)
        // recusa o build, com razão.
        var pending = await db.OutboxMessages
            .FromSql(
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
