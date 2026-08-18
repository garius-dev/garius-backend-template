using System.Text.Json;
using Google;
using Google.Cloud.SecretManager.V1;
using Grpc.Core;
using Microsoft.Extensions.Configuration;

namespace Garius.Infrastructure.Secrets;

/// <summary>
/// Provider de configuração que carrega os segredos do Google Secret Manager.
///
/// É um <see cref="ConfigurationProvider"/> de verdade (não um método estático que
/// injeta valores em memória): participa da cascata do .NET normalmente, e por ser
/// registrado por <b>último</b> no ConfigurationBuilder, tem a maior precedência —
/// que é a ordem pedida: Secret Manager > variável de ambiente > appsettings.
/// </summary>
internal sealed class GcpSecretManagerConfigurationProvider(GcpSecretManagerOptions options)
    : ConfigurationProvider
{
    public override void Load()
    {
        // ConfigurationProvider.Load é síncrono por contrato do framework, e roda uma
        // única vez no boot (antes de haver requisições). GetAwaiter().GetResult() aqui
        // é seguro: não há SynchronizationContext e não há tráfego para bloquear.
        LoadAsync().GetAwaiter().GetResult();
    }

    private async Task LoadAsync()
    {
        try
        {
            var client = await new SecretManagerServiceClientBuilder().BuildAsync();

            var versionName = new SecretVersionName(options.ProjectId, options.SecretName, options.Version);
            var response = await client.AccessSecretVersionAsync(versionName);

            var payload = response.Payload.Data.ToStringUtf8();

            var values = JsonSerializer.Deserialize<Dictionary<string, string?>>(payload)
                ?? throw new SecretsLoadException(
                    $"O secret '{options.SecretName}' não contém um objeto JSON válido.");

            Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is RpcException or GoogleApiException or JsonException or InvalidOperationException)
        {
            if (!options.Optional)
            {
                // Fail-fast: em produção, subir com configuração parcial é pior do que não
                // subir. A app ficaria de pé, aceitaria tráfego e falharia de formas obscuras
                // (ex.: senha de banco vazia -> erro de conexão a cada request).
                throw new SecretsLoadException(
                    $"Falha ao carregar os segredos do Google Secret Manager " +
                    $"(projeto '{options.ProjectId}', secret '{options.SecretName}'). " +
                    $"A aplicação não sobe com configuração parcial. " +
                    $"Para tolerar a falha e usar env vars/appsettings, defina GcpSecrets:Optional=true.",
                    ex);
            }

            // Optional: degrada em silêncio para as camadas de baixo (env var, appsettings).
            // O Serilog ainda não existe neste ponto do boot, então não há como logar.
            Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }
    }
}

/// <summary>Falha ao carregar segredos de uma origem externa.</summary>
public sealed class SecretsLoadException : Exception
{
    public SecretsLoadException(string message) : base(message) { }

    public SecretsLoadException(string message, Exception innerException) : base(message, innerException) { }
}
