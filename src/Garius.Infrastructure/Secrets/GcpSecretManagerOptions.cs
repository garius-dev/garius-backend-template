namespace Garius.Infrastructure.Secrets;

/// <summary>
/// Configuração da origem de segredos no Google Secret Manager.
/// Lida da seção <c>GcpSecrets</c>.
/// </summary>
public sealed class GcpSecretManagerOptions
{
    public const string SectionName = "GcpSecrets";

    public bool Enabled { get; set; }

    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// Nome do secret. Ele contém um <b>único JSON flat</b> com as chaves no formato
    /// de configuração do .NET:
    /// <code>
    /// {
    ///   "Database:Password": "...",
    ///   "Redis:Password":    "...",
    ///   "Jwt:PrivateKeyPem": "..."
    /// }
    /// </code>
    /// Um secret só = uma chamada de rede no boot, e um item para gerenciar no GCP.
    /// </summary>
    public string SecretName { get; set; } = string.Empty;

    /// <summary>Versão do secret. <c>latest</c> por padrão.</summary>
    public string Version { get; set; } = "latest";

    /// <summary>
    /// Se <c>true</c>, uma falha ao carregar os segredos <b>não</b> derruba o boot —
    /// a aplicação cai para as camadas de baixo (env var, appsettings).
    ///
    /// Deve ser <c>true</c> em Development (para rodar sem credencial do GCP) e
    /// <c>false</c> em Production (subir com configuração parcial é pior do que não subir:
    /// a app fica de pé, aceita tráfego e falha de formas obscuras).
    /// </summary>
    public bool Optional { get; set; }
}
