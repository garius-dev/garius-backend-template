using Garius.Api.Infrastructure.Authorization;
using Garius.Api.Infrastructure.Errors;
using Garius.Core.Authorization;
using Garius.Core.Results;

namespace Garius.Api.Features.Permissions;

/// <param name="Value">A permissão: <c>invoices.approve</c>.</param>
/// <param name="Resource">O recurso, para o frontend agrupar por seção.</param>
/// <param name="Action">A ação.</param>
/// <param name="Description">Texto legível, para a tela de administração de papéis.</param>
public sealed record PermissionDto(string Value, string Resource, string Action, string Description);

public static class PermissionEndpoints
{
    public static void MapPermissionEndpoints(this IEndpointRouteBuilder app)
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
            var catalog = Core.Authorization.Permissions.Catalog
                .Select(p => new PermissionDto(p.Value, p.Resource, p.Action, p.Description))
                .OrderBy(p => p.Resource, StringComparer.Ordinal)
                .ThenBy(p => p.Action, StringComparer.Ordinal)
                .ToList();

            return Result<IReadOnlyList<PermissionDto>>
                .Success(catalog)
                .ToHttpResult(http);
        })
        .RequirePermission(Core.Authorization.Permissions.Roles.Read)
        .WithSummary("Lista todas as permissões da aplicação.")
        .WithDescription(
            "Usado pelo frontend para montar a tela de administração de papéis. " +
            "Exige a permissão de leitura de papéis (quem administra papéis precisa saber o que pode conceder).");
    }
}
