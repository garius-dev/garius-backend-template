using Garius.Core.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Garius.Infrastructure.Database.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("tenants");

        builder.Property(t => t.Name).HasMaxLength(200).IsRequired();
        builder.Property(t => t.Slug).HasMaxLength(100).IsRequired();

        // ÍNDICE ÚNICO PARCIAL — a decisão sobre soft delete.
        //
        // Com um índice único total, um tenant "excluído" (Enabled = false) manteria o
        // slug ocupado para sempre: ninguém mais poderia recriar um tenant com aquele
        // nome. O filtro parcial faz a unicidade valer apenas entre os registros ativos.
        //
        // É a mesma regra para todo identificador de negócio no template (e-mail, CPF...).
        // Cuidado ao REATIVAR um registro soft-deleted: ele pode colidir com um novo criado
        // no lugar — a reativação precisa checar antes.
        builder.HasIndex(t => t.Slug)
               .IsUnique()
               .HasFilter("\"Enabled\" = true");
    }
}
