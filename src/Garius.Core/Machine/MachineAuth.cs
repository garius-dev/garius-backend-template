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

    /// <summary>
    /// Header <b>alternativo</b> da chave de API. O caminho principal é
    /// <c>Authorization: Bearer gk_...</c> — ver <see cref="ExtractCredential"/>.
    ///
    /// <para>
    /// Continua aceito porque é inequívoco e porque quebrar um integrador que já o usa não traz
    /// benefício nenhum.
    /// </para>
    /// </summary>
    public const string ApiKeyHeader = "X-Api-Key";

    /// <summary>Prefixo do esquema no header <c>Authorization</c>, com o espaço.</summary>
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Extrai a credencial de máquina do request e diz <b>o que ela é</b>.
    ///
    /// <para>
    /// <b>Por que uma chave de API viaja no <c>Authorization: Bearer</c>.</b> É o que o mercado
    /// faz (Stripe, OpenAI, GitHub): o integrador não quer aprender um header proprietário, e
    /// toda biblioteca HTTP já tem um jeito pronto de mandar um Bearer. O <c>X-Api-Key</c>
    /// continua funcionando, mas deixou de ser o caminho recomendado.
    /// </para>
    ///
    /// <para>
    /// <b>E como se distingue uma chave de um JWT no MESMO header?</b> Não é heurística: uma
    /// chave de API <b>sempre</b> começa com <see cref="MachineCredential.ApiKeyPrefix"/>
    /// (<c>gk_</c>), e um JWT <b>nunca</b> pode — ele é base64url de um JSON que começa
    /// obrigatoriamente com <c>{</c>, o que sempre produz <c>ey...</c>. Os dois espaços são
    /// disjuntos por construção, e o prefixo existe desde o início (para que scanners de segredo
    /// reconheçam a chave vazada num commit).
    /// </para>
    ///
    /// <para>
    /// ⚠️ Esta decisão vive AQUI, e num lugar só. O forwarder de esquema, o handler da chave e o
    /// bypass de CSRF têm de concordar sobre o que é o quê — se divergirem, o request autentica
    /// por um esquema e é autorizado por outro, que é a classe de bug mais difícil de enxergar
    /// que existe neste arquivo.
    /// </para>
    /// </summary>
    public static MachineCredentialKind ExtractCredential(
        string? authorizationHeader,
        string? apiKeyHeader,
        out string credential)
    {
        // O header dedicado vence: quem o manda está sendo explícito.
        if (!string.IsNullOrWhiteSpace(apiKeyHeader))
        {
            credential = apiKeyHeader.Trim();

            return MachineCredentialKind.ApiKey;
        }

        if (!string.IsNullOrWhiteSpace(authorizationHeader)
            && authorizationHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var value = authorizationHeader[BearerPrefix.Length..].Trim();

            credential = value;

            return value.StartsWith(MachineCredential.ApiKeyPrefix, StringComparison.Ordinal)
                ? MachineCredentialKind.ApiKey
                : MachineCredentialKind.Jwt;
        }

        credential = string.Empty;

        return MachineCredentialKind.None;
    }

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
