using Garius.Core.Messaging;
using Garius.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Garius.Infrastructure.Messaging;

/// <summary>
/// A fila do outbox está andando?
///
/// <para>
/// <b>Por que isto existe.</b> O <see cref="OutboxProcessor"/> engole as exceções de propósito
/// — uma mensagem envenenada não pode derrubar o lote inteiro. Mas isso tem um preço: quando
/// uma mensagem atinge <see cref="OutboxMessage.MaxAttempts"/>, ela <b>sai do WHERE</b> da
/// query do drenador e some. Fica um <c>Error</c> no Loki e nada mais. Ninguém é notificado; o
/// evento simplesmente nunca aconteceu, e o sistema segue como se estivesse tudo bem.
/// </para>
///
/// <para>
/// O mesmo vale para a fila parada por outro motivo (o job do Hangfire morreu, o banco travou):
/// não há erro nenhum, só ausência de progresso — que é o tipo de falha que ninguém procura
/// até um cliente reclamar.
/// </para>
///
/// <para>
/// <b>A métrica que importa é a IDADE da mensagem pendente mais antiga</b>, não a contagem.
/// Uma fila com 10 mil mensagens que drena em segundos está saudável; uma com 3 mensagens
/// paradas há uma hora está quebrada. A idade é o indicador antecedente — ela cresce antes de
/// qualquer sintoma visível ao usuário.
/// </para>
///
/// <para>
/// <b>Este check NÃO leva a tag de readiness</b>, e isso é deliberado: um outbox atrasado não
/// impede a aplicação de servir HTTP. Tirá-la do balanceamento por causa disso transformaria um
/// atraso de background numa indisponibilidade — e, pior, tiraria <i>todas</i> as réplicas ao
/// mesmo tempo, já que elas compartilham a mesma fila. Ele aparece no <c>/health/detail</c>,
/// que é de onde o alerta deve sair.
/// </para>
/// </summary>
public sealed class OutboxHealthCheck(AppDbContext db, OutboxOptions options, TimeProvider time)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters: o filtro global de soft delete não se aplica aqui — uma mensagem
        // logicamente apagada não deve contar como fila travada, mas uma MORTA precisa aparecer
        // mesmo que alguém a tenha desabilitado.
        var dead = await db.OutboxMessages
            .IgnoreQueryFilters()
            .CountAsync(
                m => m.ProcessedAt == null && m.Attempts >= OutboxMessage.MaxAttempts,
                cancellationToken);

        var oldestPending = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null && m.Attempts < OutboxMessage.MaxAttempts)
            .OrderBy(m => m.CreatedAt)
            .Select(m => (DateTimeOffset?)m.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var age = oldestPending is null
            ? TimeSpan.Zero
            : time.GetUtcNow() - oldestPending.Value;

        // Os dados vão no payload em qualquer caso: é o que um dashboard consome, e é o que se
        // olha ao investigar. Sem eles, o check diria "degradado" sem dizer o quanto.
        var data = new Dictionary<string, object>
        {
            ["deadMessages"] = dead,
            ["oldestPendingAgeSeconds"] = (int)age.TotalSeconds
        };

        if (dead > 0)
        {
            // Mensagem morta é perda de evento: alguém PRECISA olhar. Não é "degradado",
            // é falha — o dado e o evento deixaram de viver juntos, que é a única garantia
            // que o outbox existe para dar.
            return HealthCheckResult.Unhealthy(
                $"{dead} mensagem(ns) do outbox esgotaram as tentativas e não serão publicadas.",
                data: data);
        }

        if (age > TimeSpan.FromMinutes(options.StaleAfterMinutes))
        {
            return HealthCheckResult.Degraded(
                $"A mensagem mais antiga do outbox está pendente há {(int)age.TotalMinutes} minuto(s).",
                data: data);
        }

        return HealthCheckResult.Healthy("O outbox está drenando.", data);
    }
}

/// <summary>Seção <c>Outbox</c>.</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>
    /// Quantas mensagens por rodada.
    ///
    /// <para>
    /// <b>Isto define um TETO de throughput</b>, e vale fazer a conta antes de deixar no
    /// default: com o job rodando a cada minuto, <c>BatchSize × 60</c> é o máximo de mensagens
    /// por hora. No default, 6.000/h. Acima disso a fila cresce sem limite — e o sintoma é a
    /// idade da mensagem mais antiga subindo, que é o que o <see cref="OutboxHealthCheck"/>
    /// observa.
    /// </para>
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// A partir de quantos minutos uma mensagem pendente indica fila travada.
    ///
    /// <para>
    /// O drenador roda a cada minuto, então alguns minutos de atraso são normais sob carga.
    /// 15 é folgado o suficiente para não gritar por nada e curto o suficiente para avisar
    /// antes de o cliente perceber.
    /// </para>
    /// </summary>
    public int StaleAfterMinutes { get; set; } = 15;
}
