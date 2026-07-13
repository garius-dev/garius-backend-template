namespace Garius.Infrastructure.Security;

/// <summary>
/// Seção <c>Encryption</c>. As chaves vêm do Secret Manager, nunca do appsettings.
///
/// <code>
/// // no secret da aplicação (JSON flat):
/// {
///   "Encryption:Keys:1":     "&lt;32 bytes em base64&gt;",   // chave versão 1
///   "Encryption:Keys:2":     "&lt;32 bytes em base64&gt;",   // versão 2 (após rotação)
///   "Encryption:ActiveKeyVersion": "2",
///   "Encryption:BlindIndexKey":    "&lt;32 bytes em base64&gt;"
/// }
/// </code>
/// </summary>
public sealed class EncryptionOptions
{
    public const string SectionName = "Encryption";

    /// <summary>
    /// Chaves de criptografia por versão (base64, 32 bytes = AES-256).
    ///
    /// <para>
    /// <b>Chaves antigas continuam aqui após uma rotação.</b> Um dado cifrado com a versão 1
    /// carrega esse número no próprio ciphertext e continua legível depois que a versão 2
    /// assume — sem isso, rotacionar exigiria re-criptografar a base inteira offline, que é
    /// como se acaba nunca rotacionando.
    /// </para>
    /// </summary>
    public Dictionary<int, string> Keys { get; } = [];

    /// <summary>Versão usada para cifrar dados novos. As demais só decifram.</summary>
    public int ActiveKeyVersion { get; set; } = 1;

    /// <summary>
    /// Chave do HMAC do índice cego (base64, 32 bytes).
    ///
    /// <para>
    /// <b>Esta chave não pode ser rotacionada sozinha:</b> mudá-la invalida todos os índices
    /// gravados, e a busca por e-mail/CPF para de encontrar qualquer coisa. Rotacioná-la
    /// exige recalcular os índices de toda a base — um processo offline, deliberado.
    /// É uma chave diferente da de criptografia justamente para poder ter um ciclo de vida
    /// diferente.
    /// </para>
    /// </summary>
    public string BlindIndexKey { get; set; } = string.Empty;
}
