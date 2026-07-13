using Garius.Core.Security;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Garius.Infrastructure.Database.Converters;

/// <summary>
/// Cifra e decifra um <see cref="Pii"/> de forma transparente entre a entidade e o banco:
/// no C# é um <see cref="Pii"/>, no Postgres é um <c>bytea</c> cifrado com AES-256-GCM.
///
/// <code>
/// user.Email                    // Pii  (mascarado se logado/serializado)
/// // no banco: \x00000001a3f2...  (bytea — inútil sem a chave)
/// </code>
///
/// <para>
/// Isso é o que faz a criptografia ser <b>impossível de esquecer</b>: quem escreve o código
/// da feature não chama o encryptor, não decide cifrar. A coluna está declarada como PII no
/// mapeamento, e todo caminho de leitura e escrita passa por aqui.
/// </para>
/// </summary>
internal sealed class PiiConverter(IFieldEncryptor encryptor, PiiScope scope)
    : ValueConverter<Pii, byte[]>(
        pii => pii.IsEmpty ? Array.Empty<byte>() : encryptor.Encrypt(pii.Reveal()),
        bytes => bytes.Length == 0 ? Pii.Empty(scope) : Pii.Create(scope, encryptor.Decrypt(bytes)));
