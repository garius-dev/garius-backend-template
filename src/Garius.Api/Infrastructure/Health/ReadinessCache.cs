namespace Garius.Api.Infrastructure.Health;

/// <summary>
/// Guarda a resposta do <c>/health/ready</c> por alguns segundos.
///
/// <para>
/// <b>Sem isto, o health check vira carga — e justamente na hora errada.</b> O kubelet chama o
/// readiness de cada pod a cada poucos segundos. Com 10 réplicas e um período de 5s, são duas
/// consultas por segundo ao Postgres e ao Redis <i>só de monitoramento</i>. Quando o banco
/// está sofrendo — que é exatamente quando o readiness importa — esse tráfego extra <b>piora</b>
/// o problema que ele deveria estar apenas observando.
/// </para>
///
/// <para>
/// A janela é curta de propósito. Ela precisa ser bem menor que o <c>periodSeconds</c> ×
/// <c>failureThreshold</c> da probe, senão o pod demoraria a sair do balanceamento quando
/// adoecesse de verdade. Alguns segundos absorvem a rajada de chamadas sem atrasar
/// perceptivelmente a detecção.
/// </para>
///
/// <para>
/// <b>Não cacheia o resultado do shutdown.</b> Não precisa: o <see cref="ShutdownHealthCheck"/>
/// só lê uma flag em memória. Mas o cache <i>é invalidado</i> no encerramento
/// (<see cref="Invalidate"/>), porque um readiness cacheado como "pronto" atrasaria em
/// segundos a saída do balanceamento — que é o único motivo de tudo isto existir.
/// </para>
/// </summary>
internal sealed class ReadinessCache(TimeProvider? timeProvider = null)
{
    /// <summary>
    /// Quanto tempo a resposta vale. Curto: é para absorver rajada, não para esconder falha.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();

    private DateTimeOffset _cachedAt;
    private CachedResponse? _cached;

    /// <summary>A resposta ainda válida, ou <c>null</c> se expirou (ou nunca houve uma).</summary>
    internal CachedResponse? TryGet()
    {
        lock (_gate)
        {
            if (_cached is null || _time.GetUtcNow() - _cachedAt > Window)
            {
                return null;
            }

            return _cached;
        }
    }

    internal void Store(int statusCode, string body)
    {
        lock (_gate)
        {
            _cached = new CachedResponse(statusCode, body);
            _cachedAt = _time.GetUtcNow();
        }
    }

    /// <summary>
    /// Descarta o que estiver guardado. Chamado no encerramento: sem isso, uma resposta
    /// "pronto" cacheada continuaria atraindo tráfego por até <see cref="Window"/> depois do
    /// <c>SIGTERM</c>.
    /// </summary>
    internal void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    internal sealed record CachedResponse(int StatusCode, string Body);
}

/// <summary>
/// Aplica o <see cref="ReadinessCache"/> ao endpoint de readiness: devolve a resposta guardada
/// quando há uma, e guarda a nova quando não há.
/// </summary>
internal sealed class ReadinessCacheFilter(ReadinessCache cache, ShutdownState shutdown)
    : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;

        // Encerrando: NUNCA responde do cache. A resposta precisa refletir o shutdown na
        // primeira chamada — é o ponto inteiro do ShutdownState.
        if (shutdown.IsShuttingDown)
        {
            cache.Invalidate();

            return await next(context);
        }

        if (cache.TryGet() is { } hit)
        {
            http.Response.StatusCode = hit.StatusCode;
            http.Response.ContentType = "text/plain";

            await http.Response.WriteAsync(hit.Body, http.RequestAborted);

            return null;
        }

        // Captura o que o health check escreveu, para poder guardá-lo. O corpo é pequeno
        // (o nome do status), então o buffer em memória não é preocupação.
        var original = http.Response.Body;

        using var buffer = new MemoryStream();

        http.Response.Body = buffer;

        try
        {
            var result = await next(context);

            buffer.Position = 0;

            var body = await new StreamReader(buffer).ReadToEndAsync(http.RequestAborted);

            cache.Store(http.Response.StatusCode, body);

            http.Response.Body = original;

            await http.Response.WriteAsync(body, http.RequestAborted);

            return result;
        }
        finally
        {
            // Restaura sempre: deixar o MemoryStream no lugar do corpo real vazaria para
            // qualquer coisa que ainda escrevesse na resposta.
            http.Response.Body = original;
        }
    }
}
