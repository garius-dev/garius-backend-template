using Garius.Core.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Garius.Infrastructure.Database.Configurations;

internal sealed class OutboxConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("outbox_messages");

        builder.Property(m => m.Type).HasMaxLength(200).IsRequired();

        // jsonb, não text: o Postgres o valida na escrita (um JSON malformado nunca entra) e
        // permite consultá-lo depois — o que é exatamente o que se quer numa investigação
        // ("qual mensagem tinha este userId?").
        builder.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();

        builder.Property(m => m.ProcessedAt).HasColumnType("timestamptz");

        builder.Property(m => m.LastError).HasMaxLength(2000);

        // O ÍNDICE QUE FAZ O DRENADOR FUNCIONAR.
        //
        // O job roda a cada minuto e pergunta sempre a mesma coisa: "quais mensagens ainda não
        // foram processadas?". Sem índice, isso é um seq scan na tabela INTEIRA — que só cresce,
        // porque as mensagens processadas não são apagadas (são a trilha de auditoria). Numa
        // tabela com milhões de linhas processadas e três pendentes, o job levaria segundos
        // para achar as três.
        //
        // PARCIAL (só as pendentes): o índice indexa apenas o que a query procura. Ele não
        // cresce com o histórico — uma mensagem processada SAI do índice. É a diferença entre
        // um índice de três linhas e um de milhões.
        builder.HasIndex(m => m.CreatedAt)
               .HasFilter("\"ProcessedAt\" IS NULL")
               .HasDatabaseName("ix_outbox_pending");
    }
}
