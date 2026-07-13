using Garius.Core.Identity;
using Garius.Infrastructure.Database;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Garius.Infrastructure.Identity;

public static class IdentityExtensions
{
    /// <summary>
    /// Registra o ASP.NET Core Identity com o e-mail criptografado.
    ///
    /// <para>
    /// A única peça fora do padrão é o <see cref="BlindIndexLookupNormalizer"/>, que faz o
    /// <c>NormalizedEmail</c> guardar o índice cego em vez do e-mail. Com ele, o
    /// <c>UserManager</c> inteiro (<c>FindByEmailAsync</c>, <c>CreateAsync</c>,
    /// <c>CheckPasswordAsync</c>) funciona <b>sem nenhuma outra adaptação</b>.
    /// </para>
    /// </summary>
    /// <param name="services">O contêiner.</param>
    /// <param name="forBootstrap">
    /// <c>true</c> no modo <c>MIGRATE_ONLY</c>. O bootstrap precisa do <c>UserManager</c> — é
    /// ele que faz o hash da senha e grava o índice cego do e-mail ao criar o primeiro
    /// administrador —, mas <b>não</b> das peças que dependem de infraestrutura que ali não
    /// existe:
    ///
    /// <list type="bullet">
    ///   <item><c>AddDefaultTokenProviders()</c> exige o <c>IDataProtectionProvider</c>, que é
    ///         registrado no <c>AddRedis</c> (só no runtime). Serve para tokens de reset de
    ///         senha e confirmação de e-mail — que o bootstrap não emite.</item>
    ///   <item><c>RedisRefreshTokenStore</c> exige o <c>IConnectionMultiplexer</c>, idem. O
    ///         bootstrap não autentica ninguém, logo não emite refresh token.</item>
    /// </list>
    ///
    /// <para>
    /// Registrá-las no bootstrap faz o <c>builder.Build()</c> estourar na validação do
    /// container — e o <c>MIGRATE_ONLY</c> volta a não rodar. É o mesmo tipo de armadilha do
    /// <c>ICurrentUser</c>, e o <c>MigrateOnlyBootTests</c> a pega.
    /// </para>
    /// </param>
    public static IServiceCollection AddApplicationIdentity(
        this IServiceCollection services,
        bool forBootstrap = false)
    {
        ArgumentNullException.ThrowIfNull(services);

        // O DataProtection (que cifra o cookie e os tokens do Identity) é registrado em
        // AddRedis, com o keyring NO REDIS — sem isso, duas réplicas não conseguem ler o
        // cookie uma da outra.

        var identity = services.AddIdentityCore<ApplicationUser>(options =>
        {
            // Senha: o comprimento faz muito mais pela entropia do que a exigência de
            // símbolos, que só produz "Senha@123" — previsível e fácil de quebrar.
            options.Password.RequiredLength = 12;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;

            // O lockout do Identity é por conta (protege contra brute force numa conta).
            // NÃO substitui o rate limit por IP (que protege contra password spraying):
            // são duas dimensões independentes, e a Fase 4c implementa a segunda.
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.AllowedForNewUsers = true;

            // O UserName aqui é o Id em texto (identificador opaco), não o e-mail.
            options.User.RequireUniqueEmail = false;

            // Não usamos o e-mail confirmado do Identity: a coluna Email não existe (o dado
            // vive cifrado em EmailPii). A confirmação de e-mail é implementada por fora.
            options.SignIn.RequireConfirmedEmail = false;
            options.SignIn.RequireConfirmedAccount = false;
        })
        .AddRoles<ApplicationRole>()
        .AddEntityFrameworkStores<AppDbContext>();

        // Tokens de reset de senha / confirmação de e-mail. Dependem do DataProtection, que só
        // existe no runtime (o keyring vive no Redis). O bootstrap não emite token nenhum.
        if (!forBootstrap)
        {
            identity.AddDefaultTokenProviders();
        }

        // A peça-chave: NormalizedEmail passa a guardar o HMAC do e-mail, não o e-mail.
        // Registrado DEPOIS do AddIdentityCore para substituir o normalizador padrão.
        services.AddScoped<ILookupNormalizer, BlindIndexLookupNormalizer>();

        // ⚠️ CRÍTICO PARA ESCALA. O AddRoles<>() acima registra um factory de principal que
        // copia TODAS as roles do usuário e TODAS as claims delas para o cookie. Como as
        // permissões deste template são claims de papel, um usuário com muitos papéis geraria
        // um cookie de ~50 KB — 12x o limite do navegador, que o descarta EM SILÊNCIO.
        //
        // Este factory mantém no cookie apenas a identidade (~411 bytes, constante). As
        // permissões vêm do IPermissionResolver a cada request.
        //
        // Registrado por ÚLTIMO, para substituir o do AddRoles<>().
        services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, LeanClaimsPrincipalFactory>();

        // Refresh tokens no Redis: efêmeros (TTL nativo), não poluem o banco.
        // O bootstrap não autentica ninguém — e o Redis nem é registrado lá.
        if (!forBootstrap)
        {
            services.AddSingleton<IRefreshTokenStore, RedisRefreshTokenStore>();
        }

        return services;
    }
}
