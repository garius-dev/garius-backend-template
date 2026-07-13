namespace Garius.Core.Entities;

/// <summary>
/// Base de <b>toda</b> entidade de <b>toda</b> tabela.
///
/// <para>
/// As entidades do Identity não podem herdar daqui (já herdam de <c>IdentityUser</c> /
/// <c>IdentityRole</c>), mas implementam <see cref="IAuditable"/> — que é o contrato pelo
/// qual o <c>AppDbContext</c> aplica soft delete, timestamps e índices a todas as tabelas.
/// </para>
/// </summary>
public abstract class BaseEntity : IAuditable
{
    /// <summary>
    /// UUID v7: sequencial no tempo, mas ainda um <see cref="Guid"/> comum
    /// (Postgres <c>uuid</c>).
    ///
    /// <para>
    /// Não usar <see cref="Guid.NewGuid"/>: um GUID aleatório como PK fragmenta o índice
    /// B-tree do Postgres — cada insert cai num ponto aleatório da árvore. Num template
    /// que vai rodar em N apps por anos, é a decisão que não se quer descobrir errada
    /// com milhões de linhas.
    /// </para>
    /// </summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// Soft delete. O <c>DbContext</c> aplica um global query filter — uma entidade com
    /// <c>Enabled = false</c> some das consultas sem que ninguém precise lembrar de filtrar.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Preenchido automaticamente pelo interceptor de auditoria. Nunca definir à mão.
    /// <c>DateTimeOffset</c> (Postgres <c>timestamptz</c>), sempre UTC.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Preenchido automaticamente pelo interceptor de auditoria. Nunca definir à mão.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
