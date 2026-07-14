using Garius.Api.Infrastructure.Authorization;
using Garius.Api.Infrastructure.Errors;
using Garius.Core.Authorization;
using Garius.Core.Results;

namespace Garius.Api.Features.Catalog;

/// <param name="Value">A permissão: <c>invoices.approve</c>.</param>
/// <param name="Resource">O recurso, para o frontend agrupar por seção.</param>
/// <param name="Action">A ação.</param>
/// <param name="Description">Texto legível, para a tela de administração de papéis.</param>
public sealed record PermissionDto(string Value, string Resource, string Action, string Description);

/// <summary>
/// O catálogo de permissões da aplicação.
///
/// <para>
/// ⚠️ <b>Esta feature se chama <c>Catalog</c>, e NÃO <c>Permissions</c> — de propósito.</b>
/// </para>
///
/// <para>
/// Um namespace <c>Garius.Api.Features.Permissions</c> <b>colide</b> com a classe
/// <see cref="Garius.Core.Authorization.Permissions"/>. Dentro de qualquer <c>Features.*</c>, o
/// identificador <c>Permissions</c> passa a resolver para o NAMESPACE — e um membro de namespace
/// <b>vence o <c>using</c></b>. O resultado é que <c>.RequirePermission(Permissions.Products.Read)</c>,
/// a linha mais comum que uma app derivada escreve, <b>não compila</b>:
/// </para>
///
/// <code>error CS0234: The type or namespace name 'Products' does not exist in the namespace 'Garius.Api.Features.Permissions'</code>
///
/// <para>
/// O erro aponta para o lugar errado (parece faltar uma referência), e a saída óbvia seria um
/// alias (<c>using AppPermissions = ...</c>) em <b>todo</b> arquivo de feature — um imposto
/// permanente sobre cada app derivada. Renomear a feature mata a colisão na raiz, e ninguém mais
/// precisa saber que ela existiu.
/// </para>
/// </summary>
public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/permissions").WithTags("Permissions");

        // O catálogo de permissões, para o frontend montar a tela de administração de papéis.
        //
        // É o que evita a segunda lista: sem este endpoint, o front manteria a sua própria
        // relação de permissões em JavaScript — e as duas divergiriam na primeira permissão
        // nova que alguém esquecesse de copiar.
        group.MapGet("/", (HttpContext http) =>
        {
            // Sem alias e sem qualificar `Core.Authorization.` — é exatamente o que o rename
            // desta feature comprou.
            var catalog = Permissions.Catalog
                .Select(p => new PermissionDto(p.Value, p.Resource, p.Action, p.Description))
                .OrderBy(p => p.Resource, StringComparer.Ordinal)
                .ThenBy(p => p.Action, StringComparer.Ordinal)
                .ToList();

            return Result<IReadOnlyList<PermissionDto>>
                .Success(catalog)
                .ToHttpResult(http);
        })
        .RequirePermission(Permissions.Roles.Read)
        .WithSummary("Lista todas as permissões da aplicação.")
        .WithDescription(
            "Usado pelo frontend para montar a tela de administração de papéis. " +
            "Exige a permissão de leitura de papéis (quem administra papéis precisa saber o que pode conceder).");
    }
}
