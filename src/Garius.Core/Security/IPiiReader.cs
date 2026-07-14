using Garius.Core.Results;

namespace Garius.Core.Security;

/// <summary>
/// O <b>único</b> caminho para ler um dado pessoal em claro quando há um usuário por trás
/// da leitura.
///
/// <para>
/// Ele acopla as três coisas que a LGPD exige e que, separadas, alguém esquece:
/// </para>
/// <list type="number">
///   <item><b>autorização</b> — quem pede tem permissão para esse <see cref="PiiScope"/>?</item>
///   <item><b>propósito</b> — o <c>reason</c> é obrigatório e vai para o registro;</item>
///   <item><b>auditoria</b> — a leitura é gravada em <see cref="PiiAccessLog"/>.</item>
/// </list>
///
/// <para>
/// Chamar <c>Pii.Reveal()</c> diretamente pula os três. Faça isso apenas onde não há um
/// usuário na jogada (o próprio processo de login, que compara um hash; um job de
/// exportação com auditoria própria) — e deixe claro no código por quê.
/// </para>
///
/// <code>
/// // EmailPii (o Pii cifrado), não user.Email — este é `string`, existe só em memória
/// // (o Identity precisa dele como entrada) e NÃO é o dado que está no banco.
/// var email = await piiReader.RevealAsync(
///     user.EmailPii, PiiScope.Email, nameof(ApplicationUser), user.Id,
///     reason: "Exibição no perfil do próprio titular", ct);
///
/// if (email.IsSuccess) { /* email.Value é o e-mail em claro, e o acesso foi registrado */ }
/// </code>
/// </summary>
public interface IPiiReader
{
    /// <summary>
    /// Revela o valor, se o usuário atual tiver permissão — e registra o acesso.
    /// Devolve <see cref="ErrorType.Forbidden"/> se não tiver.
    ///
    /// <para>
    /// O <c>reason</c> é <b>obrigatório</b>: é o que, numa auditoria, distingue uma exportação
    /// legítima de um vazamento. Um log que diz "alguém leu 400 CPFs" sem o motivo não
    /// responde nada.
    /// </para>
    /// </summary>
    Task<Result<string>> RevealAsync(
        Pii value,
        PiiScope scope,
        string entityType,
        Guid entityId,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// O usuário atual pode ler esta categoria de dado? Útil para decidir, na montagem de um
    /// DTO, entre o valor em claro e a versão mascarada — sem gerar um erro.
    /// </summary>
    Task<bool> CanRevealAsync(PiiScope scope, CancellationToken cancellationToken = default);
}
