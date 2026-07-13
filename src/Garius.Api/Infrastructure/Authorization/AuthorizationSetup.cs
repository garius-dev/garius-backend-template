using Garius.Core.Authorization;
using Garius.Core.Machine;
using Garius.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Garius.Api.Infrastructure.Authorization;

internal static class AuthorizationSetup
{
    /// <summary>
    /// Exige autenticação, mas <b>não</b> exige a claim de tenant.
    ///
    /// <para>
    /// Para os endpoints do próprio fluxo de seleção de tenant (<c>/auth/select-tenant</c>,
    /// <c>/auth/logout</c>, <c>/auth/me</c>): eles precisam ser alcançáveis com o cookie
    /// parcial, ou o usuário com vários tenants nunca conseguiria escolher um.
    /// </para>
    /// </summary>
    internal const string AuthenticatedWithoutTenantPolicy = "AuthenticatedWithoutTenant";

    internal static IServiceCollection AddPermissionAuthorization(this IServiceCollection services)
    {
        services.AddPermissionResolver();

        // Cria as policies de permissão sob demanda: declarar .RequirePermission(...) no
        // endpoint é tudo o que é preciso. Sem isto, cada permissão teria de ser registrada
        // à mão aqui — e esquecer uma daria um 500, não um 403.
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        services.AddAuthorization(options =>
        {
            // FALHA FECHADA, em duas camadas.
            //
            // (1) autenticado — esquecer de proteger um endpoint novo o deixa FECHADO.
            //
            // (2) COM TENANT — e esta é a que menos se pensa. Quando o usuário pertence a
            //     vários tenants, o login emite um cookie PARCIAL (identidade, sem tenant)
            //     só para ele poder chamar /auth/select-tenant. Se esse cookie alcançasse um
            //     endpoint de negócio, o query filter compararia o tenant com `null` e NÃO
            //     FILTRARIA NADA: o usuário enxergaria os dados de TODOS os tenants.
            //
            //     Os endpoints do fluxo de seleção declaram AllowAnonymous/RequireAuthenticated
            //     explicitamente e ficam fora desta exigência.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireClaim(AppClaims.TenantId)
                .Build();

            options.AddPolicy(AuthenticatedWithoutTenantPolicy, policy => policy
                .RequireAuthenticatedUser());

            // Só máquina. Ver MachineAuthSetup.MachineOnlyPolicy.
            options.AddPolicy(MachineAuthSetup.MachineOnlyPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(MachineAuth.ClientTypeClaim));

            // Só pessoa: para o que não faz sentido vindo de um sistema (trocar a própria
            // senha, excluir a própria conta). Um client OAuth com a permissão certa passaria
            // sem isto — a permissão sozinha não expressa "isto exige um humano".
            options.AddPolicy(MachineAuthSetup.HumanOnlyPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    !context.User.HasClaim(claim => claim.Type == MachineAuth.ClientTypeClaim)));
        });

        return services;
    }
}
