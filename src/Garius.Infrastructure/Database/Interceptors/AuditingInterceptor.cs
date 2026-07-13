using Garius.Core.Entities;
using Garius.Core.Security;
using Garius.Core.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Garius.Infrastructure.Database.Interceptors;

/// <summary>
/// Preenche <see cref="BaseEntity.CreatedAt"/>, <see cref="BaseEntity.UpdatedAt"/> e o
/// <see cref="ITenantEntity.TenantId"/> automaticamente. Ninguém escreve isso à mão, e
/// ninguém esquece.
///
/// <para>
/// Também converte <b>DELETE em soft delete</b>: <c>Remove()</c> vira
/// <c>Enabled = false</c>. Combinado com o global query filter, um registro removido
/// simplesmente some das consultas — mas continua no banco, o que a LGPD exige para a
/// trilha de auditoria.
/// </para>
/// </summary>
internal sealed class AuditingInterceptor(ITenantResolver tenantResolver, TimeProvider timeProvider)
    : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is not null)
        {
            Apply(eventData.Context);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is not null)
        {
            Apply(eventData.Context);
        }

        return base.SavingChanges(eventData, result);
    }

    private void Apply(DbContext context)
    {
        var now = timeProvider.GetUtcNow();

        foreach (var entry in context.ChangeTracker.Entries<BaseEntity>())
        {
            // A trilha de auditoria é APPEND-ONLY. Um registro de auditoria que pode ser
            // alterado ou apagado não vale nada: é justamente o que um invasor faria para
            // encobrir o acesso indevido. Só a inserção é permitida.
            if (entry.Entity is PiiAccessLog && entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    "PiiAccessLog é uma trilha de auditoria imutável (LGPD, Art. 37): não pode ser " +
                    "alterada nem removida. Se o volume for um problema, arquive os registros antigos " +
                    "para storage frio — não os apague.");
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    AssignTenant(entry.Entity);
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    // CreatedAt é imutável: sem isto, um update poderia sobrescrevê-lo.
                    entry.Property(e => e.CreatedAt).IsModified = false;
                    break;

                case EntityState.Deleted:
                    // Soft delete: nunca apagamos de fato.
                    entry.State = EntityState.Modified;
                    entry.Entity.Enabled = false;
                    entry.Entity.UpdatedAt = now;
                    entry.Property(e => e.CreatedAt).IsModified = false;
                    break;

                default:
                    break;
            }
        }
    }

    private void AssignTenant(BaseEntity entity)
    {
        if (entity is not ITenantEntity tenantEntity || tenantEntity.TenantId != Guid.Empty)
        {
            return;
        }

        tenantEntity.TenantId = tenantResolver.CurrentTenantId
            ?? throw new InvalidOperationException(
                $"Não é possível inserir '{entity.GetType().Name}': a entidade pertence a um tenant, " +
                "mas não há tenant no contexto atual. Em um request autenticado o tenant sempre existe; " +
                "num job ou no bootstrap, defina o TenantId explicitamente.");
    }
}
