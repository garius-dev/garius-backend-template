using System.Reflection;
using Garius.Core.Tenancy;
using Garius.Infrastructure.Database.Interceptors;
using Garius.Infrastructure.Identity;
using Garius.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Garius.Infrastructure.Database;

public static class PersistenceExtensions
{
    /// <summary>
    /// Modo bootstrap: cria banco/roles/grants, aplica migrations e encerra.
    ///
    /// <para>
    /// <b>Esta constante é a única definição do nome.</b> No template anterior, o compose
    /// mandava <c>MIGRATE_ONLY</c> e o código lia <c>MIGRATION_ONLY</c> — uma letra de
    /// diferença que fazia o container de migrations nunca entrar em modo migration. O
    /// deploy falhava 100% das vezes no primeiro boot, e a API nunca subia.
    /// </para>
    ///
    /// <para>
    /// Lido via <see cref="IConfiguration"/> (não <c>Environment.GetEnvironmentVariable</c>),
    /// então participa da cascata de configuração normalmente.
    /// </para>
    /// </summary>
    public const string MigrateOnlyKey = "MIGRATE_ONLY";

    public static bool IsMigrateOnly(this IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration.GetValue<bool>(MigrateOnlyKey);
    }

    /// <summary>
    /// Registra EF Core, tenancy e o bootstrapper.
    ///
    /// <para>
    /// Com <c>migrateOnly = true</c>, o <c>DbContext</c> conecta como <b>superusuário</b>
    /// (só ele pode rodar DDL) e o tenant resolver é o <see cref="SystemTenantResolver"/>
    /// (enxerga todos os tenants). Em runtime, conecta como <c>{app}_user</c>, que só tem CRUD.
    /// </para>
    ///
    /// <para>
    /// Devolve o <see cref="DatabaseNaming"/> — a fonte única dos nomes e connection
    /// strings — para que o health check use exatamente a mesma string do DbContext.
    /// </para>
    /// </summary>
    public static DatabaseNaming AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        Assembly applicationAssembly,
        bool migrateOnly)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<TenancyOptions>(configuration.GetSection(TenancyOptions.SectionName));

        var options = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        var naming = new DatabaseNaming(options, applicationAssembly);
        services.AddSingleton(naming);

        RegisterTenantResolver(services, configuration, migrateOnly);

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AuditingInterceptor>();

        var connectionString = migrateOnly
            ? naming.RootConnectionStringToAppDatabase
            : naming.AppConnectionString;

        services.AddDbContext<AppDbContext>((provider, builder) =>
        {
            builder.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName));

            builder.AddInterceptors(provider.GetRequiredService<AuditingInterceptor>());

            // NUNCA em produção: EnableSensitiveDataLogging põe os VALORES dos parâmetros
            // (senhas, e-mails, CPFs) no log. O default já é desligado; explicitar aqui é
            // uma trava contra alguém ligar sem pensar.
            builder.EnableDetailedErrors(migrateOnly);
        });

        if (migrateOnly)
        {
            services.AddScoped<DatabaseBootstrapper>();
        }
        else
        {
            // O Identity só faz sentido no runtime; o bootstrap não autentica ninguém.
            services.AddApplicationIdentity();
        }

        return naming;
    }

    private static void RegisterTenantResolver(
        IServiceCollection services,
        IConfiguration configuration,
        bool migrateOnly)
    {
        if (migrateOnly)
        {
            // O bootstrap opera sobre todos os tenants (e cria o primeiro deles).
            services.AddScoped<ITenantResolver, SystemTenantResolver>();
            return;
        }

        var tenancy = new TenancyOptions();
        configuration.GetSection(TenancyOptions.SectionName).Bind(tenancy);

        // ESTA é a linha que alterna single-tenant ↔ SaaS. O schema não muda:
        // a coluna TenantId e o query filter existem nos dois modos.
        if (tenancy.Mode == TenancyMode.MultiTenant)
        {
            services.AddHttpContextAccessor();
            services.AddScoped<ITenantResolver, ClaimsTenantResolver>();
        }
        else
        {
            services.AddScoped<ITenantResolver, SingleTenantResolver>();
        }
    }
}
