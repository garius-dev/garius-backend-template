using System.Reflection;
using FluentValidation;

namespace Garius.Api.Infrastructure.Validation;

/// <summary>
/// Liga a validação automática de request.
///
/// <para>
/// <b>O contrato para quem escreve uma feature é UMA coisa:</b> crie um
/// <c>AbstractValidator&lt;SeuRequest&gt;</c> ao lado do endpoint. Ele é descoberto sozinho,
/// registrado sozinho e aplicado sozinho. Não há passo dois, e não há chamada para esquecer.
/// </para>
///
/// <para>
/// A consequência prática é a que importa: <b>o dia em que alguém criar um endpoint novo que
/// aceita um request já validado em outro lugar, ele nasce protegido</b> — mesmo que essa pessoa
/// nunca tenha ouvido falar deste arquivo.
/// </para>
/// </summary>
internal static class ValidationSetup
{
    /// <summary>
    /// Registra todos os <c>AbstractValidator&lt;T&gt;</c> do assembly.
    ///
    /// <para>
    /// <b>Scoped</b>, e não singleton: um validator pode injetar o <c>AppDbContext</c> (para
    /// checar que um FK existe e está ativo). Um singleton capturaria o DbContext de um request e
    /// o reusaria em todos os outros — a "captive dependency" clássica, que aparece como um
    /// <c>ObjectDisposedException</c> intermitente, em produção, sob carga.
    /// </para>
    /// </summary>
    internal static IServiceCollection AddRequestValidation(
        this IServiceCollection services,
        Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddValidatorsFromAssembly(assembly, ServiceLifetime.Scoped);

        // Guarda QUAIS tipos têm validator, para a convenção poder perguntar sem RESOLVER nada.
        //
        // ⚠️ Perguntar ao IServiceProvider (`GetService(typeof(IValidator<T>))`) na hora de montar
        // o endpoint NÃO funciona: os validators são SCOPED, e resolver um serviço scoped a
        // partir do provider RAIZ lança "Cannot resolve scoped service from root provider".
        // A aplicação subia e TODO endpoint respondia 500.
        //
        // A pergunta certa é sobre o REGISTRO ("existe um IValidator<T>?"), não sobre a
        // INSTÂNCIA ("me dê um IValidator<T>") — e o registro é conhecido aqui, no boot.
        var validatedTypes = services
            .Where(descriptor =>
                descriptor.ServiceType.IsGenericType
                && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IValidator<>))
            .Select(descriptor => descriptor.ServiceType.GetGenericArguments()[0])
            .ToHashSet();

        services.AddSingleton(new ValidatedRequestTypes(validatedTypes));

        return services;
    }

    /// <summary>
    /// Os tipos de request que têm validator. Calculado no boot, consultado ao montar cada
    /// endpoint — nunca em runtime.
    /// </summary>
    internal sealed class ValidatedRequestTypes(HashSet<Type> types)
    {
        internal bool Has(Type requestType) => types.Contains(requestType);
    }

    /// <summary>
    /// Aplica a validação a <b>todo endpoint do grupo cujo request tenha um validator
    /// registrado</b>. Um endpoint sem validator para o seu tipo de corpo passa direto.
    ///
    /// <para>
    /// É aqui que "impossível esquecer" deixa de ser promessa. A alternativa seria exigir um
    /// <c>.WithValidation&lt;CreateProductRequest&gt;()</c> em cada <c>MapPost</c> — e bastaria
    /// <b>um</b> esquecimento para um endpoint aceitar dado inválido em silêncio. Um esquecimento
    /// que não quebra nada, não loga nada, e só aparece quando o dado sujo já está no banco.
    /// </para>
    ///
    /// <para>
    /// Aplique no <b>grupo</b> (<c>app.MapGroup("/produtos").ValidateRequests()</c>) e todo
    /// endpoint dentro dele é coberto, inclusive os que ainda não existem.
    /// </para>
    ///
    /// <para>
    /// A descoberta acontece <b>uma vez, no boot</b> — o custo em runtime é o de um filtro que
    /// só existe onde há o que validar.
    /// </para>
    /// </summary>
    internal static TBuilder ValidateRequests<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Add(endpointBuilder =>
        {
            var methodInfo = endpointBuilder.Metadata.OfType<MethodInfo>().FirstOrDefault();

            if (methodInfo is null)
            {
                return;
            }

            // O REGISTRO dos validators (calculado no boot), não uma instância deles: resolver um
            // serviço scoped a partir do provider raiz lançaria. Ver AddRequestValidation.
            var validated = endpointBuilder.ApplicationServices
                .GetRequiredService<ValidatedRequestTypes>();

            foreach (var parameter in methodInfo.GetParameters())
            {
                var requestType = parameter.ParameterType;

                // Só o que PODE ser um corpo de request: uma classe/record nosso. Um
                // IValidator<string> ou IValidator<HttpContext> não existe, e nem faria sentido.
                if (!requestType.IsClass || requestType == typeof(string))
                {
                    continue;
                }

                // Existe validator para este tipo? Se não, este parâmetro não é validável.
                if (!validated.Has(requestType))
                {
                    continue;
                }

                var filterType = typeof(ValidationFilter<>).MakeGenericType(requestType);

                endpointBuilder.FilterFactories.Add((_, next) => async invocationContext =>
                {
                    // Resolvido POR REQUEST, do scope do request: o validator pode depender do
                    // AppDbContext, que é scoped. Pegá-lo do provider raiz o transformaria numa
                    // captive dependency.
                    var filter = (IEndpointFilter)ActivatorUtilities.CreateInstance(
                        invocationContext.HttpContext.RequestServices,
                        filterType);

                    return await filter.InvokeAsync(invocationContext, next);
                });
            }
        });

        return builder;
    }
}
