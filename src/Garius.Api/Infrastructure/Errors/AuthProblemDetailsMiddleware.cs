using Garius.Core.Results;
using Microsoft.AspNetCore.Mvc;

namespace Garius.Api.Infrastructure.Errors;

/// <summary>
/// Faz 401 e 403 respeitarem o contrato de resposta.
///
/// <para>
/// O middleware de autorização do ASP.NET responde apenas com o status — <b>corpo vazio</b>.
/// Isso quebraria a regra de que todo erro sai em ProblemDetails: o frontend receberia um 401
/// sem <c>code</c> e sem <c>traceId</c>, e teria que tratar esses dois status como um caso
/// especial, diferente de todos os outros erros da API.
/// </para>
///
/// <para>
/// Roda <b>depois</b> do pipeline (na volta), quando o status já foi decidido e nada foi
/// escrito ainda.
/// </para>
/// </summary>
internal sealed class AuthProblemDetailsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        await next(context);

        // Só age se o status foi 401/403 e ninguém escreveu corpo — do contrário sobrescreveria
        // um ProblemDetails que um endpoint já tenha produzido (ex.: pii.forbidden, que traz
        // um code mais específico).
        if (context.Response.HasStarted)
        {
            return;
        }

        var error = context.Response.StatusCode switch
        {
            StatusCodes.Status401Unauthorized => Error.Unauthorized(
                "auth.unauthenticated",
                "É necessário estar autenticado para acessar este recurso."),

            StatusCodes.Status403Forbidden => Error.Forbidden(
                "auth.insufficient_permission",
                "Você não tem permissão para acessar este recurso."),

            _ => null
        };

        if (error is null)
        {
            return;
        }

        var problem = ProblemDetailsFactory.Create(error, context);

        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync<ProblemDetails>(problem);
    }
}
