namespace Garius.Core.Identity;

/// <param name="Token">O token opaco (vai no cookie). Nunca é gravado — só o seu hash.</param>
/// <param name="ExpiresAt">Quando expira.</param>
public sealed record RefreshToken(string Token, DateTimeOffset ExpiresAt);

/// <param name="UserId">Dono do token.</param>
/// <param name="TenantId">Tenant selecionado na sessão.</param>
/// <param name="FamilyId">
/// A <b>sessão</b>. Preservado através de todas as rotações — ver <see cref="IRefreshTokenStore"/>.
/// </param>
public sealed record RefreshTokenData(Guid UserId, Guid? TenantId, Guid FamilyId);

/// <summary>
/// Armazena refresh tokens no Redis (efêmeros, com TTL nativo — não poluem o banco).
///
/// <para>
/// <b>Rotação com detecção de reuso.</b> Cada uso de um refresh token o consome e emite um
/// novo. Se um token <b>já consumido</b> reaparecer, só há duas explicações: ou ele foi
/// roubado, ou o legítimo perdeu a resposta. Em ambos os casos a resposta correta é a mesma:
/// <b>revogar a família inteira</b> e forçar novo login. O atacante perde o acesso, e o
/// usuário legítimo percebe (tem que logar de novo).
/// </para>
///
/// <para>
/// <b>Duas armadilhas que o template anterior tinha</b> (e que tornavam a detecção inútil):
/// </para>
/// <list type="number">
///   <item><b>Rotação não-atômica.</b> Duas requisições concorrentes com o mesmo token liam
///         "ainda não usado" e ambas emitiam um token novo — exatamente o replay que o código
///         dizia detectar. Aqui a operação é um <b>script Lua</b>, atômico por definição.</item>
///   <item><b>Família nova a cada rotação.</b> Como o <c>FamilyId</c> era regerado, cada
///         rotação criava uma "família" de um token só — e revogar a família não revogava
///         nada. Aqui o <c>FamilyId</c> identifica a <b>sessão</b> e atravessa todas as
///         rotações.</item>
/// </list>
/// </summary>
public interface IRefreshTokenStore
{
    /// <summary>Emite o primeiro token de uma sessão nova (o login).</summary>
    Task<RefreshToken> IssueAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Consome o token e emite o próximo <b>atomicamente</b>, preservando a família.
    ///
    /// <para>
    /// Devolve <c>null</c> em três casos, todos tratados como "faça login de novo": token
    /// inexistente, token expirado, ou <b>reuso detectado</b> — e neste último a família
    /// inteira é revogada antes de retornar.
    /// </para>
    /// </summary>
    Task<(RefreshToken Token, RefreshTokenData Data)?> RotateAsync(
        string token,
        CancellationToken cancellationToken = default);

    /// <summary>Revoga a sessão (logout). Invalida a família inteira.</summary>
    Task RevokeAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revoga <b>todas</b> as sessões do usuário. Para "sair de todos os dispositivos", e
    /// para quando a senha é trocada.
    /// </summary>
    Task RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
