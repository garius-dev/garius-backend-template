using Garius.Api.Infrastructure.Authorization;
using Garius.Core.Authorization;
using Garius.Core.Results;

namespace Garius.Api.Infrastructure.Errors;

/// <summary>
/// Endpoints que exercitam o contrato de resposta (envelope + ProblemDetails) de ponta a
/// ponta nos testes de integração.
///
/// <b>Nunca são mapeados em Production</b> — ver a chamada em Program.cs.
/// Não são exemplos a copiar: existem para provar que o contrato se sustenta.
/// </summary>
internal static class TestEndpoints
{
    internal static void MapTestEndpoints(this WebApplication app)
    {
        // AllowAnonymous: a FallbackPolicy exige autenticação em todo endpoint que não
        // declare o contrário. Estes existem só para exercitar o contrato de resposta.
        var group = app.MapGroup("/__test").ExcludeFromDescription().AllowAnonymous();

        // ⚠️ EXISTE PARA UM TESTE SÓ, e sem ele esse teste não é possível.
        //
        // O teste de encerramento gracioso (ShutdownE2ETests) precisa de uma requisição que
        // esteja EM VOO no instante do SIGTERM — é isso que ele prova que sobrevive. Com
        // endpoints instantâneos não há como criar a sobreposição: a requisição termina antes
        // de o sinal chegar, e o teste passaria sem exercitar a drenagem.
        //
        // O atraso é do CLIENTE (query string), não fixo, para o teste calibrá-lo sem
        // recompilar. E o Task.Delay respeita o CancellationToken de propósito: se o host
        // abortasse a requisição no shutdown, isto lançaria — que é exatamente a falha que se
        // quer detectar.
        group.MapGet("/slow", async (HttpContext http, int ms = 1000) =>
        {
            await Task.Delay(Math.Clamp(ms, 0, 30_000), http.RequestAborted);

            return Results.Ok(new { completed = true, delayMs = ms });
        });

        group.MapGet("/boom", IResult () =>
            throw new InvalidOperationException(
                "segredo-que-nao-pode-vazar: connection string, host, usuário do banco..."));

        group.MapGet("/not-found", (HttpContext http) =>
            Result<string>
                .Failure(Error.NotFound("user.not_found", "Usuário não encontrado."))
                .ToHttpResult(http));

        group.MapGet("/validation", (HttpContext http) =>
            Result<string>
                .Failure(Error.Validation(new Dictionary<string, string[]>
                {
                    ["email"] = ["E-mail já cadastrado."]
                }))
                .ToHttpResult(http));

        // Um endpoint protegido por PERMISSÃO, sem dizer nada sobre QUEM a tem.
        //
        // É o que prova, de ponta a ponta, a tese do desenho: um usuário (cookie), um client
        // OAuth (JWT) e uma chave de API (X-Api-Key) chegam aqui pelo MESMO .RequirePermission,
        // e a permissão decide sozinha. Sem um segundo vocabulário de escopos para máquina.
        var protectedGroup = app.MapGroup("/__test").ExcludeFromDescription();

        protectedGroup.MapGet("/protected", (HttpContext http) =>
            Result<object>
                .Success(new
                {
                    principal = http.User.Identity?.Name,
                    scheme = http.User.Identity?.AuthenticationType
                })
                .ToHttpResult(http))
        .RequirePermission(Permissions.Users.Read);

        // Um endpoint com EFEITO COLATERAL observável, para provar a idempotência.
        //
        // Sem um efeito contável, não haveria como distinguir "a requisição foi reexecutada e
        // deu o mesmo resultado" de "a requisição NÃO foi reexecutada" — que é exatamente o que
        // a idempotência promete. O contador é a diferença entre testar a promessa e testar
        // uma coincidência.
        group.MapPost("/side-effect", (HttpContext http) =>
        {
            var count = Interlocked.Increment(ref _sideEffectCount);

            return Result<object>.Success(new { executionCount = count }).ToHttpResult(http);
        });

        group.MapGet("/side-effect-count", (HttpContext http) =>
            Result<object>
                .Success(new { executionCount = Volatile.Read(ref _sideEffectCount) })
                .ToHttpResult(http));

        group.MapPost("/side-effect/reset", (HttpContext http) =>
        {
            Interlocked.Exchange(ref _sideEffectCount, 0);

            return Result<object>.Success(new { reset = true }).ToHttpResult(http);
        });

        // Sempre falha. Prova que um ERRO não é gravado como resposta idempotente — se fosse,
        // o cliente receberia o mesmo 500 por 24h, mesmo depois de o problema ter passado.
        group.MapPost("/side-effect/fail", (HttpContext http) =>
        {
            Interlocked.Increment(ref _sideEffectCount);

            return Result<object>
                .Failure(Error.BusinessRule("test.always_fails", "Este endpoint sempre falha."))
                .ToHttpResult(http);
        });
    }

    /// <summary>Contador de execuções. Estático porque a app é a mesma entre requisições.</summary>
    private static int _sideEffectCount;
}
