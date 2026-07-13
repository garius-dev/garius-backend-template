using System.Collections.Concurrent;
using Garius.Core.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Garius.Infrastructure.Messaging;

/// <summary>
/// Traduz o <b>nome</b> do evento (que é o que está gravado no banco) de volta para o
/// <see cref="Type"/>.
///
/// <para>
/// Existe porque o payload sai do banco como <c>("UserCreated", "{...}")</c>: uma string e um
/// JSON. Para desserializar é preciso o <c>Type</c>, e varrer os assemblies a cada mensagem
/// seria caro. O registro é populado no boot, ao registrar cada handler.
/// </para>
/// </summary>
public sealed class EventTypeRegistry
{
    private readonly ConcurrentDictionary<string, Type> _types = new(StringComparer.Ordinal);

    internal void Register<TEvent>() where TEvent : IDomainEvent =>
        _types[typeof(TEvent).Name] = typeof(TEvent);

    /// <summary>
    /// O tipo do evento, ou <c>null</c> se ninguém registrou um handler para ele — o que não
    /// é um erro. Ver <c>OutboxProcessor.ProcessOneAsync</c>.
    /// </summary>
    public Type? Resolve(string typeName) => _types.GetValueOrDefault(typeName);
}

public static class MessagingExtensions
{
    /// <summary>
    /// O registro de tipos, criado <b>aqui</b> e não pelo container.
    ///
    /// <para>
    /// Precisa existir antes do <c>Build()</c>, porque é o <c>AddEventHandler&lt;,&gt;()</c>
    /// que o popula — e ele roda na fase de <b>configuração</b> do DI, quando ainda não há
    /// provider. A alternativa (um <c>BuildServiceProvider()</c> no meio do registro) criaria
    /// um provider descartável e uma <b>segunda instância</b> de todo singleton já registrado:
    /// é a má prática que o próprio ASP.NET avisa em runtime.
    /// </para>
    /// </summary>
    private static readonly EventTypeRegistry Registry = new();

    /// <summary>
    /// Registra o outbox. Chame <b>uma vez</b>, no <c>Program.cs</c>.
    /// </summary>
    public static IServiceCollection AddOutbox(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(Registry);
        services.AddScoped<IOutbox, Outbox>();
        services.AddScoped<OutboxProcessor>();

        return services;
    }

    /// <summary>
    /// Registra um handler de evento — é assim que uma aplicação derivada reage a um evento.
    ///
    /// <code>
    /// builder.Services.AddEventHandler&lt;UserCreated, SendWelcomeEmail&gt;();
    /// </code>
    ///
    /// <para>
    /// Um mesmo evento pode ter <b>vários</b> handlers (todos são chamados). Registrar o
    /// handler é o que também registra o <b>tipo</b> do evento no
    /// <see cref="EventTypeRegistry"/> — sem isso, o drenador não saberia desserializar o
    /// payload, e a mensagem seria descartada como "sem handler".
    /// </para>
    /// </summary>
    public static IServiceCollection AddEventHandler<TEvent, THandler>(
        this IServiceCollection services)
        where TEvent : IDomainEvent
        where THandler : class, IEventHandler<TEvent>
    {
        ArgumentNullException.ThrowIfNull(services);

        // Popula o mapa nome → Type NO BOOT. Sem isto, o drenador receberia "UserCreated" do
        // banco e não saberia para qual tipo desserializar — trataria a mensagem como "sem
        // handler" e a descartaria em silêncio.
        Registry.Register<TEvent>();

        // Scoped, não singleton: um handler quase sempre injeta um DbContext (que é scoped).
        // Registrado como singleton, ele capturaria um DbContext e o manteria vivo para
        // sempre — o "captive dependency", que vaza conexão e serve dado velho.
        services.AddScoped<IEventHandler<TEvent>, THandler>();

        return services;
    }
}
