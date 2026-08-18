using System.Reflection;
using Garius.Core.Tenancy;
using Garius.Infrastructure.Database.Interceptors;
using Garius.Infrastructure.Identity;
using Garius.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Garius.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
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
    /// <param name="services">O contêiner.</param>
    /// <param name="configuration">A configuração.</param>
    /// <param name="environment">O ambiente (para o DockerAwareHost).</param>
    /// <param name="applicationAssembly">O assembly das migrations.</param>
    /// <param name="migrateOnly">Modo bootstrap.</param>
    /// <param name="onHostResolved">
    /// Avisa quando o host do secret é trocado por <c>localhost</c> (dev fora do Docker). O
    /// <c>Program</c> o liga ao log — esta camada não conhece o logger. Ver DockerAwareHost.
    /// </param>
    /// <param name="onCapacityWarning">
    /// Avisa quando <c>réplicas × pool</c> se aproxima do <c>max_connections</c> do Postgres.
    /// Pelo mesmo motivo do <paramref name="onHostResolved"/>: esta camada não conhece o logger.
    /// </param>
    public static DatabaseNaming AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        Assembly applicationAssembly,
        bool migrateOnly,
        Action<string, string, string>? onHostResolved = null,
        Action<string>? onCapacityWarning = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.Configure<TenancyOptions>(configuration.GetSection(TenancyOptions.SectionName));

        // O primeiro usuário (superadministrador). As duas chaves vêm do Secret Manager; se não
        // estiverem lá, NENHUM usuário é criado — ver BootstrapAdminSeeder.
        services.Configure<BootstrapAdminOptions>(
            configuration.GetSection(BootstrapAdminOptions.SectionName));

        var options = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(options);

        // O secret guarda o host de PRODUÇÃO (o nome do container). Rodando na máquina, fora do
        // Docker, ele vira `localhost` — é o que permite UM secret só, sem duas cópias para manter
        // em sincronia. Ver DockerAwareHost.
        options.Host = DockerAwareHost.Resolve(
            options.Host,
            configuration,
            environment,
            onResolved: onHostResolved);

        services.AddSingleton(options);

        var naming = new DatabaseNaming(options, applicationAssembly);
        services.AddSingleton(naming);

        WarnIfConnectionCeilingIsNear(options, onCapacityWarning);

        RegisterTenantResolver(services, configuration, migrateOnly);

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AuditingInterceptor>();

        var connectionString = migrateOnly
            ? naming.RootConnectionStringToAppDatabase
            : naming.AppConnectionString;

        services.AddDbContext<AppDbContext>((provider, builder) =>
        {
            builder.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);

                // RESILIÊNCIA DE CONEXÃO. Retenta o que falhou por motivo TRANSITÓRIO.
                //
                // Em Postgres local isto parece supérfluo. Em nuvem gerenciada, não: um
                // failover de Cloud SQL / RDS / Aurora derruba as conexões abertas por 5 a 30
                // segundos, e isso é ROTINA — acontece em manutenção programada do provedor,
                // sem aviso útil. Sem retry, toda manutenção do banco vira uma janela de 500
                // para o cliente.
                //
                // O Npgsql já sabe distinguir o que é transitório (falha de rede, failover) do
                // que é erro de verdade (constraint violada, sintaxe): só o primeiro grupo é
                // retentado. Um erro de negócio NÃO é tentado de novo três vezes.
                //
                // ⚠️ ISTO TEM UMA CONSEQUÊNCIA QUE NÃO É ÓBVIA: com o retry ligado, o EF passa
                // a proibir transação explícita (BeginTransaction), porque ele não sabe
                // reexecutar um bloco que você abriu à mão. Quem precisa de transação tem de
                // pedir a execution strategy e rodar dentro dela — ver OutboxProcessor, que é
                // o único lugar do template que abre transação.
                //
                // O erro, se alguém esquecer, é claro ("The configured execution strategy
                // 'NpgsqlRetryingExecutionStrategy' does not support user-initiated
                // transactions") — mas só aparece em RUNTIME, quando aquele caminho executa.
                npgsql.EnableRetryOnFailure(
                    maxRetryCount: options.MaxRetryCount,
                    maxRetryDelay: TimeSpan.FromSeconds(options.MaxRetryDelaySeconds),
                    errorCodesToAdd: null);
            });

            builder.AddInterceptors(provider.GetRequiredService<AuditingInterceptor>());

            // NUNCA em produção: EnableSensitiveDataLogging põe os VALORES dos parâmetros
            // (senhas, e-mails, CPFs) no log. O default já é desligado; explicitar aqui é
            // uma trava contra alguém ligar sem pensar.
            builder.EnableDetailedErrors(migrateOnly);
        });

        // O Identity vale nos DOIS modos, e por motivos diferentes:
        //
        //   runtime   -> autentica gente (UserManager, o normalizador de índice cego, etc.);
        //   bootstrap -> CRIA o primeiro usuário (o superadministrador). Isso exige o
        //                UserManager: é ele que faz o hash da senha e, via
        //                BlindIndexLookupNormalizer, grava o índice cego do e-mail. Montar o
        //                usuário à mão no DbContext gravaria uma senha sem hash e um
        //                NormalizedEmail nulo — e o login nunca funcionaria.
        services.AddApplicationIdentity(forBootstrap: migrateOnly);

        if (migrateOnly)
        {
            services.AddScoped<DatabaseBootstrapper>();
            services.AddScoped<BootstrapAdminSeeder>();
        }

        return naming;
    }

    /// <summary>
    /// Avisa — <b>alto</b>, no boot — quando <c>réplicas × pool</c> chega perto do
    /// <c>max_connections</c> do Postgres.
    ///
    /// <para>
    /// <b>Por que AVISO e não falha fechada.</b> O template falha fechado em <i>configuração
    /// inválida</i> (regra 9), e isto não é uma: é um alerta de <b>capacidade</b>, sobre um
    /// número (<c>ExpectedReplicas</c>) que a aplicação não tem como verificar — ela não sabe
    /// quantas réplicas o cluster vai criar. Derrubar o boot por causa de uma estimativa
    /// impediria de subir uma app perfeitamente saudável.
    /// </para>
    ///
    /// <para>
    /// O que este aviso compra é o <b>tempo</b>: sem ele, o estouro só aparece quando o HPA
    /// escala no pico, o Postgres recusa conexão com <c>too many clients already</c>, e o erro
    /// chega junto com o incidente que causou o pico — parecendo consequência dele, não causa.
    /// </para>
    /// </summary>
    private static void WarnIfConnectionCeilingIsNear(
        DatabaseOptions options,
        Action<string>? onCapacityWarning)
    {
        // Sem estimativa de réplicas, não há o que calcular. Num template, chutar a topologia
        // de quem deriva seria gritar errado — e um alarme que toca à toa é um alarme que
        // ninguém olha.
        if (options.ExpectedReplicas <= 0 || onCapacityWarning is null)
        {
            return;
        }

        // O Hangfire mantém o PRÓPRIO pool, no banco dele — ver HangfireSetup, onde o
        // WorkerCount é limitado a 8 exatamente por causa desta conta.
        const int HangfirePoolPerReplica = 8;

        // +5 para o container de migração e a folga de conexões administrativas (o psql de
        // alguém investigando, o pg_dump do backup). Esquecer essa folga é descobrir que não
        // dá para entrar no banco justamente durante o incidente.
        var estimated = options.ExpectedReplicas * (options.MaxPoolSize + HangfirePoolPerReplica) + 5;

        var threshold = options.PostgresMaxConnections * 0.7;

        if (estimated <= threshold)
        {
            return;
        }

        onCapacityWarning(
            $"Capacidade de conexões: {options.ExpectedReplicas} réplica(s) × " +
            $"({options.MaxPoolSize} do pool + {HangfirePoolPerReplica} do Hangfire) + 5 = " +
            $"~{estimated} conexões, contra max_connections={options.PostgresMaxConnections}. " +
            "Isto NÃO falha em teste nem com uma réplica — falha quando o autoscaler escala no " +
            "pico, e o 'too many clients already' chega junto com o incidente que causou o pico. " +
            "Reduza Database:MaxPoolSize, aumente o max_connections, ou ponha um pooler " +
            "(pgBouncer/PgCat) na frente.");
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
