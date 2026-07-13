using Garius.Core.Results;
using Microsoft.AspNetCore.Mvc;

namespace Garius.Api.Infrastructure.Errors;

/// <summary>
/// Converte um <see cref="Result"/> do domínio na resposta HTTP.
///
/// É a única forma de um endpoint responder: sucesso vira o envelope
/// <see cref="ApiResponse{T}"/>, falha vira ProblemDetails. Não há caminho em que um
/// endpoint devolva algo fora desse contrato.
///
/// <code>
/// app.MapGet("/users/{id}", async (Guid id, IUserService users, HttpContext http) =>
///     (await users.GetAsync(id)).ToHttpResult(http));
/// </code>
/// </summary>
public static class ResultExtensions
{
    /// <summary>Sucesso → 200 com envelope. Falha → ProblemDetails com o status do erro.</summary>
    public static IResult ToHttpResult<T>(this Result<T> result, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);

        return result.IsSuccess
            ? Results.Ok(ApiResponse<T>.Ok(result.Value, ProblemDetailsFactory.GetTraceId(context)))
            : Problem(result.Error, context);
    }

    /// <summary>Sucesso → 200 sem payload. Falha → ProblemDetails.</summary>
    public static IResult ToHttpResult(this Result result, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);

        return result.IsSuccess
            ? Results.Ok(ApiResponse.Ok(ProblemDetailsFactory.GetTraceId(context)))
            : Problem(result.Error, context);
    }

    /// <summary>Sucesso → 201 com Location. Falha → ProblemDetails.</summary>
    public static IResult ToCreatedResult<T>(this Result<T> result, HttpContext context, string location)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);

        return result.IsSuccess
            ? Results.Created(location, ApiResponse<T>.Ok(result.Value, ProblemDetailsFactory.GetTraceId(context)))
            : Problem(result.Error, context);
    }

    /// <summary>Sucesso → 200 com envelope paginado. Falha → ProblemDetails.</summary>
    public static IResult ToPagedResult<T>(
        this Result<IReadOnlyList<T>> result,
        HttpContext context,
        PageInfo page)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);

        return result.IsSuccess
            ? Results.Ok(PagedResponse<T>.Ok(result.Value, page, ProblemDetailsFactory.GetTraceId(context)))
            : Problem(result.Error, context);
    }

    private static IResult Problem(Error error, HttpContext context)
    {
        var problem = ProblemDetailsFactory.Create(error, context);

        return Results.Json<ProblemDetails>(
            problem,
            statusCode: problem.Status,
            contentType: "application/problem+json");
    }
}
