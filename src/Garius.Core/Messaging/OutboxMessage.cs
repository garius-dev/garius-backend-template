using Garius.Core.Entities;

namespace Garius.Core.Messaging;

/// <summary>
/// Um evento de domínio à espera de ser publicado.
///
/// <para>
/// <b>O problema que o outbox resolve.</b> Você cria um usuário e precisa mandar um e-mail de
/// boas-vindas. O caminho ingênuo é: salva no banco, depois chama o serviço de e-mail. Entre
/// as duas coisas há uma janela — e nela cabe um crash, um deploy, um timeout de rede. O
/// resultado é <b>um usuário sem e-mail</b> (o processo morreu antes de mandar) ou, se você
/// inverter a ordem, <b>um e-mail sem usuário</b> (o commit falhou depois do envio). Não há
/// ordem que resolva: são dois sistemas, e não existe transação entre eles.
/// </para>
///
/// <para>
/// <b>A saída</b> é não ter dois sistemas no momento da escrita. O evento é gravado numa tabela
/// do <b>mesmo banco</b>, na <b>mesma transação</b> do dado: ou os dois existem, ou nenhum
/// existe — o Postgres garante isso, não nós. Um job separado drena a tabela depois e faz a
/// publicação de verdade.
/// </para>
///
/// <para>
/// <b>A garantia é <i>at-least-once</i>, não <i>exactly-once</i>.</b> Se o processo morrer
/// entre publicar e marcar como publicado, o evento é reentregue. <b>O handler precisa ser
/// idempotente</b> — não há como fugir disso, e prometer o contrário seria mentira: um
/// "exactly-once" de verdade exigiria uma transação distribuída entre o banco e o destino, que
/// é justamente o que não existe.
/// </para>
/// </summary>
public sealed class OutboxMessage : BaseEntity
{
    /// <summary>
    /// O tipo do evento (<c>UserCreated</c>). É por ele que o drenador acha o handler.
    /// Guardamos o nome, não o <see cref="Type"/>: um assembly renomeado não pode invalidar
    /// eventos já gravados.
    /// </summary>
    public required string Type { get; set; }

    /// <summary>O evento serializado em JSON.</summary>
    public required string Payload { get; set; }

    /// <summary>
    /// Quando foi publicado com sucesso. <c>null</c> = ainda na fila.
    ///
    /// <para>
    /// É uma coluna, e não um <c>DELETE</c>, de propósito: a mensagem publicada continua
    /// existindo, e é a <b>trilha</b> de que o evento aconteceu. Apagá-la tornaria impossível
    /// responder "este e-mail chegou a ser enviado?" depois de um incidente.
    /// </para>
    /// </summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Quantas vezes já se tentou publicar. Cresce a cada falha.</summary>
    public int Attempts { get; set; }

    /// <summary>O erro da última tentativa. É o que se lê ao investigar uma mensagem travada.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Depois de tantas falhas, a mensagem para de ser tentada.
    ///
    /// <para>
    /// Sem um teto, uma mensagem <b>envenenada</b> (um payload que sempre estoura no handler)
    /// seria retentada para sempre, a cada rodada do job — enchendo o log de erro e atrasando
    /// as mensagens saudáveis atrás dela. Com o teto, ela para, fica visível como falha, e
    /// alguém decide o que fazer.
    /// </para>
    /// </summary>
    public const int MaxAttempts = 5;

    /// <summary>A mensagem esgotou as tentativas e não será mais processada.</summary>
    public bool IsDead => ProcessedAt is null && Attempts >= MaxAttempts;
}
