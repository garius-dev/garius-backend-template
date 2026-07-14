using FluentValidation;
using FluentValidation.Results;
using Garius.Api.Infrastructure.Errors;
using Garius.Core.Results;

namespace Garius.Api.Infrastructure.Validation;

/// <summary>
/// Valida o corpo da requisição <b>antes</b> do handler, e devolve 400 no contrato da API.
///
/// <para>
/// <b>É automático, e isso é o ponto.</b> O filtro descobre o <c>IValidator&lt;T&gt;</c> do tipo
/// do request no DI. Existe um? Ele roda. Não existe? Passa direto. Você escreve o validator e
/// pronto — <b>não há uma chamada para esquecer</b>.
/// </para>
///
/// <para>
/// É a mesma filosofia do resto do template: a <c>FallbackPolicy</c> fecha o endpoint que você
/// esqueceu de proteger, o build quebra no warning que você ignorou, o boot morre na configuração
/// que faltou. <b>Esquecer não pode ser uma opção silenciosa.</b> Um endpoint novo cujo validator
/// existe é validado, mesmo que quem o escreveu não saiba que este filtro existe.
/// </para>
///
/// <para>
/// ⚠️ <b>Em Minimal API, Data Annotations (<c>[Required]</c>, <c>[MaxLength]</c>) NÃO fazem
/// nada.</b> Não existe o <c>[ApiController]</c> do MVC, que era quem ligava a validação
/// automática — as annotations viram decoração: <i>parecem</i> proteger, e não protegem. Este
/// filtro é o que ocupa esse lugar.
/// </para>
///
/// <para>
/// <b>Roda DEPOIS da autorização</b> (é um endpoint filter, e o pipeline já resolveu
/// <c>UseAuthorization</c> quando chega aqui). É o que permite um validator ir ao BANCO com
/// segurança: um anônimo é barrado com 401 <b>antes</b> de disparar qualquer query. Se rodasse
/// antes, validar viraria um vetor de DoS — qualquer um faria a API consultar o banco de graça.
/// </para>
/// </summary>
internal sealed class ValidationFilter<TRequest>(IValidator<TRequest> validator) : IEndpointFilter
    where TRequest : class
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();

        if (request is null)
        {
            // O argumento não veio (rota sem corpo, ou o binder já falhou). Não é papel deste
            // filtro decidir isso — segue, e quem trata é o GlobalExceptionHandler.
            return await next(context);
        }

        var result = await validator.ValidateAsync(request, context.HttpContext.RequestAborted);

        if (result.IsValid)
        {
            return await next(context);
        }

        // O handler NÃO é chamado. Nada de "validou e executou mesmo assim".
        return Results.Json(
            ProblemDetailsFactory.Create(ToError(result), context.HttpContext),
            statusCode: StatusCodes.Status400BadRequest,
            contentType: "application/problem+json");
    }

    /// <summary>
    /// Agrupa as falhas <b>por campo</b> — e devolve TODAS de uma vez.
    ///
    /// <para>
    /// Não é detalhe de estilo: um erro por vez faz o usuário corrigir o nome, reenviar,
    /// descobrir que o preço também estava errado, corrigir, reenviar... O front precisa de
    /// <c>{ campo: [mensagens] }</c> para pintar de vermelho todos os campos errados de uma vez.
    /// É o formato que o <see cref="Error.Validation(IReadOnlyDictionary{string, string[]})"/>
    /// já esperava desde a Fase 1 — este filtro é quem finalmente o preenche.
    /// </para>
    ///
    /// <para>
    /// A chave vai em <b>camelCase</b>, como o JSON a envia: o front recebeu <c>"productId"</c>,
    /// e é <c>"productId"</c> que ele tem de achar aqui — não <c>"ProductId"</c>, que é o nome
    /// da propriedade em C# e não significa nada do lado de lá.
    /// </para>
    /// </summary>
    private static Error ToError(ValidationResult result)
    {
        var fields = result.Errors
            .GroupBy(failure => ToCamelCase(failure.PropertyName))
            .ToDictionary(
                group => group.Key,
                group => group.Select(failure => failure.ErrorMessage).ToArray(),
                StringComparer.Ordinal);

        return Error.Validation(fields);
    }

    private static string ToCamelCase(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return propertyName;
        }

        // Propriedade aninhada ("Items[0].ProductId") — cada segmento vira camelCase.
        var segments = propertyName.Split('.');

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];

            if (segment.Length > 0 && char.IsUpper(segment[0]))
            {
                segments[i] = char.ToLowerInvariant(segment[0]) + segment[1..];
            }
        }

        return string.Join('.', segments);
    }
}
