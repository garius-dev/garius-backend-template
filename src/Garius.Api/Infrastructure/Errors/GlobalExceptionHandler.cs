using Garius.Core.Results;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Garius.Api.Infrastructure.Errors;

/// <summary>
/// Rede de segurança: qualquer exceção que escape de um endpoint vira ProblemDetails.
///
/// <para>
/// <b>Nada de interno vaza para o cliente</b> — nem stack trace, nem mensagem de exceção,
/// nem em Development. O cliente recebe uma mensagem genérica e um <c>traceId</c>; o
/// detalhe fica no log, correlacionado por esse mesmo traceId. Isso evita o vazamento
/// clássico em que o texto de uma <c>NpgsqlException</c> entrega host, usuário e schema.
/// </para>
///
/// <para>
/// Chegar aqui significa <b>bug ou falha de infra</b>: erro de negócio esperado é
/// <see cref="Result"/>, e nunca deveria virar exceção.
/// </para>
/// </summary>
internal sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        // Cliente desistiu (fechou a aba, timeout). Não é erro nosso e não deve poluir o log.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return true;
        }

        var traceId = ProblemDetailsFactory.GetTraceId(httpContext);

        logger.LogError(
            exception,
            "Exceção não tratada em {Method} {Path}. TraceId={TraceId}",
            httpContext.Request.Method,
            httpContext.Request.Path,
            traceId);

        var problem = ProblemDetailsFactory.Create(Error.Unexpected(), httpContext);

        httpContext.Response.StatusCode = problem.Status!.Value;
        await httpContext.Response.WriteAsJsonAsync<ProblemDetails>(problem, cancellationToken);

        return true;
    }
}
