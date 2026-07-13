namespace Garius.Core.Results;

/// <summary>
/// Categoria de um erro de negócio. A camada de API traduz cada categoria para um
/// status HTTP — o Core não conhece HTTP.
/// </summary>
public enum ErrorType
{
    /// <summary>Entrada inválida. → 400</summary>
    Validation,

    /// <summary>Não autenticado. → 401</summary>
    Unauthorized,

    /// <summary>Autenticado, mas sem permissão. → 403</summary>
    Forbidden,

    /// <summary>Recurso inexistente. → 404</summary>
    NotFound,

    /// <summary>Conflito com o estado atual (ex.: e-mail já cadastrado). → 409</summary>
    Conflict,

    /// <summary>Regra de negócio violada. → 422</summary>
    BusinessRule,

    /// <summary>Excesso de requisições. → 429</summary>
    TooManyRequests,

    /// <summary>Falha inesperada. → 500</summary>
    Unexpected
}
