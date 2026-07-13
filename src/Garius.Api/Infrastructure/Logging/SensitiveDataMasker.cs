using Serilog.Core;
using Serilog.Events;

namespace Garius.Api.Infrastructure.Logging;

/// <summary>
/// Redige valores sensíveis <b>por nome de propriedade</b>, em qualquer evento de log.
///
/// <para>
/// Atua no destino final (o evento pronto), não na origem — então protege mesmo quando
/// o dado chega por um caminho que ninguém previu (um objeto destruturado, o corpo de
/// uma exceção de model binding, um DTO logado por engano).
/// </para>
///
/// <para>
/// O template anterior tinha um filtro que excluía eventos com a propriedade
/// <c>RequestBody</c> — mas nada no código jamais adicionava essa propriedade. O filtro
/// era decorativo. Este enricher não depende de ninguém lembrar de nada.
/// </para>
/// </summary>
internal sealed class SensitiveDataMasker : ILogEventEnricher
{
    private const string Redacted = "[REDACTED]";

    /// <summary>
    /// Comparação por "contém", case-insensitive: pega <c>Password</c>, <c>NewPassword</c>,
    /// <c>currentPassword</c>, <c>ClientSecret</c>, <c>refresh_token</c>, etc.
    /// </summary>
    private static readonly string[] SensitiveNames =
    [
        "password",
        "senha",
        "secret",
        "token",
        "authorization",
        "apikey",
        "api_key",
        "credential",
        "privatekey",
        "connectionstring",
        "cookie",
        // PII sob LGPD: nunca em texto puro no log.
        "cpf",
        "email"
    ];

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        foreach (var property in logEvent.Properties)
        {
            if (IsSensitive(property.Key))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, new ScalarValue(Redacted)));
            }
        }
    }

    private static bool IsSensitive(string propertyName)
    {
        foreach (var sensitive in SensitiveNames)
        {
            if (propertyName.Contains(sensitive, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
