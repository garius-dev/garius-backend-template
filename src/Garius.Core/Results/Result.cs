using System.Diagnostics.CodeAnalysis;

namespace Garius.Core.Results;

/// <summary>
/// Resultado de uma operação: sucesso ou <see cref="Error"/>.
///
/// Erro de negócio é um <b>valor de retorno</b>, não uma exceção. Exceção fica reservada
/// para bug/falha de infra. Isso é o que mantém o log limpo: só o que é realmente
/// inesperado vira LogError com stack trace.
/// </summary>
public class Result
{
    protected Result(Error? error) => Error = error;

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    public static Result Success() => new(null);

    public static Result Failure(Error error) => new(error);

    public static implicit operator Result(Error error) => Failure(error);
}

/// <inheritdoc cref="Result"/>
/// <typeparam name="T">Tipo do valor em caso de sucesso.</typeparam>
public sealed class Result<T> : Result
{
    private readonly T _value;

    private Result(T value) : base(null) => _value = value;

    private Result(Error error) : base(error) => _value = default!;

    /// <summary>
    /// Valor do sucesso. Lança se acessado num resultado de falha — sempre cheque
    /// <see cref="Result.IsSuccess"/> antes.
    /// </summary>
    public T Value => IsSuccess
        ? _value
        : throw new InvalidOperationException(
            $"Não é possível acessar Value de um Result com falha ({Error!.Code}).");

    public static Result<T> Success(T value) => new(value);

    public static new Result<T> Failure(Error error) => new(error);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);
}
