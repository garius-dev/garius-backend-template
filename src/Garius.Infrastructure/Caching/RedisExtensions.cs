using Microsoft.AspNetCore.DataProtection;
using Garius.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Garius.Infrastructure.Caching;

public static class RedisExtensions
{
    /// <summary>
    /// Redis: <b>dependência obrigatória</b>, não opcional.
    ///
    /// <para>
    /// A autenticação depende dele (refresh tokens, DataProtection). O template anterior
    /// subia "degradado" quando o Redis estava fora — e então <b>toda requisição de login
    /// estourava 500</b>, porque os serviços que injetavam o <c>IConnectionMultiplexer</c>
    /// não o encontravam no DI. Aqui, o boot <b>falha</b> — alto e claro.
    /// </para>
    /// </summary>
    public static IServiceCollection AddRedis(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        string applicationName,
        Action<string, string, string>? onHostResolved = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var options = new RedisOptions();
        configuration.GetSection(RedisOptions.SectionName).Bind(options);

        // O secret guarda o endereço de PRODUÇÃO (o nome do container). Rodando na máquina, fora
        // do Docker, ele vira `localhost` — mantendo a porta e as opções. Ver DockerAwareHost.
        options.ConnectionString = DockerAwareHost.ResolveRedis(
            options.ConnectionString,
            configuration,
            environment,
            onResolved: onHostResolved);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException(
                "Redis:ConnectionString não foi configurado. O Redis é obrigatório: a autenticação " +
                "(refresh tokens) e o DataProtection (que cifra o cookie) dependem dele. " +
                "Subir sem Redis faria toda requisição de login falhar com 500.");
        }

        if (string.IsNullOrWhiteSpace(options.InstanceName))
        {
            // Isola as chaves desta aplicação das das outras que compartilham o mesmo Redis.
            options.InstanceName = applicationName;
        }

        services.AddSingleton(options);

        var configurationOptions = ConfigurationOptions.Parse(options.ConnectionString);

        if (!string.IsNullOrWhiteSpace(options.Password))
        {
            configurationOptions.Password = options.Password;
        }

        // AbortOnConnectFail = true: falhar no boot é melhor do que subir e falhar em cada
        // request. Um container que não sobe é visível; um que sobe quebrado, não.
        configurationOptions.AbortOnConnectFail = true;
        configurationOptions.ConnectTimeout = 5_000;

        var multiplexer = ConnectionMultiplexer.Connect(configurationOptions);

        services.AddSingleton<IConnectionMultiplexer>(multiplexer);

        // ⚠️ DataProtection NO REDIS — não no filesystem.
        //
        // O DataProtection cifra o cookie de autenticação. Com o keyring em disco, cada
        // réplica gera o seu: a réplica B não consegue ler o cookie emitido pela réplica A, e
        // o usuário é deslogado aleatoriamente conforme o balanceador o joga de um lado para
        // o outro. Um restart invalida todos os cookies de uma vez.
        //
        // O prefixo de chave isola o keyring desta aplicação — sem ele, duas aplicações no
        // mesmo Redis sobrescreveriam o keyring uma da outra.
        services.AddDataProtection()
                .PersistKeysToStackExchangeRedis(multiplexer, $"{options.InstanceName}:dp-keys")
                .SetApplicationName(options.InstanceName);

        return services;
    }
}
