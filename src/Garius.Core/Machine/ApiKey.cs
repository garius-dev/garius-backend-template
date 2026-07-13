using Garius.Core.Entities;
using Garius.Core.Tenancy;

namespace Garius.Core.Machine;

/// <summary>
/// Uma <b>chave de API</b> para um terceiro: uma credencial única, de longa duração, enviada
/// direto no header — sem troca prévia por token.
///
/// <para>
/// <b>Por que existe, se já há OAuth client credentials.</b> São públicos diferentes. O
/// client credentials é um fluxo de duas etapas (pega token, usa token) que um integrador
/// precisa implementar; a API key é uma linha de <c>curl</c>. Para um parceiro que só chama
/// dois endpoints, exigir o fluxo OAuth é atrito sem ganho de segurança — e o resultado
/// costuma ser o parceiro guardando o token de 1h em disco e reusando-o depois de expirado.
/// </para>
///
/// <para>
/// <b>O trade-off, dito na cara.</b> A API key é de longa duração: se vazar, vale até alguém
/// perceber e revogar. É por isso que ela tem <see cref="CallLimit"/> e
/// <see cref="LastUsedAt"/> e o client OAuth não precisa — o JWT dele morre em uma hora
/// sozinho, mas a chave depende de <b>vigilância</b>. O limite de chamadas é o que transforma
/// um vazamento silencioso e ilimitado num vazamento que trava e aparece.
/// </para>
/// </summary>
public sealed class ApiKey : BaseEntity, ITenantEntity
{
    /// <summary>
    /// Os primeiros caracteres da chave, <b>em claro</b> (ex.: <c>gk_live_a1b2c3d4</c>).
    ///
    /// <para>
    /// Existe por duas razões práticas, e ambas são a diferença entre uma chave administrável
    /// e uma inutilizável:
    /// </para>
    /// <list type="number">
    ///   <item><b>Exibição.</b> A tela de administração mostra <c>gk_live_a1b2c3d4…</c> — sem
    ///         isso, a lista de chaves seria uma coluna de hashes indistinguíveis, e ninguém
    ///         saberia qual revogar.</item>
    ///   <item><b>Busca.</b> Na autenticação, o prefixo é <b>indexado</b>: acha-se a linha
    ///         candidata por ele e só então se compara o hash. Sem o prefixo, cada request
    ///         teria de varrer a tabela inteira comparando hashes — O(n) por chamada.</item>
    /// </list>
    ///
    /// <para>
    /// É seguro publicá-lo: são 8 caracteres de um segredo de 256 bits. O resto continua
    /// existindo apenas como hash.
    /// </para>
    /// </summary>
    public required string KeyPrefix { get; set; }

    /// <summary>
    /// Hash SHA-256 da chave <b>inteira</b>. Como no <see cref="OAuthClient.SecretHash"/>, a
    /// chave em claro só existe na resposta que a cria.
    /// </summary>
    public required string KeyHash { get; set; }

    /// <summary>De quem é esta chave / para que serve. Aparece na administração.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// As permissões desta chave — o <b>mesmo catálogo</b> dos usuários e dos clients OAuth.
    /// Uma chave de terceiro quase sempre deve ser somente-leitura de um recurso específico.
    /// </summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>O tenant em nome do qual a chave opera. Ver <see cref="OAuthClient.TenantId"/>.</summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Teto de chamadas que esta chave pode fazer. <c>null</c> = sem teto.
    ///
    /// <para>
    /// <b>É um total acumulado, não uma taxa.</b> Não confundir com rate limit (que é
    /// requisições por janela de tempo, protege a infraestrutura, e chega na Fase 5). Este
    /// limite é de <i>quota</i>: existe para que uma chave de terceiro não possa ser usada
    /// indefinidamente sem que ninguém repare. Uma chave estourada para de funcionar e alguém
    /// precisa olhar para ela.
    /// </para>
    /// </summary>
    public long? CallLimit { get; set; }

    /// <summary>Quantas chamadas já foram feitas. Comparado com <see cref="CallLimit"/>.</summary>
    public long CallCount { get; set; }

    /// <summary>Quando a chave deixa de valer. <c>null</c> = não expira.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Último uso. Numa credencial de longa duração, é o principal sinal de que ela vazou
    /// (uso vindo de onde não deveria) ou de que ninguém mais a usa (e portanto deveria ser
    /// revogada).
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>A quota acabou?</summary>
    public bool IsExhausted => CallLimit is not null && CallCount >= CallLimit;

    /// <summary>A chave passou da validade?</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is not null && now >= ExpiresAt;
}
