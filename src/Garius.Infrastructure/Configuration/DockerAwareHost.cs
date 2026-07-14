using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Garius.Infrastructure.Configuration;

/// <summary>
/// Faz o <b>mesmo</b> secret servir a sua máquina e o servidor.
///
/// <para>
/// <b>O problema.</b> O Secret Manager guarda os endereços de <b>produção</b> —
/// <c>Database:Host = postgres-prod</c>, <c>Redis:ConnectionString = redis-prod:6379</c> — porque
/// dentro de um container o endereço é o <b>nome do outro container</b> (<c>localhost</c>, ali,
/// é o próprio container). Mas o secret é <b>um só</b>: rodando na sua máquina, com
/// <c>dotnet run</c>, esses nomes não resolvem. O Postgres e o Redis estão em
/// <c>localhost</c>.
/// </para>
///
/// <para>
/// <b>A saída.</b> A aplicação troca o host por <c>localhost</c> <b>sozinha</b> — e apenas quando
/// as duas condições valem:
/// </para>
///
/// <list type="bullet">
///   <item><b>é Development</b> — em produção o valor do secret vale como está, sempre; e</item>
///   <item><b>NÃO está num container</b> (<c>DOCKER_RUN</c> ausente) — porque dentro do
///         container, mesmo em Development, o nome do container é o endereço certo.</item>
/// </list>
///
/// <para>
/// É o que permite <b>um secret só</b>, sem duas cópias de nada para manter em sincronia — e é
/// exatamente o padrão que as aplicações em produção já usam.
/// </para>
/// </summary>
public static class DockerAwareHost
{
    /// <summary>A env var que o <c>docker-compose</c> injeta. Presente = estamos num container.</summary>
    public const string DockerRunKey = "DOCKER_RUN";

    /// <summary>
    /// Rodando na máquina do desenvolvedor, fora do Docker? Só então os endereços do secret
    /// (que são os de produção) precisam virar <c>localhost</c>.
    /// </summary>
    public static bool IsLocalDevelopment(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var inDocker = configuration.GetValue<bool?>(DockerRunKey) ?? false;

        return environment.IsDevelopment() && !inDocker;
    }

    /// <summary>
    /// O host, resolvido para o ambiente. Em desenvolvimento local devolve <c>localhost</c>;
    /// em qualquer outro caso, o valor como veio.
    /// </summary>
    /// <param name="host">O host como veio da configuração (do secret, em produção).</param>
    /// <param name="configuration">A configuração — para ler o <c>DOCKER_RUN</c>.</param>
    /// <param name="environment">O ambiente — para saber se é <c>Development</c>.</param>
    /// <param name="onResolved">
    /// Chamado quando a troca acontece — com a chave, o valor original e o resolvido. É por aqui
    /// que a troca vai para o LOG, e isso não é ruído: sem ela, o comportamento é mágica
    /// silenciosa. Quando o secret aponta para um host que não existe na sua máquina, o erro é um
    /// "No such host is known" cru — e nada liga uma coisa à outra. Você fica olhando para o
    /// appsettings, que diz `localhost`, sem entender.
    /// </param>
    public static string Resolve(
        string host,
        IConfiguration configuration,
        IHostEnvironment environment,
        Action<string, string, string>? onResolved = null)
    {
        if (!IsLocalDevelopment(configuration, environment)
            || string.IsNullOrWhiteSpace(host)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return host;
        }

        onResolved?.Invoke("Database:Host", host, "localhost");

        return "localhost";
    }

    /// <summary>
    /// O mesmo, para a connection string do Redis (<c>host:porta</c>, com opções depois da
    /// vírgula). Troca <b>só o host</b> — a porta e as opções seguem intactas, e a senha nem
    /// passa por aqui (ela vem separada, em <c>Redis:Password</c>).
    /// </summary>
    /// <param name="connectionString">A connection string como veio da configuração.</param>
    /// <param name="configuration">A configuração — para ler o <c>DOCKER_RUN</c>.</param>
    /// <param name="environment">O ambiente — para saber se é <c>Development</c>.</param>
    /// <param name="onResolved">Chamado quando a troca acontece. Ver <see cref="Resolve"/>.</param>
    public static string ResolveRedis(
        string connectionString,
        IConfiguration configuration,
        IHostEnvironment environment,
        Action<string, string, string>? onResolved = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString)
            || !IsLocalDevelopment(configuration, environment))
        {
            return connectionString;
        }

        // "redis-prod:6379,abortConnect=false" -> "localhost:6379,abortConnect=false"
        var parts = connectionString.Split(',', 2);
        var endpoint = parts[0];

        var port = endpoint.Contains(':', StringComparison.Ordinal)
            ? endpoint[(endpoint.LastIndexOf(':') + 1)..]
            : "6379";

        var resolved = $"localhost:{port}";

        if (!endpoint.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
        {
            onResolved?.Invoke("Redis:ConnectionString", endpoint, resolved);
        }

        return parts.Length == 2 ? $"{resolved},{parts[1]}" : resolved;
    }
}
