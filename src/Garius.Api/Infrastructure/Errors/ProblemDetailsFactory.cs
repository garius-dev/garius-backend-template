using System.Diagnostics;
using Garius.Core.Results;
using Microsoft.AspNetCore.Mvc;

namespace Garius.Api.Infrastructure.Errors;

/// <summary>
/// Traduz um <see cref="Error"/> do domínio para ProblemDetails (RFC 9457) — o formato
/// de erro de toda a API.
///
/// Extensões que sempre acompanham o problema:
/// <list type="bullet">
///   <item><c>traceId</c> — mesmo valor do envelope de sucesso; é a chave para achar o log no Grafana.</item>
///   <item><c>code</c> — código estável (<c>user.email_already_taken</c>) para o frontend decidir comportamento sem parsear texto.</item>
///   <item><c>errors</c> — erros por campo, quando é validação.</item>
/// </list>
/// </summary>
internal static class ProblemDetailsFactory
{
    private const string TypeBaseUri = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5";

    internal static ProblemDetails Create(Error error, HttpContext context)
    {
        var status = ToStatusCode(error.Type);

        var problem = new ProblemDetails
        {
            Type = $"{TypeBaseUri}.{status}",
            Title = ToTitle(error.Type),
            Status = status,
            Detail = error.Message,
            Instance = $"{context.Request.Method} {context.Request.Path}"
        };

        problem.Extensions["traceId"] = GetTraceId(context);
        problem.Extensions["code"] = error.Code;

        if (error.Fields is { Count: > 0 })
        {
            problem.Extensions["errors"] = error.Fields;
        }

        return problem;
    }

    /// <summary>
    /// O traceId vem do <see cref="Activity"/> corrente (W3C trace context), o mesmo que
    /// o Serilog enriquece nos logs — é isso que permite colar o traceId da resposta no
    /// Grafana e achar exatamente o request que falhou.
    /// </summary>
    internal static string GetTraceId(HttpContext context) =>
        Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

    internal static int ToStatusCode(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.BusinessRule => StatusCodes.Status422UnprocessableEntity,
        ErrorType.TooManyRequests => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status500InternalServerError
    };

    private static string ToTitle(ErrorType type) => type switch
    {
        ErrorType.Validation => "Requisição inválida",
        ErrorType.Unauthorized => "Não autenticado",
        ErrorType.Forbidden => "Acesso negado",
        ErrorType.NotFound => "Recurso não encontrado",
        ErrorType.Conflict => "Conflito",
        ErrorType.BusinessRule => "Regra de negócio violada",
        ErrorType.TooManyRequests => "Requisições em excesso",
        _ => "Erro interno"
    };
}
