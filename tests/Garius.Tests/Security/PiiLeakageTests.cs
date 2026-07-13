using System.Text.Json;
using Garius.Core.Security;

namespace Garius.Tests.Security;

/// <summary>
/// Prova que <see cref="Pii"/> <b>não vaza por acidente</b> — que é o ponto de ele ser um
/// tipo e não uma <c>string</c>.
///
/// <para>
/// Criptografar o banco protege contra um dump vazado. Não protege contra o dado sair num
/// log, num JSON de resposta ou numa mensagem de erro — que é como PII realmente vaza no
/// dia a dia. Estes testes travam essa segunda metade.
/// </para>
/// </summary>
public class PiiLeakageTests
{
    private const string Email = "joao.silva@empresa.com";
    private const string Cpf = "12345678901";

    [Fact]
    public void Interpolar_em_string_produz_o_valor_MASCARADO()
    {
        var pii = Pii.Create(PiiScope.Email, Email);

        // O caso real: logger.LogInformation($"usuário {user.Email}") ou um ToString()
        // implícito. ToString() devolve a máscara, então o descuido não vaza o dado.
        var interpolated = $"usuário {pii}";

        interpolated.ShouldNotContain(Email);
        interpolated.ShouldContain("j***@empresa.com");
    }

    [Fact]
    public void Serializar_em_JSON_produz_o_valor_MASCARADO()
    {
        var dto = new { Email = Pii.Create(PiiScope.Email, Email) };

        // O caso real: alguém põe um Pii num DTO de resposta. O JsonConverter garante que
        // o que sai pela API é a máscara — nunca o e-mail.
        var json = JsonSerializer.Serialize(dto);

        json.ShouldNotContain(Email);
        json.ShouldContain("j***@empresa.com");
    }

    [Fact]
    public void Deserializar_Pii_direto_e_PROIBIDO()
    {
        // Impedir isto força o padrão correto: PII entra como string no DTO de request e só
        // vira Pii no domínio, onde o escopo é conhecido. Adivinhar o escopo produziria a
        // máscara errada — mascarar um CPF como se fosse e-mail é como se vaza um CPF.
        Should.Throw<NotSupportedException>(() =>
            JsonSerializer.Deserialize<Pii>("\"joao@empresa.com\""));
    }

    [Fact]
    public void Reveal_e_o_UNICO_caminho_para_o_valor_em_claro()
    {
        var pii = Pii.Create(PiiScope.Email, Email);

        // Explícito, fácil de achar num grep e num code review — que é o objetivo.
        pii.Reveal().ShouldBe(Email);
    }

    [Theory]
    [InlineData(PiiScope.Email, "joao.silva@empresa.com", "j***@empresa.com")]
    [InlineData(PiiScope.Cpf, "12345678901", "***.***.789-**")]
    [InlineData(PiiScope.Phone, "11987654321", "*****-4321")]
    public void A_mascara_preserva_o_suficiente_para_o_titular_reconhecer(
        PiiScope scope, string value, string expected)
    {
        // A máscara não é só "***": ela mantém o bastante para a própria pessoa reconhecer
        // o dado (e um atendente confirmar identidade), sem identificá-la para terceiros.
        Pii.Create(scope, value).Masked.ShouldBe(expected);
    }

    [Fact]
    public void CPF_mascarado_nao_contem_nenhum_digito_inicial()
    {
        // Os 6 primeiros dígitos são os que mais restringem a busca; a máscara os esconde.
        var masked = Pii.Create(PiiScope.Cpf, Cpf).Masked;

        masked.ShouldNotContain("123");
        masked.ShouldNotContain("456");
    }

    [Fact]
    public void Pii_vazio_nao_quebra()
    {
        var empty = Pii.Empty(PiiScope.Email);

        empty.IsEmpty.ShouldBeTrue();
        empty.Masked.ShouldBe(string.Empty);
        empty.Reveal().ShouldBe(string.Empty);
    }
}
