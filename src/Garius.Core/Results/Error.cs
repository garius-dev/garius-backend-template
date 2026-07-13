namespace Garius.Core.Results;

/// <summary>
/// Um erro de negócio, com um código estável que o frontend pode usar para decidir
/// comportamento (e para traduzir mensagens), independente do texto exibido.
/// </summary>
/// <param name="Code">
/// Código estável em <c>recurso.motivo</c> (ex.: <c>user.email_already_taken</c>).
/// Faz parte do contrato com o frontend — mudar um código é breaking change.
/// </param>
/// <param name="Message">Mensagem legível. Pode mudar sem quebrar o contrato.</param>
/// <param name="Type">Categoria, que determina o status HTTP.</param>
/// <param name="Fields">
/// Erros por campo, quando <see cref="ErrorType.Validation"/>.
/// Chave = nome do campo em camelCase (como o JSON o envia).
/// </param>
public sealed record Error(
    string Code,
    string Message,
    ErrorType Type = ErrorType.Unexpected,
    IReadOnlyDictionary<string, string[]>? Fields = null)
{
    public static Error Validation(string code, string message) =>
        new(code, message, ErrorType.Validation);

    public static Error Validation(IReadOnlyDictionary<string, string[]> fields) =>
        new("validation.failed", "Um ou mais campos são inválidos.", ErrorType.Validation, fields);

    public static Error Unauthorized(string code = "auth.unauthorized", string message = "Não autenticado.") =>
        new(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code = "auth.forbidden", string message = "Acesso negado.") =>
        new(code, message, ErrorType.Forbidden);

    public static Error NotFound(string code, string message) =>
        new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) =>
        new(code, message, ErrorType.Conflict);

    public static Error BusinessRule(string code, string message) =>
        new(code, message, ErrorType.BusinessRule);

    public static Error TooManyRequests(string code = "rate_limit.exceeded", string message = "Requisições em excesso.") =>
        new(code, message, ErrorType.TooManyRequests);

    /// <summary>
    /// Falha inesperada. A mensagem é deliberadamente genérica: detalhes de exceção
    /// nunca vão para o cliente — vão para o log, correlacionados pelo traceId.
    /// </summary>
    public static Error Unexpected(string code = "server.unexpected", string message = "Ocorreu um erro inesperado.") =>
        new(code, message, ErrorType.Unexpected);
}
