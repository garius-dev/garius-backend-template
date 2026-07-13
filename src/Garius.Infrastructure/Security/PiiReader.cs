using Garius.Core.Authorization;
using Garius.Core.Results;
using Garius.Core.Security;
using Garius.Infrastructure.Database;
using Microsoft.Extensions.Logging;

namespace Garius.Infrastructure.Security;

/// <summary>
/// Implementa o portal único de leitura de PII: autoriza, revela e <b>audita</b>.
/// Ver <see cref="IPiiReader"/>.
/// </summary>
internal sealed class PiiReader(
    ICurrentUser currentUser,
    AppDbContext dbContext,
    ILogger<PiiReader> logger) : IPiiReader
{
    public Task<bool> CanRevealAsync(PiiScope scope, CancellationToken cancellationToken = default) =>
        IsAuthorizedAsync(scope, ownerId: null, cancellationToken);

    public async Task<Result<string>> RevealAsync(
        Pii value,
        PiiScope scope,
        string entityType,
        Guid entityId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (!await IsAuthorizedAsync(scope, entityId, cancellationToken))
        {
            // Tentativa de acesso negada também é um evento de segurança relevante.
            logger.LogWarning(
                "Acesso a PII NEGADO. Usuário={UserId} Escopo={Scope} Entidade={EntityType}/{EntityId} Motivo={Reason}",
                currentUser.UserId, scope, entityType, entityId, reason);

            return Error.Forbidden(
                "pii.forbidden",
                "Você não tem permissão para ver este dado pessoal.");
        }

        if (value.IsEmpty)
        {
            return string.Empty;
        }

        // Registra ANTES de devolver o valor. Se a gravação da auditoria falhar, o acesso não
        // acontece — um acesso não auditado é pior do que um acesso negado.
        await AuditAsync(scope, entityType, entityId, reason, cancellationToken);

        return value.Reveal();
    }

    /// <summary>
    /// Duas formas de estar autorizado:
    ///
    /// <list type="number">
    ///   <item><b>Ser o titular.</b> A LGPD garante ao titular o acesso aos próprios dados
    ///         (Art. 18, II) — sem esta regra, o usuário não veria o próprio e-mail no perfil.</item>
    ///   <item><b>Ter a permissão</b> do escopo (ex.: <c>pii.cpf.read</c> para um gestor de RH).</item>
    /// </list>
    ///
    /// <para>
    /// Sem usuário no contexto (um job, o bootstrap), a leitura é permitida: não há a quem
    /// negar, e esses caminhos não são expostos a terceiros. O acesso continua sendo auditado,
    /// com <c>ActorUserId = null</c>.
    /// </para>
    /// </summary>
    private async Task<bool> IsAuthorizedAsync(
        PiiScope scope,
        Guid? ownerId,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null)
        {
            return true;
        }

        if (ownerId is not null && currentUser.UserId == ownerId)
        {
            return true;
        }

        var permission = Permissions.ForPiiScope(scope);

        return await currentUser.HasPermissionAsync(permission.Value, cancellationToken);
    }

    private async Task AuditAsync(
        PiiScope scope,
        string entityType,
        Guid entityId,
        string reason,
        CancellationToken cancellationToken)
    {
        dbContext.PiiAccessLogs.Add(new PiiAccessLog
        {
            ActorUserId = currentUser.UserId,
            EntityType = entityType,
            EntityId = entityId,
            Scope = scope,
            Reason = reason,
            ClientIp = currentUser.ClientIp,
            TraceId = currentUser.TraceId
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
