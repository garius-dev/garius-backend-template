using Garius.Core.Security;
using Microsoft.AspNetCore.Identity;

namespace Garius.Infrastructure.Identity;

/// <summary>
/// <b>A peça que faz o Identity funcionar com o e-mail criptografado.</b>
///
/// <para>
/// O <c>UserStore</c> do Identity busca o usuário assim:
/// </para>
/// <code>
/// // FindByEmailAsync(email):
/// var normalized = _lookupNormalizer.NormalizeEmail(email);
/// Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized);
/// </code>
///
/// <para>
/// O normalizador padrão devolve o e-mail em maiúsculas — o que gravaria o e-mail
/// <b>em claro</b> na coluna <c>NormalizedEmail</c>, anulando toda a criptografia.
/// </para>
///
/// <para>
/// Este devolve o <b>índice cego</b> (HMAC-SHA256 em base64). Com isso:
/// </para>
/// <list type="bullet">
///   <item><c>NormalizedEmail</c> guarda o HMAC, nunca o e-mail;</item>
///   <item><c>FindByEmailAsync</c> continua funcionando <b>nativamente</b> — o Identity compara
///         o HMAC do que foi digitado com o HMAC gravado, sem saber que é um HMAC;</item>
///   <item>não é preciso sobrescrever o <c>UserStore</c> nem reimplementar o login.</item>
/// </list>
///
/// <para>
/// A normalização (minúsculas, trim) já acontece dentro do <see cref="IBlindIndex"/>, então
/// <c>"JOAO@X.COM"</c> e <c>" joao@x.com "</c> produzem o mesmo valor — e o login é
/// case-insensitive de graça.
/// </para>
/// </summary>
internal sealed class BlindIndexLookupNormalizer(IBlindIndex blindIndex) : ILookupNormalizer
{
    public string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email)
            ? null
            : Convert.ToBase64String(blindIndex.Compute(PiiScope.Email, email));

    /// <summary>
    /// O <c>UserName</c> não é PII neste template (é o Id do usuário em texto — um
    /// identificador opaco). A normalização padrão, em maiúsculas, basta.
    /// </summary>
    public string? NormalizeName(string? name) => name?.Trim().ToUpperInvariant();
}
