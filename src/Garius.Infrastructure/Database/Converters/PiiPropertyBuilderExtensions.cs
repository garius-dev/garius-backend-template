using System.Linq.Expressions;
using Garius.Core.Security;
using Garius.Core.Tenancy;
using Garius.Infrastructure.Database.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Garius.Infrastructure.Database;

/// <summary>
/// Declara uma coluna como dado pessoal no mapeamento do EF.
/// </summary>
public static class PiiPropertyBuilderExtensions
{
    /// <summary>
    /// Configura, de uma vez, a coluna cifrada (<c>bytea</c>, AES-256-GCM) e a coluna do
    /// <b>índice cego</b> que a torna pesquisável.
    ///
    /// <code>
    /// builder.HasPii(u => u.Email, u => u.EmailIndex, PiiScope.Email, encryptor, unique: true);
    /// </code>
    ///
    /// <para>
    /// As duas colunas são configuradas juntas de propósito: cifrar sem indexar produz um
    /// campo em que não se pode buscar (e o login para de funcionar); indexar sem cifrar não
    /// protege nada. Separá-las seria convidar a fazer metade.
    /// </para>
    ///
    /// <para>
    /// Com <c>unique = true</c>, o índice é <b>composto com <c>TenantId</c></b> (em entidades
    /// multi-tenant) e sempre <b>parcial</b> (<c>WHERE Enabled = true</c>) — é o que permite
    /// recadastrar o e-mail de quem excluiu a conta. Ver a Regra 6 no README.
    /// </para>
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasPii<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, Pii>> piiProperty,
        Expression<Func<TEntity, byte[]>> indexProperty,
        PiiScope scope,
        IFieldEncryptor encryptor,
        bool unique = false)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(encryptor);
        ArgumentNullException.ThrowIfNull(indexProperty);

        builder.Property(piiProperty)
               .HasConversion(new PiiConverter(encryptor, scope))
               .HasColumnType("bytea")
               .IsRequired();

        builder.Property(indexProperty)
               .HasColumnType("bytea")
               .IsRequired();

        var indexColumn = GetPropertyName(indexProperty);

        var columns = typeof(ITenantEntity).IsAssignableFrom(typeof(TEntity))
            ? new[] { nameof(ITenantEntity.TenantId), indexColumn }
            : [indexColumn];

        var index = builder.HasIndex(columns);

        if (unique)
        {
            index.IsUnique().HasFilter("\"Enabled\" = true");
        }

        return builder;
    }

    private static string GetPropertyName<TEntity>(Expression<Func<TEntity, byte[]>> expression) =>
        expression.Body is MemberExpression member
            ? member.Member.Name
            : throw new ArgumentException(
                "A expressão do índice cego deve apontar uma propriedade simples (ex.: u => u.EmailIndex).",
                nameof(expression));
}
