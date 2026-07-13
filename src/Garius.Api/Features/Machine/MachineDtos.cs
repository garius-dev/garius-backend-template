using System.Text.Json.Serialization;
using Garius.Core.Machine;

namespace Garius.Api.Features.Machine;

/// <summary>
/// Requisição de token (RFC 6749, §4.4). Os nomes dos campos são <c>snake_case</c> porque a
/// RFC os define assim — um cliente OAuth genérico envia exatamente estes nomes, e renomeá-los
/// para o camelCase do resto da API quebraria toda biblioteca padrão.
/// </summary>
public sealed record TokenRequest(
    [property: JsonPropertyName("grant_type")] string GrantType,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret")] string ClientSecret);

/// <summary>
/// Resposta de token (RFC 6749, §5.1).
///
/// <para>
/// <b>Esta resposta não vai dentro do envelope padrão da API.</b> A RFC define o formato
/// exato — <c>access_token</c>, <c>token_type</c>, <c>expires_in</c> — e é o que qualquer
/// cliente OAuth espera encontrar na raiz do JSON. Embrulhá-la em <c>{ data: ... }</c> a
/// tornaria ilegível para toda biblioteca padrão, e o ganho de consistência seria só estético.
/// É a única exceção deliberada ao contrato de resposta.
/// </para>
/// </summary>
public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("scope")] string Scope);

/// <summary>Erro de token (RFC 6749, §5.2) — também fora do envelope, pela mesma razão.</summary>
public sealed record TokenErrorResponse(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description")] string ErrorDescription);

// --- administração -----------------------------------------------------------

/// <param name="Name">Para que serve este client. Aparece na administração.</param>
/// <param name="Scopes">Permissões do catálogo (<c>invoices.read</c>). Validadas na criação.</param>
/// <param name="ExpiresAt">Quando o client deixa de valer. <c>null</c> = não expira.</param>
public sealed record CreateOAuthClientRequest(
    string Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt = null);

/// <summary>
/// A criação de um client — <b>a única vez</b> em que o segredo existe em claro.
///
/// <para>
/// Não há como recuperá-lo depois: o banco guarda apenas o hash. Perdeu, gera outro. É a mesma
/// disciplina de uma senha, e é o que faz um vazamento do banco não entregar credenciais
/// utilizáveis.
/// </para>
/// </summary>
public sealed record CreateOAuthClientResponse(
    Guid Id,
    string ClientId,
    string ClientSecret,
    string Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt);

/// <param name="Name">De quem é a chave / para que serve.</param>
/// <param name="Scopes">Permissões do catálogo. Uma chave de terceiro quase sempre é só leitura.</param>
/// <param name="CallLimit">Teto TOTAL de chamadas (quota), não uma taxa. <c>null</c> = sem teto.</param>
/// <param name="ExpiresAt">Quando a chave deixa de valer. <c>null</c> = não expira.</param>
public sealed record CreateApiKeyRequest(
    string Name,
    IReadOnlyList<string> Scopes,
    long? CallLimit = null,
    DateTimeOffset? ExpiresAt = null);

/// <summary>Como no client OAuth: a chave em claro só aparece aqui, uma única vez.</summary>
public sealed record CreateApiKeyResponse(
    Guid Id,
    string Key,
    string Name,
    IReadOnlyList<string> Scopes,
    long? CallLimit,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Um client na listagem. <b>Sem o segredo</b> — ele não existe mais em lugar nenhum, e é essa
/// a intenção.
/// </summary>
public sealed record OAuthClientDto(
    Guid Id,
    string ClientId,
    string Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt);

/// <summary>
/// Uma chave na listagem. Mostra o <b>prefixo</b> (<c>gk_a1b2c3d4…</c>), que é o que permite
/// identificá-la para revogar — ver <see cref="ApiKey.KeyPrefix"/>.
/// </summary>
public sealed record ApiKeyDto(
    Guid Id,
    string KeyPrefix,
    string Name,
    IReadOnlyList<string> Scopes,
    long? CallLimit,
    long CallCount,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt);
