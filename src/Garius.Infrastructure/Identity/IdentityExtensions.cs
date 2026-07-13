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
    public static IServiceCollection AddApplicationIdentity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // O DataProtection (que cifra o cookie e os tokens do Identity) é registrado em
        // AddRedis, com o keyring NO REDIS — sem isso, duas réplicas não conseguem ler o
        // cookie uma da outra.

        services.AddIdentityCore<ApplicationUser>(options =>
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
        .AddEntityFrameworkStores<AppDbContext>()
        .AddDefaultTokenProviders();

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
        services.AddSingleton<IRefreshTokenStore, RedisRefreshTokenStore>();

        return services;
    }
}
