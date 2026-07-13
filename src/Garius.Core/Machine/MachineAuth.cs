namespace Garius.Core.Machine;

/// <summary>
/// Nomes e constantes compartilhados pelos dois fluxos de máquina. Definidos <b>uma vez</b>:
/// o mesmo nome é gravado no token e lido na autorização, e duas definições que divergem
/// produzem o pior tipo de bug — o principal autentica, mas não tem permissão nenhuma, e
/// ninguém entende por quê.
/// </summary>
public static class MachineAuth
{
    /// <summary>Esquema de autenticação do JWT emitido para clients OAuth.</summary>
    public const string BearerScheme = "Bearer";

    /// <summary>Esquema de autenticação das chaves de API.</summary>
    public const string ApiKeyScheme = "ApiKey";

    /// <summary>Header em que a chave de API é enviada.</summary>
    public const string ApiKeyHeader = "X-Api-Key";

    /// <summary>
    /// Claim que marca o principal como <b>uma máquina, não uma pessoa</b>. Vale
    /// <c>oauth_client</c> ou <c>api_key</c>.
    ///
    /// <para>
    /// É o que permite a um endpoint sensível recusar uma máquina mesmo que ela tenha a
    /// permissão (por exemplo: "excluir a conta" só faz sentido vindo de um humano logado). Sem
    /// esta claim, não haveria como distinguir — um principal com <c>users.delete</c> é um
    /// principal com <c>users.delete</c>.
    /// </para>
    /// </summary>
    public const string ClientTypeClaim = "client_type";

    /// <summary>Valor de <see cref="ClientTypeClaim"/> para um client OAuth (JWT).</summary>
    public const string OAuthClientType = "oauth_client";

    /// <summary>Valor de <see cref="ClientTypeClaim"/> para uma chave de API.</summary>
    public const string ApiKeyType = "api_key";

    /// <summary>
    /// O único <c>grant_type</c> suportado. Não implementamos <c>password</c>,
    /// <c>authorization_code</c> nem <c>implicit</c> — os dois últimos exigem um servidor de
    /// identidade de verdade, e o primeiro é desaconselhado pelo próprio OAuth 2.1.
    /// </summary>
    public const string ClientCredentialsGrant = "client_credentials";
}
