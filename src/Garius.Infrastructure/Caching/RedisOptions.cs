namespace Garius.Infrastructure.Caching;

/// <summary>Seção <c>Redis</c>. A senha vem do Secret Manager, nunca do appsettings.</summary>
public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    /// <summary>Ex.: <c>localhost:6379</c> ou <c>redis:6379</c> (rede Docker).</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Do Secret Manager: <c>Redis:Password</c>. Vazio em desenvolvimento.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Prefixo de todas as chaves desta aplicação.
    ///
    /// <para>
    /// Necessário porque várias aplicações compartilham a mesma instância de Redis: sem o
    /// prefixo, o keyring de DataProtection de uma sobrescreveria o da outra — e os cookies
    /// de todas parariam de funcionar ao mesmo tempo.
    /// </para>
    /// </summary>
    public string InstanceName { get; set; } = string.Empty;
}
