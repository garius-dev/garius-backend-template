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

        // CORPO INVÁLIDO: culpa do CLIENTE (400), nunca falha nossa (500).
        //
        // ⚠️ Sem isto, um `POST` com `{}` ou com JSON quebrado devolvia **500 server.unexpected**.
        // Isso é errado em três frentes ao mesmo tempo:
        //
        //   1. MENTE sobre a causa: culpa o servidor por um request que o cliente montou errado.
        //   2. POLUI o log: o GetLevel do Serilog marca 5xx como Error — então qualquer scanner
        //      batendo na API gerava ERRO no Grafana. Alarme falso, no sistema que existe para
        //      dar alarme verdadeiro.
        //   3. É um vetor barato de ruído: mandar lixo no corpo custa nada, e gerava stack trace.
        //
        // Um JSON malformado nem chega ao validador — ele estoura na DESSERIALIZAÇÃO, antes de
        // existir um objeto para validar. Por isso o tratamento é aqui, e não no filtro de
        // validação: são dois problemas diferentes.
        if (exception is BadHttpRequestException or System.Text.Json.JsonException)
        {
            var badRequest = ProblemDetailsFactory.Create(
                Error.Validation(
                    "request.invalid_body",
                    "O corpo da requisição é inválido ou está malformado."),
                httpContext);

            // Warning, não Error: é o cliente que errou. Aparece no log (pode ser um front com
            // bug, e você quer saber), mas não dispara o alarme de 5xx.
            logger.LogWarning(
                "Corpo inválido em {Method} {Path}: {Reason}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                exception.Message);

            httpContext.Response.StatusCode = badRequest.Status!.Value;

            await httpContext.Response.WriteAsJsonAsync<ProblemDetails>(badRequest, cancellationToken);

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
