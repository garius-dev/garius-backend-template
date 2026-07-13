using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Garius.Core.Authorization;
using Garius.Core.Machine;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Garius.Infrastructure.Machine;

/// <summary>
/// Emite o JWT de um client OAuth2. É todo o "servidor de identidade" de que precisamos —
/// ver <see cref="OAuthClient"/> para o porquê de não haver um Duende aqui.
/// </summary>
public sealed class JwtIssuer
{
    private readonly JwtOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SigningCredentials _credentials;

    public JwtIssuer(IOptions<JwtOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _timeProvider = timeProvider;

        _credentials = new SigningCredentials(
            BuildSigningKey(_options),
            SecurityAlgorithms.HmacSha256);
    }

    /// <summary>
    /// Constrói a chave de assinatura, <b>falhando no boot</b> se ela não estiver configurada
    /// ou for curta demais.
    ///
    /// <para>
    /// É deliberado que a aplicação <b>não suba</b> nesse caso. A alternativa — gerar uma chave
    /// aleatória em memória quando falta configuração — parece conveniente e é a pior opção
    /// possível: os tokens deixariam de valer a cada restart, e cada réplica assinaria com uma
    /// chave diferente (um token emitido por uma seria rejeitado pela outra). O erro
    /// apareceria como falhas intermitentes de autenticação em produção, não como o que
    /// realmente é.
    /// </para>
    /// </summary>
    public static SymmetricSecurityKey BuildSigningKey(JwtOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SigningKey))
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey não está configurada. Gere uma com `openssl rand -base64 32` e " +
                "grave-a no Secret Manager. A aplicação não sobe sem ela — de propósito.");
        }

        byte[] key;

        try
        {
            key = Convert.FromBase64String(options.SigningKey);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Jwt:SigningKey precisa estar em base64.", ex);
        }

        // HMAC-SHA256 com uma chave menor que o próprio digest é fraco, e a biblioteca a
        // rejeitaria em runtime — na primeira emissão, não no boot. Melhor aqui.
        if (key.Length < 32)
        {
            throw new InvalidOperationException(
                $"Jwt:SigningKey tem {key.Length} bytes; o mínimo para HMAC-SHA256 é 32.");
        }

        return new SymmetricSecurityKey(key);
    }

    /// <summary>
    /// O JWT do client, com os escopos <b>dentro</b> dele.
    ///
    /// <para>
    /// Ao contrário do cookie de usuário — onde as permissões estourariam o limite de tamanho
    /// do navegador e por isso ficam fora (ver <c>LeanClaimsPrincipalFactory</c>) — aqui elas
    /// entram. Um client tem uma lista curta e fixa de escopos, e nenhum navegador no caminho.
    /// É o que torna a autorização de máquina <b>stateless</b>: nenhum SELECT por request.
    /// </para>
    /// </summary>
    public MachineToken Issue(OAuthClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        var now = _timeProvider.GetUtcNow();
        var expiresAt = now.AddMinutes(_options.LifetimeMinutes);

        var claims = new List<Claim>
        {
            // `sub` é o client, não um usuário. Um endpoint que leia NameIdentifier esperando
            // um Guid de usuário vai encontrar o client_id — e é por isso que existe a claim
            // client_type: para que seja possível distinguir os dois deliberadamente.
            new(JwtRegisteredClaimNames.Sub, client.ClientId),

            // `jti` — id único do token. Não o usamos para revogação (o JWT é stateless por
            // opção), mas ele é o que torna um token rastreável nos logs.
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),

            new(MachineAuth.ClientTypeClaim, MachineAuth.OAuthClientType),

            // O tenant vai no token. A partir daqui o ClaimsTenantResolver trata a máquina
            // exatamente como trata um usuário — e o global query filter também. É o que faz
            // um client não conseguir ler dados de outro tenant, por construção.
            new(AppClaims.TenantId, client.TenantId.ToString()),

            new("client_name", client.Name)
        };

        // Uma claim de permissão por escopo — o MESMO tipo de claim que um usuário teria.
        // É isso que permite ao PermissionAuthorizationHandler servir os dois com um só código.
        claims.AddRange(client.Scopes.Select(scope => new Claim(AppClaims.Permission, scope)));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: _credentials);

        var encoded = new JwtSecurityTokenHandler().WriteToken(token);

        return new MachineToken(
            encoded,
            (int)(expiresAt - now).TotalSeconds,
            string.Join(' ', client.Scopes));
    }
}

/// <summary>
/// O token emitido, no formato da RFC 6749 (§5.1) — é o que um cliente OAuth genérico
/// espera encontrar, e o que as bibliotecas de todos os ecossistemas já sabem ler.
/// </summary>
/// <param name="AccessToken">O JWT.</param>
/// <param name="ExpiresInSeconds">Vida restante, em segundos.</param>
/// <param name="Scope">Os escopos concedidos, separados por espaço (como manda a RFC).</param>
public sealed record MachineToken(string AccessToken, int ExpiresInSeconds, string Scope)
{
    /// <summary>Sempre <c>Bearer</c>. Faz parte do contrato da RFC.</summary>
    public string TokenType => MachineAuth.BearerScheme;

    public string ExpiresInSecondsText =>
        ExpiresInSeconds.ToString(CultureInfo.InvariantCulture);
}
