using Garius.Core.Security;
using Garius.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Garius.Tests.Infrastructure;

/// <summary>
/// Chaves de criptografia para os testes. São chaves de teste reais (32 bytes válidos), não
/// mocks — o objetivo é exercitar o AES-GCM e o HMAC de verdade.
/// </summary>
internal static class TestCrypto
{
    internal static readonly EncryptionOptions Options = new()
    {
        Keys = { [1] = "ZFbLDHAltmKIu1ANyNd7XyLre4jRiwYwKWjL8Lrn7nU=" },
        ActiveKeyVersion = 1,
        BlindIndexKey = "ywIgmu+JbmkZ2HMcpLnWgheAF0CxDQlVZrRjT3VpaO4="
    };

    internal static IFieldEncryptor Encryptor { get; } =
        new AesGcmFieldEncryptor(Microsoft.Extensions.Options.Options.Create(Options));

    internal static IBlindIndex BlindIndex { get; } =
        new HmacBlindIndex(Microsoft.Extensions.Options.Options.Create(Options));
}
