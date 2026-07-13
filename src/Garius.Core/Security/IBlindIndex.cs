namespace Garius.Core.Security;

/// <summary>
/// Índice cego: um HMAC-SHA256 do valor normalizado, guardado numa coluna própria e
/// indexada. É o que devolve a capacidade de <b>busca exata</b> sobre um campo cifrado com
/// nonce aleatório.
///
/// <code>
/// // login continua funcionando, sem decifrar nada:
/// WHERE "EmailIndex" = @hmac_do_email_digitado
/// </code>
///
/// <para>
/// Por que HMAC e não um hash simples: um SHA-256 puro de um CPF é quebrável por força
/// bruta em segundos (só existem ~10^11 CPFs). O HMAC exige a chave secreta, que vive no
/// Secret Manager — sem ela, o índice não diz nada sobre o valor.
/// </para>
///
/// <para>
/// <b>O que o índice cego vaza, por construção:</b> igualdade. Dois registros com o mesmo
/// e-mail terão o mesmo índice. É o preço de poder buscar — e é aceitável, porque o índice
/// único já tornaria essa igualdade observável de qualquer forma.
/// </para>
/// </summary>
public interface IBlindIndex
{
    /// <summary>
    /// Calcula o índice do valor.
    ///
    /// <para>
    /// O <paramref name="scope"/> determina a <b>normalização</b>, e ela precisa ser idêntica
    /// na gravação e na busca: e-mail vira minúsculas, CPF e telefone perdem a máscara. Sem
    /// isso, <c>"123.456.789-01"</c> e <c>"12345678901"</c> gerariam índices diferentes — o
    /// mesmo CPF passaria duas vezes pelo índice único, e a busca não acharia o registro.
    /// </para>
    /// </summary>
    byte[] Compute(PiiScope scope, string value);
}
