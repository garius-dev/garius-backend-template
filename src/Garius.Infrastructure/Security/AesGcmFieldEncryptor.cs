using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Garius.Core.Security;
using Microsoft.Extensions.Options;

namespace Garius.Infrastructure.Security;

/// <summary>
/// AES-256-GCM com nonce aleatório.
///
/// <para>
/// <b>Formato do ciphertext</b> (tudo o que é preciso para decifrar vive dentro dele):
/// </para>
/// <code>
/// ┌──────────┬───────────┬─────────────────┬──────────┐
/// │ versão   │ nonce     │ dados cifrados  │ tag GCM  │
/// │ 4 bytes  │ 12 bytes  │ N bytes         │ 16 bytes │
/// └──────────┴───────────┴─────────────────┴──────────┘
/// </code>
///
/// <para>
/// <b>Por que a versão vai junto:</b> é o que torna a rotação de chave viável. Um dado
/// cifrado com a chave v1 continua legível depois que a v2 assume — sem isso, rotacionar
/// exigiria re-criptografar a base inteira offline, e na prática ninguém rotacionaria nunca.
/// </para>
///
/// <para>
/// <b>Por que GCM:</b> é autenticado. Se alguém alterar um byte no banco, o
/// <see cref="Decrypt"/> lança em vez de devolver lixo silenciosamente.
/// </para>
/// </summary>
internal sealed class AesGcmFieldEncryptor : IFieldEncryptor
{
    private const int VersionSize = sizeof(int);
    private const int NonceSize = 12;   // 96 bits: o tamanho recomendado para GCM
    private const int TagSize = 16;     // 128 bits

    private readonly Dictionary<int, byte[]> _keys;
    private readonly int _activeVersion;

    public AesGcmFieldEncryptor(IOptions<EncryptionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;

        if (settings.Keys.Count == 0)
        {
            throw new InvalidOperationException(
                "Encryption:Keys não foi configurado. Sem chave, os dados pessoais não podem ser " +
                "cifrados. Configure-a no Google Secret Manager (32 bytes em base64).");
        }

        _keys = settings.Keys.ToDictionary(
            pair => pair.Key,
            pair => DecodeKey(pair.Value, $"Encryption:Keys:{pair.Key}"));

        _activeVersion = settings.ActiveKeyVersion;

        if (!_keys.ContainsKey(_activeVersion))
        {
            throw new InvalidOperationException(
                $"Encryption:ActiveKeyVersion = {_activeVersion}, mas não existe uma chave com essa " +
                $"versão em Encryption:Keys. Versões disponíveis: {string.Join(", ", _keys.Keys)}.");
        }
    }

    public byte[] Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var key = _keys[_activeVersion];
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);

        var result = new byte[VersionSize + NonceSize + plainBytes.Length + TagSize];

        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(0, VersionSize), _activeVersion);

        var nonce = result.AsSpan(VersionSize, NonceSize);
        var cipher = result.AsSpan(VersionSize + NonceSize, plainBytes.Length);
        var tag = result.AsSpan(VersionSize + NonceSize + plainBytes.Length, TagSize);

        // Nonce ALEATÓRIO por gravação: é o que faz o mesmo valor gerar ciphertexts
        // diferentes, e o que impede um atacante de correlacionar registros iguais.
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        return result;
    }

    public string Decrypt(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        if (ciphertext.Length < VersionSize + NonceSize + TagSize)
        {
            throw new CryptographicException("Ciphertext malformado: menor que o cabeçalho mínimo.");
        }

        var version = BinaryPrimitives.ReadInt32BigEndian(ciphertext.AsSpan(0, VersionSize));

        if (!_keys.TryGetValue(version, out var key))
        {
            throw new CryptographicException(
                $"O dado foi cifrado com a chave versão {version}, que não está configurada. " +
                "Ao rotacionar, mantenha as chaves antigas em Encryption:Keys — do contrário " +
                "os dados cifrados com elas ficam ilegíveis para sempre.");
        }

        var plainLength = ciphertext.Length - VersionSize - NonceSize - TagSize;

        var nonce = ciphertext.AsSpan(VersionSize, NonceSize);
        var cipher = ciphertext.AsSpan(VersionSize + NonceSize, plainLength);
        var tag = ciphertext.AsSpan(VersionSize + NonceSize + plainLength, TagSize);

        var plainBytes = new byte[plainLength];

        using var aes = new AesGcm(key, TagSize);

        // Lança CryptographicException se a tag não bater — ou seja, se o dado no banco
        // tiver sido adulterado. Melhor falhar alto do que devolver lixo.
        aes.Decrypt(nonce, cipher, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] DecodeKey(string base64, string settingName)
    {
        byte[] key;

        try
        {
            key = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{settingName} não é um base64 válido.", ex);
        }

        return key.Length == 32
            ? key
            : throw new InvalidOperationException(
                $"{settingName} tem {key.Length} bytes; AES-256 exige exatamente 32.");
    }
}
