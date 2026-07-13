using System.Security.Cryptography;
using Garius.Core.Security;
using Garius.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Garius.Tests.Security;

/// <summary>
/// Trava as propriedades da criptografia de campo. Cada teste corresponde a uma decisão de
/// design que, se quebrada, torna a proteção inútil.
/// </summary>
public class FieldEncryptionTests
{
    private const string KeyV1 = "ZFbLDHAltmKIu1ANyNd7XyLre4jRiwYwKWjL8Lrn7nU=";
    private const string KeyV2 = "N4mhGLnOq3KuwYVAUeGUsU/hfCoiXBk63EPVoWco5Gg=";

    [Fact]
    public void Cifra_e_decifra_o_valor()
    {
        var encryptor = Build(activeVersion: 1);

        var cipher = encryptor.Encrypt("joao@empresa.com");

        encryptor.Decrypt(cipher).ShouldBe("joao@empresa.com");
    }

    /// <summary>
    /// A propriedade que justifica o nonce aleatório: o mesmo valor produz ciphertexts
    /// DIFERENTES. Com criptografia determinística, dois usuários com o mesmo CPF teriam o
    /// mesmo ciphertext — e um atacante com o dump correlacionaria registros sem ter a chave.
    /// </summary>
    [Fact]
    public void O_mesmo_valor_gera_ciphertexts_diferentes()
    {
        var encryptor = Build(activeVersion: 1);

        var first = encryptor.Encrypt("12345678901");
        var second = encryptor.Encrypt("12345678901");

        first.ShouldNotBe(second);

        // ...e ambos decifram para o mesmo valor.
        encryptor.Decrypt(first).ShouldBe("12345678901");
        encryptor.Decrypt(second).ShouldBe("12345678901");
    }

    /// <summary>
    /// GCM é autenticado: adulterar o dado no banco faz a decifragem FALHAR, em vez de
    /// devolver lixo silenciosamente.
    /// </summary>
    [Fact]
    public void Detecta_adulteracao_do_ciphertext()
    {
        var encryptor = Build(activeVersion: 1);

        var cipher = encryptor.Encrypt("joao@empresa.com");

        // Um byte alterado — como faria alguém com acesso de escrita ao banco.
        cipher[^1] ^= 0xFF;

        Should.Throw<CryptographicException>(() => encryptor.Decrypt(cipher));
    }

    /// <summary>
    /// A prova de que a rotação de chave funciona SEM re-criptografar a base: um dado
    /// cifrado com a v1 continua legível depois que a v2 vira a ativa, porque a versão
    /// viaja dentro do próprio ciphertext.
    ///
    /// <para>
    /// Sem isso, rotacionar exigiria um processo offline sobre toda a base — e é assim que
    /// se acaba nunca rotacionando.
    /// </para>
    /// </summary>
    [Fact]
    public void Rotacao_de_chave_mantem_os_dados_antigos_legiveis()
    {
        // Dado gravado quando a v1 era a chave ativa.
        var before = Build(activeVersion: 1);
        var oldCipher = before.Encrypt("cpf-gravado-antes-da-rotacao");

        // A chave é rotacionada: a v2 passa a ser a ativa, mas a v1 continua configurada.
        var after = Build(activeVersion: 2);

        // O dado antigo continua legível.
        after.Decrypt(oldCipher).ShouldBe("cpf-gravado-antes-da-rotacao");

        // E os dados novos já usam a v2.
        var newCipher = after.Encrypt("cpf-novo");
        newCipher[3].ShouldBe((byte)2, "a versão da chave vai nos 4 primeiros bytes do ciphertext");

        after.Decrypt(newCipher).ShouldBe("cpf-novo");
    }

    /// <summary>
    /// O erro que destrói dados: rotacionar e REMOVER a chave antiga. A mensagem precisa
    /// dizer exatamente isso — não um "erro de decifragem" genérico.
    /// </summary>
    [Fact]
    public void Remover_a_chave_antiga_torna_os_dados_ilegiveis_e_o_erro_e_explicito()
    {
        var withV1 = Build(activeVersion: 1);
        var cipher = withV1.Encrypt("dado-cifrado-com-a-v1");

        // Alguém rotacionou e apagou a v1 do Secret Manager.
        var onlyV2 = new AesGcmFieldEncryptor(Options.Create(new EncryptionOptions
        {
            Keys = { [2] = KeyV2 },
            ActiveKeyVersion = 2,
            BlindIndexKey = KeyV1
        }));

        var exception = Should.Throw<CryptographicException>(() => onlyV2.Decrypt(cipher));

        exception.Message.ShouldContain("versão 1");
        exception.Message.ShouldContain("mantenha as chaves antigas");
    }

    [Fact]
    public void Chave_de_tamanho_errado_falha_no_boot()
    {
        var options = Options.Create(new EncryptionOptions
        {
            Keys = { [1] = Convert.ToBase64String(new byte[16]) },   // 128 bits, não 256
            ActiveKeyVersion = 1
        });

        var exception = Should.Throw<InvalidOperationException>(() => new AesGcmFieldEncryptor(options));

        exception.Message.ShouldContain("32");
    }

    [Fact]
    public void ActiveKeyVersion_sem_chave_correspondente_falha_no_boot()
    {
        var options = Options.Create(new EncryptionOptions
        {
            Keys = { [1] = KeyV1 },
            ActiveKeyVersion = 5   // não existe
        });

        Should.Throw<InvalidOperationException>(() => new AesGcmFieldEncryptor(options));
    }

    private static AesGcmFieldEncryptor Build(int activeVersion) =>
        new(Options.Create(new EncryptionOptions
        {
            Keys = { [1] = KeyV1, [2] = KeyV2 },
            ActiveKeyVersion = activeVersion,
            BlindIndexKey = KeyV1
        }));
}
