using System.Text;
using System.Text.Json;
using Garius.Api.Infrastructure.Errors;
using Garius.Infrastructure.Idempotency;

namespace Garius.Api.Infrastructure.Idempotency;

/// <summary>
/// Torna uma requisição <b>repetível sem duplicar efeito</b>, quando o cliente manda um
/// <c>Idempotency-Key</c>.
///
/// <para>
/// <b>É opt-in, e isso é deliberado.</b> Sem a chave, a requisição passa direto — o middleware
/// não inventa uma chave a partir do corpo, do usuário ou da rota. Uma chave inferida
/// transformaria duas operações <b>legitimamente iguais</b> (comprar o mesmo item duas vezes,
/// de propósito) numa só, silenciosamente. Só o cliente sabe se duas requisições idênticas são
/// a mesma intenção ou duas intenções — então é ele que decide, mandando (ou não) a chave.
/// </para>
/// </summary>
internal sealed class IdempotencyMiddleware(
    RequestDelegate next,
    RedisIdempotencyStore store,
    ILogger<IdempotencyMiddleware> logger)
{
    internal const string HeaderName = "Idempotency-Key";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!TryGetKey(context, out var key))
        {
            await next(context);

            return;
        }

        var reservation = await store.TryReserveAsync(key, context.RequestAborted);

        switch (reservation.State)
        {
            case IdempotencyState.Completed:
                await ReplayAsync(context, reservation.Response!, key);

                return;

            case IdempotencyState.InProgress:
                await ConflictAsync(context, key);

                return;

            default:
                await ExecuteAndCaptureAsync(context, key);

                return;
        }
    }

    /// <summary>
    /// Executa a requisição de verdade e <b>captura a resposta</b> para gravá-la.
    /// </summary>
    private async Task ExecuteAndCaptureAsync(HttpContext context, string key)
    {
        var originalBody = context.Response.Body;

        // O Response.Body é um stream de escrita direta para a rede: depois de escrito, o
        // conteúdo já foi embora e não há como lê-lo. Trocá-lo por um MemoryStream é o que
        // permite capturá-lo — e é preciso RESTAURAR o original no finally, ou a resposta
        // nunca chegaria ao cliente.
        using var buffer = new MemoryStream();

        context.Response.Body = buffer;

        try
        {
            await next(context);

            buffer.Seek(0, SeekOrigin.Begin);

            var body = await new StreamReader(buffer).ReadToEndAsync(context.RequestAborted);

            if (IsSuccess(context.Response.StatusCode))
            {
                await store.CompleteAsync(
                    key, context.Response.StatusCode, body, context.RequestAborted);
            }
            else
            {
                // FALHOU: libera a reserva. Gravar um erro como resposta idempotente
                // envenenaria a chave — o cliente receberia o mesmo 500 por 24h, mesmo depois
                // de o problema ter sido resolvido. Ver RedisIdempotencyStore.ReleaseAsync.
                await store.ReleaseAsync(key, context.RequestAborted);
            }

            buffer.Seek(0, SeekOrigin.Begin);

            context.Response.Body = originalBody;

            await buffer.CopyToAsync(originalBody, context.RequestAborted);
        }
        catch
        {
            // Uma exceção que escapa também é uma falha: a reserva não pode ficar de pé, ou a
            // operação fica travada até o TTL.
            await store.ReleaseAsync(key, CancellationToken.None);

            context.Response.Body = originalBody;

            throw;
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    /// <summary>Devolve a resposta guardada — <b>sem reexecutar nada</b>.</summary>
    private async Task ReplayAsync(HttpContext context, StoredResponse stored, string key)
    {
        logger.LogInformation(
            "Idempotência: requisição repetida devolvida do cache (chave {Key}, status {Status}). " +
            "Nada foi reexecutado.",
            key, stored.StatusCode);

        context.Response.StatusCode = stored.StatusCode;
        context.Response.ContentType = "application/json";

        // Diz ao cliente que ele está vendo um replay, e não uma execução nova. Sem isto, um
        // integrador depurando um retry não teria como distinguir as duas coisas.
        context.Response.Headers["Idempotency-Replayed"] = "true";

        await context.Response.WriteAsync(stored.Body, context.RequestAborted);
    }

    /// <summary>
    /// Uma requisição <b>idêntica ainda está executando</b>. Não há resposta para devolver, e
    /// executar de novo duplicaria o efeito — que é justamente o que a chave existe para evitar.
    /// </summary>
    private static async Task ConflictAsync(HttpContext context, string key)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            type = "https://tools.ietf.org/html/rfc9110#section-15.5.10",
            title = "Requisição em andamento",
            status = StatusCodes.Status409Conflict,
            detail = "Uma requisição com este Idempotency-Key ainda está sendo processada. " +
                     "Tente novamente em instantes.",
            code = "idempotency.in_progress",
            // O MESMO traceId de todo o resto da API — ver ProblemDetailsFactory.GetTraceId.
            traceId = ProblemDetailsFactory.GetTraceId(context)
        }), context.RequestAborted);
    }

    /// <summary>
    /// Só faz sentido em métodos que <b>alteram estado</b> — e só quando o cliente pede.
    /// Um GET já é idempotente por definição do próprio HTTP.
    /// </summary>
    private static bool TryGetKey(HttpContext context, out string key)
    {
        key = string.Empty;

        if (HttpMethods.IsGet(context.Request.Method)
            || HttpMethods.IsHead(context.Request.Method)
            || HttpMethods.IsOptions(context.Request.Method))
        {
            return false;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return false;
        }

        var value = values.ToString();

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // A chave vai compor uma chave do Redis. Um valor gigante vindo do cliente é entrada
        // não confiável — truncar evita que alguém encha o Redis com uma chave de 1 MB.
        key = value.Length > 128 ? value[..128] : value;

        return true;
    }

    private static bool IsSuccess(int statusCode) => statusCode is >= 200 and < 300;
}

/// <summary>
/// Um <see cref="MemoryStream"/> como <c>Response.Body</c> tem um efeito colateral: o
/// <c>Content-Length</c> pode ser calculado sobre ele. Não é problema aqui porque o corpo é
/// copiado inteiro para o stream original antes de a resposta ser finalizada.
/// </summary>
internal static class IdempotencyExtensions
{
    internal static IApplicationBuilder UseIdempotency(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<IdempotencyMiddleware>();
    }
}
