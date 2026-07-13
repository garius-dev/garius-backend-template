using System.Linq.Expressions;
using Garius.Core.Entities;
using Garius.Core.Identity;
using Garius.Core.Machine;
using Garius.Core.Messaging;
using Garius.Core.Security;
using Garius.Core.Tenancy;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Garius.Infrastructure.Database;

/// <summary>
/// O <c>DbContext</c> da aplicação. É também o contexto do ASP.NET Core Identity.
///
/// <para>
/// Dois global query filters são aplicados automaticamente a toda entidade
/// <see cref="IAuditable"/> — incluindo as do Identity:
/// </para>
/// <list type="number">
///   <item><b>Soft delete</b> — <c>Enabled = false</c> some das consultas.</item>
///   <item><b>Tenant</b> — em <see cref="ITenantEntity"/>, filtra pelo tenant corrente.</item>
/// </list>
///
/// <para>
/// O filtro de tenant fica <b>sempre ligado</b>, inclusive em modo single-tenant (onde
/// compara com um valor constante, custo ~zero — o Postgres resolve pelo índice). É isso
/// que elimina, por construção, a classe de bug "esqueci de filtrar e vazei dado entre
/// clientes".
/// </para>
/// </summary>
public class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ITenantResolver tenantResolver,
    IFieldEncryptor encryptor)
    : IdentityDbContext<
        ApplicationUser,
        ApplicationRole,
        Guid,
        ApplicationUserClaim,
        ApplicationUserRole,
        ApplicationUserLogin,
        ApplicationRoleClaim,
        ApplicationUserToken>(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    /// <summary>Vínculo N:N entre usuário e tenant.</summary>
    public DbSet<ApplicationUserTenant> UserTenants => Set<ApplicationUserTenant>();

    /// <summary>Trilha de auditoria de leitura de dados pessoais (LGPD, Art. 37).</summary>
    public DbSet<PiiAccessLog> PiiAccessLogs => Set<PiiAccessLog>();

    /// <summary>
    /// Eventos de domínio à espera de publicação. Gravados na <b>mesma transação</b> do dado
    /// que os originou — ver <see cref="OutboxMessage"/>.
    /// </summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>Clients OAuth2 (máquina a máquina, via <i>client credentials</i>).</summary>
    public DbSet<OAuthClient> OAuthClients => Set<OAuthClient>();

    /// <summary>Chaves de API de terceiros.</summary>
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    /// <summary>
    /// Exposto às configurações de entidade (via <c>ApplyConfigurationsFromAssembly</c>) para
    /// mapear os campos cifrados. O EF constrói o modelo antes de existir um scope de DI, então
    /// o encryptor precisa vir por aqui.
    /// </summary>
    internal IFieldEncryptor Encryptor => encryptor;

    /// <summary>
    /// Lido pelos query filters. É uma propriedade (não uma constante capturada), então o
    /// EF a trata como parâmetro da query compilada — o filtro funciona mesmo com o modelo
    /// em cache e um tenant diferente a cada request.
    /// </summary>
    private Guid? CurrentTenantId => tenantResolver.CurrentTenantId;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        base.OnConfiguring(optionsBuilder);

        // O IdentityDbContext base mapeia ApplicationUser.Email, e nós o ignoramos logo em
        // seguida (a coluna gravaria o e-mail EM CLARO — ver ApplicationUserConfiguration).
        // O EF avisa sobre esse "mapeia e ignora" a cada boot. É consequência do design
        // correto, não de um defeito — e um aviso tolerado em todo boot vira ruído que acaba
        // escondendo o aviso que importa.
        optionsBuilder.ConfigureWarnings(warnings =>
            warnings.Ignore(CoreEventId.MappedPropertyIgnoredWarning));
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Monta as tabelas do Identity.
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Aplicada à mão: precisa do IFieldEncryptor, que o scan por reflexão não injetaria.
        // Por isso ela NÃO implementa IEntityTypeConfiguration<> — ver o comentário lá.
        new Configurations.ApplicationUserConfiguration(encryptor)
            .Configure(builder.Entity<ApplicationUser>());

        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            if (!typeof(IAuditable).IsAssignableFrom(clrType))
            {
                continue;
            }

            builder.Entity(clrType).HasQueryFilter(BuildQueryFilter(clrType));

            // timestamptz: sem isto o Npgsql grava DateTimeOffset como "timestamp without
            // time zone" e o offset se perde silenciosamente.
            builder.Entity(clrType).Property(nameof(IAuditable.CreatedAt)).HasColumnType("timestamptz");
            builder.Entity(clrType).Property(nameof(IAuditable.UpdatedAt)).HasColumnType("timestamptz");

            // Toda consulta passa pelo filtro de soft delete; sem este índice, toda
            // consulta faria seq scan em tabelas grandes.
            builder.Entity(clrType).HasIndex(nameof(IAuditable.Enabled));

            if (typeof(ITenantEntity).IsAssignableFrom(clrType))
            {
                builder.Entity(clrType).HasIndex(nameof(ITenantEntity.TenantId));
            }
        }
    }

    /// <summary>
    /// Monta <c>e =&gt; e.Enabled</c> e, se a entidade é multi-tenant,
    /// <c>e =&gt; e.Enabled &amp;&amp; (e.TenantId == CurrentTenantId || CurrentTenantId == null)</c>.
    ///
    /// <para>
    /// Um <c>CurrentTenantId</c> nulo significa <b>sem filtro de tenant</b> — é o caso do
    /// bootstrap, das migrations e de jobs de manutenção, que legitimamente enxergam todos
    /// os tenants. Num request autenticado ele nunca é nulo.
    /// </para>
    /// </summary>
    private LambdaExpression BuildQueryFilter(Type clrType)
    {
        var parameter = Expression.Parameter(clrType, "e");

        Expression body = Expression.Property(parameter, nameof(IAuditable.Enabled));

        if (typeof(ITenantEntity).IsAssignableFrom(clrType))
        {
            var currentTenant = Expression.Property(
                Expression.Constant(this),
                nameof(CurrentTenantId));

            var tenantMatches = Expression.Equal(
                Expression.Convert(Expression.Property(parameter, nameof(ITenantEntity.TenantId)), typeof(Guid?)),
                currentTenant);

            var noTenantContext = Expression.Equal(currentTenant, Expression.Constant(null, typeof(Guid?)));

            body = Expression.AndAlso(body, Expression.OrElse(tenantMatches, noTenantContext));
        }

        return Expression.Lambda(body, parameter);
    }
}
