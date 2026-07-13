using Garius.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Garius.Infrastructure.Database.Configurations;

internal sealed class ApplicationRoleConfiguration : IEntityTypeConfiguration<ApplicationRole>
{
    public void Configure(EntityTypeBuilder<ApplicationRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("roles");

        builder.Property(r => r.Name).HasMaxLength(100);
        builder.Property(r => r.NormalizedName).HasMaxLength(100);
        builder.Property(r => r.Description).HasMaxLength(500);

        // Um papel global e um papel de tenant podem ter o mesmo nome — daí o índice
        // composto. Parcial, como todo índice único do template: excluir um papel libera
        // o nome para ser reutilizado.
        builder.HasIndex(r => new { r.TenantId, r.NormalizedName })
               .IsUnique()
               .HasFilter("\"Enabled\" = true");

        builder.HasMany(r => r.Claims)
               .WithOne(c => c.Role)
               .HasForeignKey(c => c.RoleId)
               .IsRequired();

        builder.HasMany(r => r.UserRoles)
               .WithOne(ur => ur.Role)
               .HasForeignKey(ur => ur.RoleId)
               .IsRequired();
    }
}

internal sealed class ApplicationUserRoleConfiguration : IEntityTypeConfiguration<ApplicationUserRole>
{
    public void Configure(EntityTypeBuilder<ApplicationUserRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_roles");

        builder.Property(ur => ur.CreatedAt).HasColumnType("timestamptz");

        // A chave primária continua sendo a do Identity: (UserId, RoleId).
        // Não acrescentar TenantId aqui — o UserStore faz Find(userId, roleId) com dois
        // valores, e uma chave de três partes quebra o AddToRoleAsync.
        // O tenant vive no PAPEL (ApplicationRole.TenantId).
        builder.HasIndex(ur => ur.UserId);

        // Filtro de SEGURANÇA: um vínculo com usuário ou papel desabilitado não deve
        // conceder acesso nenhum. Ver o comentário em ApplicationRoleClaimConfiguration.
        builder.HasQueryFilter(ur => ur.User.Enabled && ur.Role.Enabled);
    }
}

internal sealed class ApplicationUserTenantConfiguration : IEntityTypeConfiguration<ApplicationUserTenant>
{
    public void Configure(EntityTypeBuilder<ApplicationUserTenant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_tenants");

        builder.HasKey(ut => new { ut.UserId, ut.TenantId });

        builder.Property(ut => ut.CreatedAt).HasColumnType("timestamptz");

        builder.HasOne(ut => ut.Tenant)
               .WithMany()
               .HasForeignKey(ut => ut.TenantId)
               .IsRequired();

        // Filtro de segurança: o vínculo só vale se o usuário, o tenant e o próprio vínculo
        // estiverem ativos. Sem isto, remover alguém de um tenant não o removeria de fato.
        builder.HasQueryFilter(ut => ut.Enabled && ut.User.Enabled && ut.Tenant.Enabled);
    }
}

internal sealed class ApplicationUserClaimConfiguration : IEntityTypeConfiguration<ApplicationUserClaim>
{
    public void Configure(EntityTypeBuilder<ApplicationUserClaim> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_claims");

        builder.HasIndex(c => new { c.UserId, c.TenantId });

        // Filtro de segurança: permissão avulsa de usuário desabilitado não vale.
        builder.HasQueryFilter(c => c.User.Enabled);
    }
}

internal sealed class ApplicationRoleClaimConfiguration : IEntityTypeConfiguration<ApplicationRoleClaim>
{
    public void Configure(EntityTypeBuilder<ApplicationRoleClaim> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("role_claims");

        builder.HasIndex(c => c.RoleId);

        // ⚠️ ESTE FILTRO É DE SEGURANÇA, não cosmético.
        //
        // ApplicationRole tem soft delete; ApplicationRoleClaim (que carrega as PERMISSÕES)
        // não herda de IAuditable e não teria filtro nenhum. Sem esta linha, desabilitar um
        // papel o esconderia das consultas — mas as permissões dele continuariam sendo
        // carregadas. Revogar um papel não revogaria o acesso.
        //
        // O EF, aliás, avisa exatamente isso: "has a global query filter defined and is the
        // required end of a relationship... may lead to unexpected results".
        builder.HasQueryFilter(c => c.Role.Enabled);
    }
}

internal sealed class ApplicationUserLoginConfiguration : IEntityTypeConfiguration<ApplicationUserLogin>
{
    public void Configure(EntityTypeBuilder<ApplicationUserLogin> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_logins");

        builder.HasQueryFilter(l => l.User.Enabled);
    }
}

internal sealed class ApplicationUserTokenConfiguration : IEntityTypeConfiguration<ApplicationUserToken>
{
    public void Configure(EntityTypeBuilder<ApplicationUserToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_tokens");

        // Um token (reset de senha, confirmação) de usuário desabilitado não pode ser usado.
        builder.HasQueryFilter(t => t.User.Enabled);
    }
}
