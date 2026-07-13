namespace Garius.Tests.Infrastructure;

/// <summary>
/// Todas as classes que sobem a <b>API real</b> compartilham <b>uma única</b>
/// <see cref="ApiFactory"/> — e, portanto, um único par de containers.
///
/// <para>
/// <b>Não é otimização: é correção.</b> O <c>ApiFactory</c> configura a aplicação por
/// <b>variável de ambiente</b> (é o único jeito de vencer o <c>appsettings.Development.json</c>,
/// que aponta para o Postgres local do desenvolvedor — ver o comentário lá). Variável de
/// ambiente é estado <b>global do processo</b>.
/// </para>
///
/// <para>
/// Sem esta collection, o xUnit roda as classes <b>em paralelo</b>, cada uma com a sua fábrica:
/// duas instâncias escrevendo as mesmas variáveis, e a primeira a terminar <b>apaga as
/// variáveis e destrói os containers</b> das outras — que quebram no meio com
/// <c>"Failed to connect"</c> e <c>"target machine actively refused it"</c>. A falha é de
/// corrida, então ela varia de execução para execução e some no <c>--no-build</c>, o que a
/// torna especialmente traiçoeira.
/// </para>
///
/// <para>
/// <b>Uma classe nova que use <see cref="ApiFactory"/> precisa entrar aqui</b> — via
/// <c>[Collection(ApiCollection.Name)]</c> e <c>ICollectionFixture</c>, não
/// <c>IClassFixture</c>.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "Api";

    // Sem corpo, de propósito: esta classe só carrega os atributos. É o xUnit que a instancia
    // e injeta a ApiFactory nas classes da collection.
}
