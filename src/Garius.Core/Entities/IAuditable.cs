namespace Garius.Core.Entities;

/// <summary>
/// Contrato de soft delete e auditoria de tempo.
///
/// <para>
/// Existe porque nem tudo pode herdar de <see cref="BaseEntity"/>: as entidades do Identity
/// já herdam de <c>IdentityUser&lt;Guid&gt;</c> / <c>IdentityRole&lt;Guid&gt;</c>, e o C# não
/// tem herança múltipla. A interface é o que permite que o <c>AppDbContext</c> aplique o
/// mesmo tratamento (query filter de soft delete, <c>timestamptz</c>, índices, preenchimento
/// automático) a <b>todas</b> as tabelas, herdando de <c>BaseEntity</c> ou não.
/// </para>
/// </summary>
public interface IAuditable
{
    /// <summary>Soft delete: <c>false</c> some das consultas, mas o registro permanece no banco.</summary>
    bool Enabled { get; set; }

    /// <summary>Preenchido pelo interceptor. Imutável depois da criação.</summary>
    DateTimeOffset CreatedAt { get; set; }

    /// <summary>Preenchido pelo interceptor a cada gravação.</summary>
    DateTimeOffset UpdatedAt { get; set; }
}
