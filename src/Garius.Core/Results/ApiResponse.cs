namespace Garius.Core.Results;

/// <summary>
/// Envelope de <b>sucesso</b> de todo endpoint. Formato previsível para o frontend:
///
/// <code>
/// { "success": true, "data": { ... }, "traceId": "0af7651916cd43dd" }
/// </code>
///
/// Erros <b>não</b> usam este envelope — usam ProblemDetails (RFC 9457), que é o que
/// o ASP.NET, o Scalar e os geradores de client TypeScript entendem nativamente.
/// O <c>traceId</c> aparece nos dois, e é a chave para achar o log no Grafana.
/// </summary>
/// <typeparam name="T">Tipo do payload.</typeparam>
public sealed record ApiResponse<T>
{
    public bool Success => true;

    public required T Data { get; init; }

    /// <summary>Correlaciona esta resposta com os logs. Mesmo valor no ProblemDetails de erro.</summary>
    public required string TraceId { get; init; }

    public static ApiResponse<T> Ok(T data, string traceId) => new() { Data = data, TraceId = traceId };
}

/// <summary>
/// Envelope de sucesso sem payload (ex.: DELETE que retorna 200 sem corpo útil).
/// </summary>
public sealed record ApiResponse
{
    public bool Success => true;

    public required string TraceId { get; init; }

    public static ApiResponse Ok(string traceId) => new() { TraceId = traceId };
}

/// <summary>
/// Envelope de sucesso para listagens paginadas. Padroniza a paginação em todos os
/// endpoints de lista, para o frontend não ter que descobrir um formato novo a cada um.
/// </summary>
public sealed record PagedResponse<T>
{
    public bool Success => true;

    public required IReadOnlyList<T> Data { get; init; }

    public required PageInfo Page { get; init; }

    public required string TraceId { get; init; }

    public static PagedResponse<T> Ok(IReadOnlyList<T> data, PageInfo page, string traceId) =>
        new() { Data = data, Page = page, TraceId = traceId };
}

/// <param name="Number">Página atual, 1-based.</param>
/// <param name="Size">Itens por página.</param>
/// <param name="TotalItems">Total de itens em todas as páginas.</param>
public sealed record PageInfo(int Number, int Size, long TotalItems)
{
    public int TotalPages => Size <= 0 ? 0 : (int)Math.Ceiling(TotalItems / (double)Size);

    public bool HasNext => Number < TotalPages;

    public bool HasPrevious => Number > 1;
}
