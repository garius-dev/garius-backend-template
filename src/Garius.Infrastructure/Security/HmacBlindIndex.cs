using System.Security.Cryptography;
using System.Text;
using Garius.Core.Security;
using Microsoft.Extensions.Options;

namespace Garius.Infrastructure.Security;

/// <summary>
/// Índice cego por HMAC-SHA256. Devolve a busca exata sobre campos cifrados com nonce
/// aleatório, sem decifrar nada:
///
/// <code>
/// WHERE "EmailIndex" = @hmac_do_email_digitado
/// </code>
///
/// <para>
/// <b>HMAC, e não SHA-256 puro:</b> um hash simples de CPF é quebrável por força bruta em
/// segundos — só existem ~10^11 CPFs, e um atacante com o dump do banco enumera todos. O
/// HMAC exige a chave secreta, que vive no Secret Manager: sem ela, o índice não diz nada.
/// </para>
/// </summary>
internal sealed class HmacBlindIndex : IBlindIndex
{
    private readonly byte[] _key;

    public HmacBlindIndex(IOptions<EncryptionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var base64 = options.Value.BlindIndexKey;

        if (string.IsNullOrWhiteSpace(base64))
        {
            throw new InvalidOperationException(
                "Encryption:BlindIndexKey não foi configurada. Sem ela, não é possível buscar por " +
                "e-mail ou CPF (nem fazer login). Configure-a no Google Secret Manager " +
                "(32 bytes em base64).");
        }

        _key = Convert.FromBase64String(base64);

        if (_key.Length != 32)
        {
            throw new InvalidOperationException(
                $"Encryption:BlindIndexKey tem {_key.Length} bytes; são esperados 32.");
        }
    }

    public byte[] Compute(PiiScope scope, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var normalized = Normalize(scope, value);

        return HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(normalized));
    }

    /// <summary>
    /// <b>É aqui que se erra.</b> A normalização tem de ser <b>exatamente a mesma</b> na
    /// gravação e na busca. Se <c>"123.456.789-01"</c> e <c>"12345678901"</c> gerassem
    /// índices diferentes, o mesmo CPF passaria duas vezes pelo índice único e a busca não
    /// acharia o registro.
    ///
    /// <para>
    /// A normalização depende do <b>escopo</b>, não de adivinhar pelo conteúdo — por isso
    /// <see cref="IBlindIndex.Compute"/> recebe o <see cref="PiiScope"/>.
    /// </para>
    /// </summary>
    private static string Normalize(PiiScope scope, string value) => scope switch
    {
        // Só os dígitos: com ou sem máscara é o mesmo documento/telefone.
        PiiScope.Cpf or PiiScope.Phone => new string([.. value.Where(char.IsDigit)]),

        // ToLowerInvariant, NÃO ToLower: em cultura turca, "I".ToLower() é "ı" (sem ponto),
        // e o mesmo e-mail geraria índices diferentes conforme o locale da máquina.
        _ => value.Trim().ToLowerInvariant()
    };
}
