using System.Globalization;
using System.Text.Json;
using Garius.Core.Authorization;
using Garius.Infrastructure.Caching;
using Garius.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Garius.Infrastructure.Authorization;

/// <summary>
/// Resolve e cacheia as permissões efetivas de um usuário — <b>no Redis</b>.
///
/// <para>
/// <b>Por que não em memória.</b> Era assim até a Fase 5, e era um bug esperando a segunda
/// réplica: cada processo tinha o seu <c>IMemoryCache</c>, e uma invalidação numa réplica
/// <b>não alcançava as outras</b>. Revogar o acesso de alguém funcionava na réplica que
/// atendeu o request de revogação — e nas demais o usuário continuava entrando, até o TTL
/// expirar. Com o balanceador jogando o usuário de um lado para o outro, o acesso revogado
/// funcionava <b>de forma intermitente</b> por minutos, que é a pior forma de um bug de
/// segurança se manifestar: parece flakiness, não parece falha.
/// </para>
///
/// <para>
/// No Redis o cache é <b>um só</b>, compartilhado por todas as réplicas. Invalidar numa
/// invalida em todas, e a revogação passa a ser imediata de verdade.
/// </para>
/// </summary>
internal sealed class RedisPermissionResolver(
    AppDbContext dbContext,
    IConnectionMultiplexer redis,
    RedisOptions options,
    ILogger<RedisPermissionResolver> logger) : IPermissionResolver
{
    /// <summary>
    /// <b>Rede de segurança, não o mecanismo de invalidação.</b> A invalidação é por evento
    /// (imediata). Este TTL só existe para que uma entrada órfã — de um usuário que ninguém
    /// mais toca — não fique no Redis para sempre.
    ///
    /// <para>
    /// Por isso ele pode ser generoso: não é ele que garante a revogação. Quando era um cache
    /// em memória, o TTL curto (5 min) era a única defesa contra a invalidação que não
    /// alcançava as outras réplicas — e mesmo assim deixava uma janela de 5 minutos.
    /// </para>
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        var db = redis.GetDatabase();

        var generation = await GetGenerationAsync(db);
        var key = BuildKey(generation, userId, tenantId);

        var cached = await db.StringGetAsync(key);

        if (!cached.IsNullOrEmpty)
        {
            // O cast explícito é obrigatório: RedisValue converte implicitamente tanto para
            // string quanto para ReadOnlySpan<byte>, e o compilador não consegue escolher.
            var permissions = JsonSerializer.Deserialize<string[]>((string)cached!);

            if (permissions is not null)
            {
                return permissions.ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }

        var loaded = await LoadAsync(userId, tenantId, cancellationToken);

        await db.StringSetAsync(
            key,
            JsonSerializer.Serialize(loaded.ToArray()),
            CacheTtl);

        return loaded;
    }

    public Task InvalidateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // A chave inclui o tenant, e um usuário pode estar em vários — mas não sabemos em
        // quais sem consultar o banco. Bumpar a geração invalida tudo, o que é mais barato
        // (e mais seguro) do que arriscar deixar uma entrada obsoleta de pé.
        return InvalidateAllAsync(cancellationToken);
    }

    /// <summary>
    /// Invalida <b>tudo</b>, em todas as réplicas, incrementando um contador de geração.
    ///
    /// <para>
    /// <b>Por que uma geração e não um <c>SCAN</c> + <c>DEL</c>.</b> O Redis não indexa chaves
    /// por padrão: apagar "todas as chaves de permissão" exigiria varrer o keyspace inteiro —
    /// uma operação O(n) sobre um banco que também está servindo produção. O <c>INCR</c> é
    /// atômico, O(1), e as entradas velhas simplesmente deixam de ser encontradas (a geração
    /// faz parte da chave) e expiram sozinhas pelo TTL.
    /// </para>
    /// </summary>
    public async Task InvalidateAllAsync(CancellationToken cancellationToken = default)
    {
        var db = redis.GetDatabase();

        // INCR é atômico: duas réplicas invalidando ao mesmo tempo não se atropelam.
        var generation = await db.StringIncrementAsync(GenerationKey);

        logger.LogInformation(
            "Cache de permissões invalidado em TODAS as réplicas (geração {Generation})",
            generation);
    }

    /// <summary>
    /// A geração corrente. Se a chave não existe (Redis novo, ou a chave expirou), vale zero —
    /// e não há nada em cache mesmo, então não há o que invalidar.
    /// </summary>
    private async Task<long> GetGenerationAsync(IDatabase db)
    {
        var value = await db.StringGetAsync(GenerationKey);

        return value.IsNullOrEmpty
            ? 0
            : long.Parse((string)value!, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Duas origens, achatadas num conjunto só:
    ///
    /// <list type="number">
    ///   <item>as <b>claims dos papéis</b> do usuário — o caminho normal;</item>
    ///   <item>as <b>claims diretas</b> do usuário — a exceção pontual.</item>
    /// </list>
    ///
    /// <para>
    /// Os query filters do <c>AppDbContext</c> já excluem usuário, papel e vínculo
    /// desabilitados — desabilitar um papel <b>revoga</b> as permissões dele.
    /// </para>
    /// </summary>
    private async Task<IReadOnlySet<string>> LoadAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        // Permissões vindas dos papéis. Um papel global (TenantId = null) vale em todos os
        // tenants; um papel de tenant só vale no dele.
        var fromRoles = await dbContext.UserRoles
            .Where(ur => ur.UserId == userId)
            .Where(ur => ur.Role.TenantId == null || ur.Role.TenantId == tenantId)
            .SelectMany(ur => ur.Role.Claims)
            .Where(c => c.ClaimType == Permission.ClaimType)
            .Select(c => c.ClaimValue!)
            .ToListAsync(cancellationToken);

        // Permissões avulsas, concedidas direto ao usuário.
        var direct = await dbContext.UserClaims
            .Where(c => c.UserId == userId)
            .Where(c => c.ClaimType == Permission.ClaimType)
            .Where(c => c.TenantId == null || c.TenantId == tenantId)
            .Select(c => c.ClaimValue!)
            .ToListAsync(cancellationToken);

        return fromRoles
            .Concat(direct)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private string GenerationKey => $"{options.InstanceName}:perm-generation";

    private string BuildKey(long generation, Guid userId, Guid? tenantId) =>
        $"{options.InstanceName}:perm:{generation}:{userId}:{tenantId}";
}
