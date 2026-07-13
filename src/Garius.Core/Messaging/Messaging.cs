namespace Garius.Core.Messaging;

/// <summary>
/// Um evento de domínio: <b>algo que aconteceu</b>, no passado, e que já é fato consumado.
///
/// <code>
/// public sealed record UserCreated(Guid UserId, string DisplayName) : IDomainEvent;
/// </code>
///
/// <para>
/// Nomeie no <b>passado</b> (<c>UserCreated</c>, não <c>CreateUser</c>). Não é um comando —
/// ninguém pode recusá-lo, e quem o recebe não decide se ele acontece: ele <b>já</b> aconteceu,
/// e a transação que o gravou já foi commitada.
/// </para>
/// </summary>
public interface IDomainEvent;

/// <summary>
/// Reage a um evento. É isto que uma aplicação derivada implementa.
///
/// <code>
/// public sealed class SendWelcomeEmail(IEmailSender email) : IEventHandler&lt;UserCreated&gt;
/// {
///     public async Task HandleAsync(UserCreated evt, CancellationToken ct) =&gt;
///         await email.SendAsync(evt.UserId, "Bem-vindo!", ct);
/// }
/// </code>
///
/// <para>
/// ⚠️ <b>O handler PRECISA ser idempotente.</b> A entrega é <i>at-least-once</i>: se o processo
/// morrer entre a publicação e a marcação de "publicado", o evento é reentregue — e o handler
/// roda de novo. Um handler que manda e-mail sem checar nada mandará <b>dois e-mails</b>.
/// Não é um defeito do outbox: é a consequência inevitável de não haver transação entre o banco
/// e o mundo externo. Ver <see cref="OutboxMessage"/>.
/// </para>
///
/// <para>
/// Registre-o no DI e o drenador o encontra: <c>services.AddEventHandler&lt;UserCreated,
/// SendWelcomeEmail&gt;()</c>.
/// </para>
/// </summary>
public interface IEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Enfileira um evento — <b>na mesma transação</b> do dado que o originou.
///
/// <code>
/// db.Users.Add(user);
/// await outbox.EnqueueAsync(new UserCreated(user.Id, user.DisplayName), ct);
///
/// await db.SaveChangesAsync(ct);   // ← o dado E o evento, num commit só
/// </code>
///
/// <para>
/// <b>Não chame <c>SaveChangesAsync</c> dentro do <c>EnqueueAsync</c>.</b> Ele apenas
/// <i>adiciona</i> a mensagem ao <c>DbContext</c>; o commit é seu, e é ele que dá a garantia
/// toda: ou o usuário e o evento existem, ou nenhum dos dois existe. Um <c>SaveChanges</c>
/// prematuro publicaria um evento sobre um usuário que a transação ainda pode desfazer — e aí
/// o e-mail de boas-vindas chega para um usuário que nunca existiu.
/// </para>
/// </summary>
public interface IOutbox
{
    Task EnqueueAsync<TEvent>(TEvent domainEvent, CancellationToken cancellationToken = default)
        where TEvent : IDomainEvent;
}
