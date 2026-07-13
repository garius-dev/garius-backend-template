using Garius.Core.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Garius.Infrastructure.Security;

public static class SecurityExtensions
{
    /// <summary>
    /// Registra a criptografia de campo (AES-256-GCM), o índice cego (HMAC-SHA256) e o
    /// portal auditado de leitura de PII.
    ///
    /// <para>
    /// O <see cref="IFieldEncryptor"/> é <b>singleton</b> e devolvido, porque o mapeamento do
    /// EF precisa dele ao construir o modelo — antes de existir um scope.
    /// </para>
    /// </summary>
    public static IFieldEncryptor AddFieldEncryption(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<EncryptionOptions>(configuration.GetSection(EncryptionOptions.SectionName));

        var options = new EncryptionOptions();
        configuration.GetSection(EncryptionOptions.SectionName).Bind(options);

        var wrapped = Microsoft.Extensions.Options.Options.Create(options);

        // Instanciados aqui (e não só resolvidos do DI) porque o OnModelCreating precisa do
        // encryptor. Falham no construtor se a chave não estiver configurada — fail-fast:
        // uma app que sobe sem chave gravaria PII em claro, ou quebraria no primeiro insert.
        var encryptor = new AesGcmFieldEncryptor(wrapped);
        var blindIndex = new HmacBlindIndex(wrapped);

        services.AddSingleton<IFieldEncryptor>(encryptor);
        services.AddSingleton<IBlindIndex>(blindIndex);

        services.AddScoped<IPiiReader, PiiReader>();

        return encryptor;
    }
}
