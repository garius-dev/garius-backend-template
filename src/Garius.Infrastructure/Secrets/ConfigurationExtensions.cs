using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Garius.Infrastructure.Secrets;

public static class ConfigurationExtensions
{
    /// <summary>
    /// Monta a cascata de configuração pedida:
    ///
    /// <code>
    /// Google Secret Manager  (maior precedência — a fonte padrão em produção)
    ///        ↓ se não achar
    /// variável de ambiente
    ///        ↓ se não achar
    /// appsettings.{Environment}.json
    ///        ↓ se não achar
    /// appsettings.json       (menor precedência)
    /// </code>
    ///
    /// No .NET, <b>o último provider registrado vence</b>. O host já registrou
    /// appsettings e env vars nessa ordem; basta acrescentar o Secret Manager por último.
    ///
    /// Em <b>Development</b> a origem é opcional (roda sem credencial do GCP).
    /// Em <b>Production</b> ela é obrigatória: falhar o boot é melhor do que subir
    /// com metade da configuração.
    /// </summary>
    public static IConfigurationBuilder AddSecretSources(
        this IConfigurationBuilder builder,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(environment);

        // Lê a própria configuração de GcpSecrets a partir do que já foi registrado
        // (appsettings + env vars) — os metadados de "onde buscar" não são segredo.
        var bootstrapConfig = builder.Build();

        var options = new GcpSecretManagerOptions();
        bootstrapConfig.GetSection(GcpSecretManagerOptions.SectionName).Bind(options);

        if (!options.Enabled)
        {
            return builder;
        }

        if (string.IsNullOrWhiteSpace(options.ProjectId) || string.IsNullOrWhiteSpace(options.SecretName))
        {
            throw new SecretsLoadException(
                "GcpSecrets:Enabled=true, mas GcpSecrets:ProjectId ou GcpSecrets:SecretName não foram configurados.");
        }

        // Fora de Production, uma falha do GCP degrada para as camadas de baixo (env var,
        // appsettings) em vez de derrubar o boot. Em Production, falha o boot: subir com
        // configuração parcial é pior do que não subir.
        //
        // Pode ser forçado por GcpSecrets:Optional — útil, por exemplo, para quem receber
        // esta aplicação e quiser rodá-la sem GCP nenhum.
        if (!bootstrapConfig.GetSection($"{GcpSecretManagerOptions.SectionName}:Optional").Exists())
        {
            options.Optional = !environment.IsProduction();
        }

        builder.Add(new GcpSecretManagerConfigurationSource(options));

        return builder;
    }
}
