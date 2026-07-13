using Garius.Core.Security;
using Garius.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Garius.Tests.Security;

/// <summary>
/// O índice cego é o que devolve a busca exata (e o login) sobre um campo cifrado com nonce
/// aleatório. Se a normalização divergir entre a gravação e a busca, o login quebra por um
/// espaço em branco — e o índice único deixa de impedir duplicatas.
/// </summary>
public class BlindIndexTests
{
    private static readonly HmacBlindIndex Index = new(Options.Create(new EncryptionOptions
    {
        BlindIndexKey = "ywIgmu+JbmkZ2HMcpLnWgheAF0CxDQlVZrRjT3VpaO4="
    }));

    [Fact]
    public void O_mesmo_valor_gera_sempre_o_mesmo_indice()
    {
        // É o oposto da criptografia (que é aleatória de propósito): o índice PRECISA ser
        // determinístico, ou não haveria como buscar por ele.
        var first = Index.Compute(PiiScope.Email, "joao@empresa.com");
        var second = Index.Compute(PiiScope.Email, "joao@empresa.com");

        first.ShouldBe(second);
    }

    [Theory]
    [InlineData("joao@empresa.com")]
    [InlineData("JOAO@EMPRESA.COM")]
    [InlineData("  Joao@Empresa.com  ")]
    public void E_mail_normaliza_maiusculas_e_espacos(string variant)
    {
        // Sem isto, o usuário que digita "JOAO@..." no login não encontraria a própria conta.
        var canonical = Index.Compute(PiiScope.Email, "joao@empresa.com");

        Index.Compute(PiiScope.Email, variant).ShouldBe(canonical);
    }

    [Theory]
    [InlineData("12345678901")]
    [InlineData("123.456.789-01")]
    [InlineData(" 123.456.789-01 ")]
    public void CPF_normaliza_a_mascara(string variant)
    {
        // Sem isto, o MESMO CPF passaria duas vezes pelo índice único (uma com máscara,
        // outra sem) — e a busca não acharia o registro cadastrado na outra grafia.
        var canonical = Index.Compute(PiiScope.Cpf, "12345678901");

        Index.Compute(PiiScope.Cpf, variant).ShouldBe(canonical);
    }

    [Fact]
    public void Valores_diferentes_geram_indices_diferentes()
    {
        var first = Index.Compute(PiiScope.Email, "joao@empresa.com");
        var second = Index.Compute(PiiScope.Email, "maria@empresa.com");

        first.ShouldNotBe(second);
    }

    [Fact]
    public void O_indice_nao_revela_o_valor()
    {
        // É um HMAC-SHA256: 32 bytes que não guardam relação legível com a entrada.
        // O que protege o CPF (espaço de busca de apenas ~10^11) é a CHAVE secreta —
        // um SHA-256 puro seria quebrado por força bruta em segundos.
        var index = Index.Compute(PiiScope.Cpf, "12345678901");

        index.Length.ShouldBe(32);
        Convert.ToBase64String(index).ShouldNotContain("12345678901");
    }

    [Fact]
    public void Chave_ausente_falha_no_boot()
    {
        var options = Options.Create(new EncryptionOptions { BlindIndexKey = "" });

        var exception = Should.Throw<InvalidOperationException>(() => new HmacBlindIndex(options));

        exception.Message.ShouldContain("BlindIndexKey");
    }
}
