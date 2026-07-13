namespace Garius.Core.Security;

/// <summary>
/// Quem está fazendo esta requisição. Abstrai o <c>HttpContext</c> para que o domínio não
/// precise conhecê-lo.
/// </summary>
public interface ICurrentUser
{
    /// <summary>Id do usuário autenticado. <c>null</c> em um job, no bootstrap ou num request anônimo.</summary>
    Guid? UserId { get; }

    /// <summary>IP real do cliente (via <c>CF-Connecting-IP</c> validado).</summary>
    string? ClientIp { get; }

    /// <summary>Correlaciona a ação com os logs no Grafana.</summary>
    string? TraceId { get; }

    /// <summary>
    /// O usuário tem esta permissão? Considera papéis e permissões avulsas, com curinga
    /// (<c>*</c> satisfaz tudo; <c>pii.*</c> satisfaz <c>pii.cpf.read</c>).
    ///
    /// <para>
    /// Assíncrono de propósito: as permissões vêm de um cache que pode precisar consultar o
    /// banco (ou o Redis). Uma versão síncrona exigiria <c>.GetAwaiter().GetResult()</c> aqui
    /// dentro — bloqueio numa thread do pool a cada request, que é como se cria thread
    /// starvation sob carga.
    /// </para>
    /// </summary>
    Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default);
}
