namespace Garius.Infrastructure.Machine;

/// <summary>
/// Seção <c>Jwt</c>. Governa apenas os tokens de <b>máquina</b> — usuários usam cookie, não JWT.
///
/// <code>
/// // no secret da aplicação:
/// {
///   "Jwt:SigningKey": "&lt;32+ bytes em base64&gt;"
/// }
/// // no appsettings (não são segredo):
/// {
///   "Jwt": { "Issuer": "https://api.dominio.com", "Audience": "https://api.dominio.com" }
/// }
/// </code>
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// Chave de assinatura HMAC-SHA256 (base64, <b>no mínimo</b> 32 bytes). Vem do Secret
    /// Manager — nunca do appsettings.
    ///
    /// <para>
    /// <b>Simétrica (HS256), não RSA.</b> Quem assina e quem valida é a <b>mesma</b> aplicação:
    /// não há terceiro que precise verificar o token sem poder emiti-lo, que é o único motivo
    /// real para um par de chaves. RSA aqui só acrescentaria um endpoint JWKS e a rotação de
    /// um par de chaves, para resolver um problema que não temos.
    /// </para>
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Quem emitiu. Validado na entrada — um token de outra aplicação não serve aqui.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Para quem vale. Validado na entrada.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Vida do token, em minutos. Curta de propósito.
    ///
    /// <para>
    /// O JWT é <b>stateless</b>: uma vez emitido, vale até expirar — revogar um client
    /// <b>não</b> mata os tokens que ele já tem em mãos, porque nada é consultado a cada
    /// request (é justamente o que o torna barato). Esta janela é, portanto, o tempo máximo em
    /// que um client revogado continua funcionando. Uma hora é o equilíbrio usual; encurtá-la
    /// aumenta o tráfego no <c>/auth/token</c>, alongá-la aumenta o estrago de um vazamento.
    /// </para>
    /// </summary>
    public int LifetimeMinutes { get; set; } = 60;
}
