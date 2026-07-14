using System.Reflection;
using Garius.Core.Authorization;
using Shouldly;

namespace Garius.Tests.Api;

/// <summary>
/// Nenhuma feature pode se chamar <c>Permissions</c> — o namespace colidiria com a classe
/// <see cref="Permissions"/> e <b>quebraria a linha mais comum do template</b>.
///
/// <para>
/// <b>Isto já aconteceu.</b> A feature do catálogo se chamava <c>Features/Permissions</c>, e o
/// efeito era que, dentro de QUALQUER <c>Features.*</c>, o identificador <c>Permissions</c>
/// resolvia para o NAMESPACE — não para a classe. Um membro de namespace <b>vence o
/// <c>using</c></b>, então nem importar <c>Garius.Core.Authorization</c> resolvia:
/// </para>
///
/// <code>
/// namespace Garius.Api.Features.Products;
/// ...
/// .RequirePermission(Permissions.Products.Read);
/// // error CS0234: The type or namespace name 'Products' does not exist
/// //              in the namespace 'Garius.Api.Features.Permissions'
/// </code>
///
/// <para>
/// O erro aponta para o lugar errado (parece faltar uma referência), e a "solução" que se
/// encontra é um <c>using AppPermissions = ...</c> em todo arquivo de feature — um imposto
/// permanente sobre cada app derivada. O template pagava esse imposto em dois arquivos.
/// </para>
///
/// <para>
/// A feature foi renomeada para <c>Catalog</c>. Este teste existe para que ninguém a traga de
/// volta: a falha seria descoberta lá na frente, por quem estivesse escrevendo o PRIMEIRO
/// endpoint da app derivada — e ela não teria contexto nenhum para entender o erro.
/// </para>
/// </summary>
public class NamespaceCollisionTests
{
    [Fact]
    public void Nenhum_namespace_de_feature_colide_com_a_classe_Permissions()
    {
        var apiAssembly = typeof(Program).Assembly;

        var offending = apiAssembly
            .GetTypes()
            .Select(type => type.Namespace)
            .Where(ns => ns is not null)
            .Distinct(StringComparer.Ordinal)
            .Where(ns => ns!.StartsWith("Garius.Api.Features.", StringComparison.Ordinal))
            .Where(ns =>
            {
                // O último segmento do namespace é o nome da feature.
                var feature = ns!.Split('.')[^1];

                // Nomes que colidem com um TIPO que toda feature usa sem qualificar. Um
                // namespace com qualquer um destes nomes quebra o código que o README ensina.
                return feature is "Permissions"   // Garius.Core.Authorization.Permissions
                    or "Results"                  // Microsoft.AspNetCore.Http.Results
                    or "Security"
                    or "Authorization";
            })
            .ToList();

        offending.ShouldBeEmpty(
            $"estes namespaces de feature colidem com um tipo do template: " +
            $"{string.Join(", ", offending)}. Um membro de namespace VENCE o `using`, então " +
            "`.RequirePermission(Permissions.X.Y)` deixaria de compilar em TODA feature — e o " +
            "erro (CS0234) aponta para o lugar errado. Renomeie a feature.");
    }

    /// <summary>
    /// E a prova de que o caminho feliz funciona: a classe <see cref="Permissions"/> é
    /// alcançável pelo nome simples, que é como o README ensina a usá-la.
    /// </summary>
    [Fact]
    public void A_classe_Permissions_e_alcancavel_pelo_nome_simples()
    {
        // Se um namespace de feature voltasse a se chamar `Permissions`, esta linha ainda
        // compilaria AQUI (o teste não vive em Features.*) — por isso o teste acima existe, e é
        // ele que tem os dentes. Esta asserção só documenta o uso pretendido.
        Permissions.Clients.Read.Value.ShouldBe("clients.read");

        Permissions.Catalog.ShouldNotBeEmpty();
    }
}
