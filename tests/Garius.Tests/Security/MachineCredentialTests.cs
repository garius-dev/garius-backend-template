using Garius.Core.Machine;

namespace Garius.Tests.Security;

public class MachineCredentialTests
{
    [Fact]
    public void O_segredo_gerado_tem_entropia_criptografica()
    {
        // 256 bits em base64url. É o que torna o hash rápido (SHA-256) suficiente: não há
        // dicionário nem força bruta viável contra 2^256 — ver MachineCredential.
        var secret = MachineCredential.Generate();

        secret.Length.ShouldBeGreaterThanOrEqualTo(43);

        // base64url: sem +, / ou = (que quebrariam em header, URL e JSON).
        secret.ShouldNotContain("+");
        secret.ShouldNotContain("/");
        secret.ShouldNotContain("=");
    }

    [Fact]
    public void Dois_segredos_gerados_nunca_colidem()
    {
        var secrets = Enumerable.Range(0, 1_000)
            .Select(_ => MachineCredential.Generate())
            .ToHashSet(StringComparer.Ordinal);

        secrets.Count.ShouldBe(1_000);
    }

    [Fact]
    public void A_chave_de_API_nasce_com_o_marcador_que_os_scanners_reconhecem()
    {
        var key = MachineCredential.GenerateApiKey();

        // O prefixo `gk_` é o que permite a um scanner de segredo (GitHub, GitGuardian) detectar
        // a chave vazada num commit público. Um blob de base64 puro passaria despercebido.
        key.ShouldStartWith(MachineCredential.ApiKeyPrefix);
    }

    [Fact]
    public void O_hash_confere_com_o_proprio_segredo_e_so_com_ele()
    {
        var secret = MachineCredential.Generate();
        var hash = MachineCredential.Hash(secret);

        MachineCredential.Verify(secret, hash).ShouldBeTrue();
        MachineCredential.Verify(MachineCredential.Generate(), hash).ShouldBeFalse();
    }

    [Fact]
    public void Verify_com_entrada_vazia_ou_nula_e_falso_e_nao_explode()
    {
        var hash = MachineCredential.Hash("qualquer-coisa");

        MachineCredential.Verify("", hash).ShouldBeFalse();
        MachineCredential.Verify(null!, hash).ShouldBeFalse();
        MachineCredential.Verify("segredo", "").ShouldBeFalse();
        MachineCredential.Verify("segredo", null!).ShouldBeFalse();
    }

    [Fact]
    public void O_prefixo_e_estavel_e_nao_estoura_com_uma_chave_curta()
    {
        var key = MachineCredential.GenerateApiKey();

        var prefix = MachineCredential.PrefixOf(key);

        prefix.Length.ShouldBe(MachineCredential.PrefixLength);
        key.ShouldStartWith(prefix);

        // Uma chave mais curta que o prefixo devolve o que houver, sem lançar.
        MachineCredential.PrefixOf("gk_").ShouldBe("gk_");
    }
}
