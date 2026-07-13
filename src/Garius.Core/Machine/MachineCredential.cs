using System.Security.Cryptography;
using System.Text;

namespace Garius.Core.Machine;

/// <summary>
/// Gera e verifica os segredos de máquina (<c>client_secret</c> e chave de API).
///
/// <para>
/// <b>Por que SHA-256 e não Argon2/BCrypt.</b> Um KDF lento existe para tornar inviável o
/// ataque de dicionário contra um segredo <b>escolhido por um humano</b> — que tem pouca
/// entropia real. Estes segredos são gerados <b>por nós</b>, com 256 bits vindos do CSPRNG:
/// não há dicionário, e a força bruta contra 2²⁵⁶ não termina antes do fim do universo. O
/// custo de um KDF lento aqui seria pago a <b>cada request</b> autenticado por chave de API,
/// sem comprar segurança nenhuma.
/// </para>
///
/// <para>
/// A comparação é em <b>tempo constante</b> — ver <see cref="Verify"/>.
/// </para>
/// </summary>
public static class MachineCredential
{
    /// <summary>
    /// Quantos caracteres do segredo de API viram o prefixo <b>público</b>, guardado em claro e
    /// indexado. Ver <see cref="ApiKey.KeyPrefix"/>.
    /// </summary>
    public const int PrefixLength = 8;

    /// <summary>
    /// Marca as chaves de API. Um segredo que sai no formato <c>gk_&lt;algo&gt;</c> é
    /// reconhecível: scanners de segredo (GitHub, GitGuardian) conseguem detectá-lo vazado num
    /// commit público — o que um blob aleatório de base64 não permite.
    /// </summary>
    public const string ApiKeyPrefix = "gk_";

    /// <summary>256 bits do CSPRNG, em base64url (seguro em header, URL e JSON).</summary>
    public static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
               .Replace('+', '-')
               .Replace('/', '_')
               .TrimEnd('=');

    /// <summary>Gera uma chave de API completa, já com o prefixo identificável.</summary>
    public static string GenerateApiKey() => ApiKeyPrefix + Generate();

    /// <summary>O que vai para o banco. O segredo em claro nunca é gravado.</summary>
    public static string Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    /// <summary>
    /// Os primeiros <see cref="PrefixLength"/> caracteres, que ficam em claro para busca e
    /// exibição. Uma chave curta demais (só o marcador) devolve o que houver — não estoura.
    /// </summary>
    public static string PrefixOf(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return key.Length <= PrefixLength ? key : key[..PrefixLength];
    }

    /// <summary>
    /// Compara em <b>tempo constante</b>.
    ///
    /// <para>
    /// Um <c>==</c> de string sai no primeiro byte diferente. A diferença de tempo entre um
    /// hash que erra no primeiro caractere e um que erra no último é medível pela rede — e
    /// permite descobrir o segredo caractere a caractere, em vez de por força bruta. É um
    /// ataque real (<i>timing attack</i>), e a defesa custa nada.
    /// </para>
    /// </summary>
    public static bool Verify(string secret, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Hash(secret)),
            Encoding.UTF8.GetBytes(expectedHash));
    }
}
