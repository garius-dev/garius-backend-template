using FluentValidation;
using Garius.Core.Entities;
using Garius.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace Garius.Api.Infrastructure.Validation;

/// <summary>
/// Regras reutilizáveis — <b>a regra vive UMA vez, e todo endpoint que precisa dela a reusa</b>.
///
/// <para>
/// <b>O problema que isto resolve.</b> Vários endpoints recebem o mesmo id de produto: criar
/// pedido, adicionar item, aplicar desconto. Todos precisam da mesma pergunta: <i>esse produto
/// existe? está ativo? é do meu tenant?</i> Se cada validator escrever isso à mão, é só uma
/// questão de tempo até um endpoint novo — escrito seis meses depois, com pressa — <b>esquecer</b>.
/// E o esquecimento não quebra nada na hora: ele deixa entrar um FK órfão, ou pior, um id de
/// <b>outro tenant</b>. Você descobre no relatório errado, meses depois.
/// </para>
///
/// <para>
/// Com a regra num lugar só, "existe e está ativo" é <b>uma linha</b> no validator, e mudar o
/// critério muda em todos os lugares de uma vez.
/// </para>
/// </summary>
internal static class ValidationRules
{
    /// <summary>
    /// O id aponta para uma entidade que <b>existe</b>, está <b>ativa</b> e é <b>do meu
    /// tenant</b>.
    ///
    /// <para>
    /// ⚠️ Parece que só checa existência — mas checa as três coisas, e é o
    /// <c>AppDbContext</c> que faz as outras duas de graça: o <b>query filter global</b> já
    /// aplica <c>Enabled = true</c> (soft delete) e o <c>TenantId</c> corrente em toda consulta.
    /// Um registro apagado, ou de outro tenant, <b>simplesmente não existe</b> para esta query.
    /// </para>
    ///
    /// <para>
    /// É por isso que a regra é segura por construção: não há como escrevê-la e esquecer do
    /// tenant. Se alguém usar <c>IgnoreQueryFilters()</c> aqui, quebra as duas garantias de uma
    /// vez — não faça.
    /// </para>
    ///
    /// <example>
    /// <code>
    /// public sealed class CreateOrderValidator : AbstractValidator&lt;CreateOrderRequest&gt;
    /// {
    ///     public CreateOrderValidator(AppDbContext db)
    ///     {
    ///         RuleFor(x =&gt; x.ProductId).MustExist&lt;CreateOrderRequest, Product&gt;(db, "Produto");
    ///         RuleFor(x =&gt; x.Quantity).GreaterThan(0);
    ///     }
    /// }
    /// </code>
    /// </example>
    /// </summary>
    /// <typeparam name="TRequest">O request que está sendo validado.</typeparam>
    /// <typeparam name="TEntity">A entidade que o id precisa apontar.</typeparam>
    /// <param name="rule">A regra em construção (fornecida pelo FluentValidation).</param>
    /// <param name="db">O contexto — é o query filter dele que aplica Enabled e TenantId.</param>
    /// <param name="label">
    /// Como a entidade se chama <b>para o usuário</b> ("Produto", "Cliente"). Vai na mensagem de
    /// erro — o cliente não deve ler o nome da sua classe C#.
    /// </param>
    internal static IRuleBuilderOptions<TRequest, Guid> MustExist<TRequest, TEntity>(
        this IRuleBuilder<TRequest, Guid> rule,
        AppDbContext db,
        string label)
        where TEntity : class, IAuditable
    {
        ArgumentNullException.ThrowIfNull(db);

        return rule
            .MustAsync(async (id, cancellationToken) =>
                id != Guid.Empty
                && await db.Set<TEntity>().AnyAsync(
                    entity => EF.Property<Guid>(entity, "Id") == id,
                    cancellationToken))
            .WithMessage($"{label} não encontrado.");
        // A mensagem é a MESMA para "não existe", "foi apagado" e "é de outro tenant".
        // Distinguir os três entregaria a um atacante um oráculo: ele descobriria QUAIS ids
        // existem em outros tenants só variando o palpite. É a mesma razão pela qual o login
        // não diz se o e-mail está cadastrado.
    }

    /// <summary>Versão para id opcional: <c>null</c> passa, mas um id preenchido tem de valer.</summary>
    /// <typeparam name="TRequest">O request que está sendo validado.</typeparam>
    /// <typeparam name="TEntity">A entidade que o id precisa apontar.</typeparam>
    /// <param name="rule">A regra em construção (fornecida pelo FluentValidation).</param>
    /// <param name="db">O contexto — é o query filter dele que aplica Enabled e TenantId.</param>
    /// <param name="label">Como a entidade se chama para o usuário ("Produto").</param>
    internal static IRuleBuilderOptions<TRequest, Guid?> MustExistIfProvided<TRequest, TEntity>(
        this IRuleBuilder<TRequest, Guid?> rule,
        AppDbContext db,
        string label)
        where TEntity : class, IAuditable
    {
        ArgumentNullException.ThrowIfNull(db);

        return rule
            .MustAsync(async (id, cancellationToken) =>
                id is null
                || (id != Guid.Empty
                    && await db.Set<TEntity>().AnyAsync(
                        entity => EF.Property<Guid>(entity, "Id") == id,
                        cancellationToken)))
            .WithMessage($"{label} não encontrado.");
    }

    /// <summary>
    /// Uma permissão que <b>existe no catálogo</b>. Um escopo digitado errado
    /// (<c>"users.raed"</c>) numa credencial de máquina é pior que um erro comum: ele cria uma
    /// credencial que <b>autentica mas não autoriza nada</b>, e o integrador passa horas
    /// procurando o defeito na integração dele — que está certa.
    ///
    /// <para>
    /// ⚠️ Isto é <b>outra</b> checagem, e não substitui a <c>MachineAuthService.ValidateScopesAsync</c>:
    /// lá se verifica se quem cria a credencial <b>tem</b> o poder que está delegando (a defesa
    /// contra escalada de privilégio, que precisa saber QUEM é o chamador). Aqui só se verifica
    /// se o escopo <b>existe</b>. As duas são necessárias, e nenhuma cobre a outra.
    /// </para>
    /// </summary>
    internal static IRuleBuilderOptions<TRequest, string> MustBeAKnownPermission<TRequest>(
        this IRuleBuilder<TRequest, string> rule) =>
        rule.Must(scope =>
                !string.IsNullOrWhiteSpace(scope)
                && (scope == Core.Authorization.Permissions.SuperAdmin
                    || Core.Authorization.Permissions.Exists(scope)))
            .WithMessage("'{PropertyValue}' não é uma permissão conhecida.");
}
