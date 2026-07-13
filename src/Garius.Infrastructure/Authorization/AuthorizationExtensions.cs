using Garius.Core.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Garius.Infrastructure.Authorization;

public static class AuthorizationExtensions
{
    /// <summary>
    /// Registra o resolvedor de permissões efetivas (papéis + avulsas), com cache <b>no
    /// Redis</b>.
    ///
    /// <para>
    /// O cache é no Redis, e não em memória, porque a invalidação precisa alcançar <b>todas as
    /// réplicas</b> — ver <see cref="RedisPermissionResolver"/>. Com um cache por processo, a
    /// revogação de acesso funcionava só na réplica que atendeu o request, e o usuário revogado
    /// continuava entrando pelas outras, de forma intermitente.
    /// </para>
    ///
    /// <para>
    /// A implementação é <c>internal</c>: quem consome depende de
    /// <see cref="IPermissionResolver"/>, não da classe concreta.
    /// </para>
    /// </summary>
    public static IServiceCollection AddPermissionResolver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IPermissionResolver, RedisPermissionResolver>();

        return services;
    }
}
