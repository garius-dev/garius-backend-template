using Garius.Core.Authorization;
using Garius.Core.Security;

namespace Garius.Tests.Authorization;

/// <summary>
/// O matching de permissões é a decisão de autorização mais fundamental do sistema. Um erro
/// aqui concede acesso indevido em silêncio — e é a única coisa que separa um usuário comum
/// de um superadministrador.
/// </summary>
public class PermissionMatchingTests
{
    [Theory]
    [InlineData("invoices.approve", "invoices.approve")]   // exata
    [InlineData("INVOICES.APPROVE", "invoices.approve")]   // insensível a caixa
    [InlineData("invoices.*", "invoices.approve")]         // curinga do recurso
    [InlineData("*", "invoices.approve")]                  // superadministrador
    [InlineData("*", "qualquer.coisa.nova")]               // inclusive o que ainda não existe
    public void Concede(string granted, string required) =>
        Permission.Matches(granted, required).ShouldBeTrue();

    [Theory]
    [InlineData("invoices.read", "invoices.approve")]      // ação diferente
    [InlineData("users.*", "invoices.approve")]            // recurso diferente
    [InlineData("invoice.*", "invoices.approve")]          // prefixo parecido, mas não é
    [InlineData("", "invoices.approve")]
    public void Nega(string granted, string required) =>
        Permission.Matches(granted, required).ShouldBeFalse();

    /// <summary>
    /// O curinga só existe à DIREITA. Um <c>*.delete</c> ("apagar qualquer coisa") é uma
    /// permissão que ninguém consegue auditar — e que quase sempre seria concedida por engano.
    /// </summary>
    [Fact]
    public void Nao_ha_curinga_a_esquerda()
    {
        Permission.Matches("*.approve", "invoices.approve").ShouldBeFalse();
        Permission.Matches("*.delete", "users.delete").ShouldBeFalse();
    }

    /// <summary>
    /// O curinga de recurso não pode vazar para um recurso cujo nome apenas COMEÇA igual.
    /// Sem o separador na comparação, <c>invoice.*</c> concederia acesso a <c>invoices.*</c>.
    /// </summary>
    [Fact]
    public void O_curinga_de_recurso_respeita_a_fronteira_do_nome()
    {
        Permission.Matches("user.*", "users.delete").ShouldBeFalse();
        Permission.Matches("users.*", "users.delete").ShouldBeTrue();
    }

    [Fact]
    public void O_catalogo_e_descoberto_por_reflexao()
    {
        // Uma aplicação derivada só precisa DECLARAR a permissão; não há uma segunda lista
        // para manter em sincronia.
        Permissions.Catalog.ShouldContain(Permissions.Users.Create);
        Permissions.Catalog.ShouldContain(Permissions.Pii.ReadCpf);

        Permissions.Exists("users.create").ShouldBeTrue();
        Permissions.Exists("permissao.inventada").ShouldBeFalse();
    }

    [Fact]
    public void Cada_escopo_de_PII_tem_a_sua_permissao()
    {
        // Quem vê o e-mail não necessariamente vê o CPF.
        Permissions.ForPiiScope(PiiScope.Email).Value.ShouldBe("pii.email.read");
        Permissions.ForPiiScope(PiiScope.Cpf).Value.ShouldBe("pii.cpf.read");

        Permissions.ForPiiScope(PiiScope.Email)
            .ShouldNotBe(Permissions.ForPiiScope(PiiScope.Cpf));
    }
}
