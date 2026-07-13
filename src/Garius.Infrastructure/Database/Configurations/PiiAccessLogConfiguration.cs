using Garius.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Garius.Infrastructure.Database.Configurations;

internal sealed class PiiAccessLogConfiguration : IEntityTypeConfiguration<PiiAccessLog>
{
    public void Configure(EntityTypeBuilder<PiiAccessLog> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("pii_access_logs");

        builder.Property(l => l.EntityType).HasMaxLength(100).IsRequired();
        builder.Property(l => l.Reason).HasMaxLength(500).IsRequired();
        builder.Property(l => l.ClientIp).HasMaxLength(45);   // cabe um IPv6
        builder.Property(l => l.TraceId).HasMaxLength(64);

        // Guardado como texto, não como int: um número no banco de auditoria não diz nada
        // a quem investiga um incidente daqui a dois anos, e reordenar o enum trocaria o
        // significado dos registros antigos.
        builder.Property(l => l.Scope)
               .HasConversion<string>()
               .HasMaxLength(30)
               .IsRequired();

        // As duas perguntas que uma auditoria faz:
        //   "quem acessou os dados DESTA pessoa?"
        builder.HasIndex(l => new { l.EntityType, l.EntityId });
        //   "o que ESTE usuário andou acessando?"
        builder.HasIndex(l => new { l.ActorUserId, l.CreatedAt });
    }
}
