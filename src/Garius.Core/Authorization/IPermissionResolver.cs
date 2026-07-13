namespace Garius.Core.Authorization;

/// <summary>
/// Resolve as <b>permissões efetivas</b> de um usuário: as dos seus papéis, mais as
/// concedidas diretamente a ele, achatadas num único conjunto.
/// </summary>
public interface IPermissionResolver
{
    /// <summary>
    /// As permissões do usuário no tenant informado.
    ///
    /// <para>
    /// O resultado fica <b>em cache</b>: sem isso, cada request faria um JOIN entre usuário,
    /// papéis e claims antes de chegar à lógica de negócio. O cache é invalidado por evento
    /// (ao mudar um papel ou uma concessão), não por TTL curto — um usuário que perdeu o
    /// acesso precisa perdê-lo <b>agora</b>, não em cinco minutos.
    /// </para>
    /// </summary>
    Task<IReadOnlySet<string>> GetPermissionsAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Descarta o cache de um usuário. Chamar sempre que os papéis ou as permissões dele
    /// mudarem — do contrário a revogação não tem efeito imediato.
    /// </summary>
    Task InvalidateAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Descarta o cache de <b>todos</b> os usuários. Necessário quando muda um <b>papel</b>:
    /// alterar as permissões de "Financeiro" afeta todo mundo que o tem, e o cache é por
    /// usuário — não há como saber quem é afetado sem varrer o banco.
    /// </summary>
    Task InvalidateAllAsync(CancellationToken cancellationToken = default);
}
