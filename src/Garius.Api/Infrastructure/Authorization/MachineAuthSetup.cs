using System.Text;
using Garius.Core.Authorization;
using Garius.Core.Machine;
using Garius.Infrastructure.Machine;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Garius.Api.Infrastructure.Authorization;

/// <summary>
/// Autenticação de <b>máquina</b>: JWT (OAuth2 client credentials) e chave de API.
///
/// <para>
/// Convivem com o cookie do usuário no mesmo pipeline. Qual esquema atende cada request é
/// decidido pelo <b>forwarder</b> registrado em <c>AddCookieAuthentication</c> — ver lá.
/// </para>
/// </summary>
internal static class MachineAuthSetup
{
    /// <summary>
    /// Policy para endpoints que só uma <b>máquina</b> pode chamar. Raramente necessária: o
    /// normal é o endpoint exigir uma <i>permissão</i>, e não se importar com quem a tem.
    /// </summary>
    internal const string MachineOnlyPolicy = "MachineOnly";

    /// <summary>
    /// Policy para endpoints que só uma <b>pessoa</b> pode chamar.
    ///
    /// <para>
    /// Existe para operações que não fazem sentido vindas de um sistema — trocar a própria
    /// senha, excluir a própria conta, aceitar termos de uso. Sem ela, um client OAuth com a
    /// permissão certa poderia executá-las, e a permissão sozinha não expressa "isto exige um
    /// humano".
    /// </para>
    /// </summary>
    internal const string HumanOnlyPolicy = "HumanOnly";

    internal static AuthenticationBuilder AddMachineAuthentication(
        this AuthenticationBuilder builder,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        builder.Services.AddSingleton<JwtIssuer>();
        builder.Services.AddScoped<MachineClientStore>();

        var options = new JwtOptions();
        configuration.GetSection(JwtOptions.SectionName).Bind(options);

        // Falha no BOOT se a chave não estiver configurada — nunca gera uma aleatória.
        // Ver JwtIssuer.BuildSigningKey para o porquê (tokens que morrem a cada restart e
        // réplicas que não aceitam os tokens umas das outras).
        var signingKey = JwtIssuer.BuildSigningKey(options);

        builder.AddJwtBearer(MachineAuth.BearerScheme, jwt =>
        {
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = options.Issuer,

                ValidateAudience = true,
                ValidAudience = options.Audience,

                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signingKey,

                ValidateLifetime = true,

                // Zero, não os 5 minutos do padrão.
                //
                // O ClockSkew existe para tolerar relógios dessincronizados entre servidores
                // diferentes. Aqui quem assina e quem valida é a MESMA aplicação, com o MESMO
                // relógio — a tolerância só faria um token expirado continuar valendo por mais
                // cinco minutos, o que é exatamente o que a vida curta do token tenta evitar.
                ClockSkew = TimeSpan.Zero,

                // O JWT carrega as permissões como claims `permission`, e o tipo de claim de
                // NOME precisa ser dito explicitamente — do contrário o handler procura os
                // nomes longos do WS-Federation e o principal sai sem identidade.
                NameClaimType = "sub",
                RoleClaimType = AppClaims.Permission
            };

            // O padrão do handler é remapear `sub` para o ClaimType longo do WS-Federation
            // (schemas.xmlsoap.org/...). Isso quebraria qualquer código que leia a claim pelo
            // nome que o JwtIssuer gravou. Manter os nomes como estão no token.
            jwt.MapInboundClaims = false;
        });

        builder.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
            MachineAuth.ApiKeyScheme,
            configureOptions: null);

        return builder;
    }

    /// <summary>
    /// Escolhe o esquema conforme o que o request <b>traz</b>: <c>Authorization: Bearer</c> →
    /// JWT; <c>X-Api-Key</c> → chave de API; qualquer outra coisa → o cookie do usuário.
    ///
    /// <para>
    /// Sem este forwarder, o esquema padrão (cookie) atenderia <b>tudo</b>: um request com um
    /// JWT perfeitamente válido chegaria à autorização como <b>anônimo</b>, porque o handler de
    /// cookie não procura no header <c>Authorization</c> — e o 401 resultante não teria
    /// nenhuma explicação visível.
    /// </para>
    /// </summary>
    internal static string SelectScheme(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.Headers.ContainsKey(MachineAuth.ApiKeyHeader))
        {
            return MachineAuth.ApiKeyScheme;
        }

        var authorization = context.Request.Headers.Authorization.ToString();

        if (authorization.StartsWith(
                $"{MachineAuth.BearerScheme} ", StringComparison.OrdinalIgnoreCase))
        {
            return MachineAuth.BearerScheme;
        }

        return CookieAuthSetup.SchemeName;
    }
}
