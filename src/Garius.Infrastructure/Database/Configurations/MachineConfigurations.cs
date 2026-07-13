using Garius.Core.Machine;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Garius.Infrastructure.Database.Configurations;

internal sealed class OAuthClientConfiguration : IEntityTypeConfiguration<OAuthClient>
{
    public void Configure(EntityTypeBuilder<OAuthClient> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("oauth_clients");

        builder.Property(c => c.ClientId).HasMaxLength(100).IsRequired();
        builder.Property(c => c.SecretHash).HasMaxLength(64).IsRequired();
        builder.Property(c => c.Name).HasMaxLength(200).IsRequired();

        builder.Property(c => c.ExpiresAt).HasColumnType("timestamptz");
        builder.Property(c => c.LastUsedAt).HasColumnType("timestamptz");

        // Escopos como array nativo do Postgres (text[]), não como uma tabela filha nem um
        // JSON. São uma lista curta, sempre lida inteira junto com o client e nunca consultada
        // por elemento — uma tabela filha só acrescentaria um JOIN a cada emissão de token.
        builder.Property(c => c.Scopes)
               .HasColumnType("text[]")
               .IsRequired();

        // Índice único PARCIAL (ativos). Como no slug do tenant: com um índice total, um
        // client revogado (Enabled = false) manteria o client_id ocupado para sempre.
        builder.HasIndex(c => c.ClientId)
               .IsUnique()
               .HasFilter("\"Enabled\" = true");
    }
}

internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("api_keys");

        builder.Property(k => k.KeyPrefix).HasMaxLength(32).IsRequired();
        builder.Property(k => k.KeyHash).HasMaxLength(64).IsRequired();
        builder.Property(k => k.Name).HasMaxLength(200).IsRequired();

        builder.Property(k => k.ExpiresAt).HasColumnType("timestamptz");
        builder.Property(k => k.LastUsedAt).HasColumnType("timestamptz");

        builder.Property(k => k.Scopes)
               .HasColumnType("text[]")
               .IsRequired();

        // O prefixo é o que se busca a cada request autenticado por chave — sem este índice,
        // toda chamada faria um seq scan comparando hashes.
        //
        // NÃO é único: dois segredos diferentes podem, por acaso, começar com os mesmos 8
        // caracteres. É um índice de BUSCA, e por isso a autenticação sempre confirma o hash
        // completo depois — nunca aceita uma chave só porque o prefixo bateu.
        builder.HasIndex(k => k.KeyPrefix);

        // O hash, sim, é único entre as ativas: duas chaves não podem ser o mesmo segredo.
        builder.HasIndex(k => k.KeyHash)
               .IsUnique()
               .HasFilter("\"Enabled\" = true");
    }
}
