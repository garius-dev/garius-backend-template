using System.Reflection;
using System.Text.Json;
using Garius.Infrastructure.Database;

namespace Garius.Tests.Configuration;

/// <summary>
/// A rede de segurança de quem <b>deriva</b> este template.
///
/// <para>
/// <b>O erro que estes testes existem para pegar.</b> O <c>Database:ApplicationName</c> é a fonte
/// única dos nomes no Postgres: dele saem <c>db_{slug}</c>, <c>hangfire_{slug}</c> e
/// <c>{slug}_user</c>. Quem copia o template e esquece de trocá-lo cria uma aplicação que
/// <b>compila, sobe e funciona</b> — apontando para o <b>mesmo banco e o mesmo usuário</b> de todas
/// as outras que também esqueceram. A colisão é silenciosa até duas aplicações se atropelarem em
/// produção, e aí o diagnóstico é caríssimo (dados de uma aparecendo na outra).
/// </para>
///
/// <para>
/// O <c>dotnet new</c> troca esse valor automaticamente. Estes testes são o que pega o caso em que
/// alguém <b>não</b> usou o <c>dotnet new</c> — copiou a pasta à mão, ou pediu a uma IA para
/// renomear. Transformam o erro mais caro do template num erro de <b>build</b>.
/// </para>
/// </summary>
public class TemplateDerivationTests
{
    /// <summary>
    /// O <c>ApplicationName</c> que vem no template — o valor que uma aplicação derivada <b>tem</b>
    /// de trocar.
    ///
    /// <para>
    /// ⚠️ <b>Montado por concatenação, e não escrito inteiro.</b> Parece paranoia, e não é: o
    /// <c>dotnet new</c> substitui o nome do template <b>em todo o conteúdo dos arquivos</b> —
    /// inclusive dentro <b>deste teste</b>. Escrito por extenso, o literal viraria o nome da app
    /// derivada, e o teste passaria a afirmar "o ApplicationName não pode ser MinhaApp.Backend" —
    /// exatamente o valor <b>correto</b>. O guarda se autodestruiria, em silêncio, e ninguém
    /// perceberia porque ele nunca mais falharia.
    /// </para>
    ///
    /// <para>
    /// (Descoberto derivando o template de verdade e rodando a suíte — não olhando o código.)
    /// </para>
    /// </summary>
    private static string TemplateApplicationName =>
        $"{Marker}Tech.Backend.Template";

    /// <summary>
    /// O prefixo dos assemblies do template. Mesma razão — ver
    /// <see cref="TemplateApplicationName"/>.
    /// </summary>
    private static string TemplateAssemblyPrefix => $"{Marker}.";

    /// <summary>
    /// A palavra que o <c>dotnet new</c> procura e substitui — <b>quebrada em duas metades</b>
    /// para que ele <b>não a encontre aqui</b>.
    ///
    /// <para>
    /// Sem a quebra, o substituidor a trocaria pelo nome da app derivada — e o guarda passaria a
    /// comparar o <c>ApplicationName</c> com um valor que <b>nunca</b> ocorre, deixando de falhar
    /// para sempre. É a armadilha do teste que se autodesativa: ele continua verde, e por isso
    /// ninguém descobre que ele parou de vigiar. (Foi o que aconteceu na primeira versão deste
    /// arquivo — e só apareceu ao derivar o template de verdade e ler o resultado.)
    /// </para>
    /// </summary>
    private const string Marker = "Gari" + "us";

    /// <summary>
    /// ⚠️ <b>Este teste é ignorado NO PRÓPRIO TEMPLATE, e falha em qualquer app derivada que não
    /// tenha trocado o nome.</b>
    ///
    /// <para>
    /// A distinção é feita pelo <b>nome do assembly de testes</b>: no template ele é
    /// <c>Garius.Tests</c>; numa app derivada pelo <c>dotnet new</c>, ele vira
    /// <c>MinhaApp.Tests</c>. Ou seja: o teste só cobra o <c>ApplicationName</c> de quem já
    /// renomeou os projetos — que é exatamente quem está construindo uma aplicação de verdade.
    /// </para>
    ///
    /// <para>
    /// Sem essa distinção, o teste estaria <b>permanentemente vermelho no template</b> — e um teste
    /// que sempre falha é um teste que todo mundo aprende a ignorar, inclusive quando ele passar a
    /// dizer a verdade.
    /// </para>
    /// </summary>
    [Fact]
    public void Uma_aplicacao_DERIVADA_precisa_trocar_o_Database_ApplicationName()
    {
        var assemblyName = typeof(TemplateDerivationTests).Assembly.GetName().Name!;

        Assert.SkipWhen(
            assemblyName.StartsWith(TemplateAssemblyPrefix, StringComparison.Ordinal),
            "Este é o próprio template. O teste vale para as aplicações derivadas dele.");

        var applicationName = ReadApplicationNameFromAppSettings();

        // String literal (sem $): as chaves do exemplo de JSON são conteúdo, não interpolação.
        applicationName.ShouldNotBe(
            TemplateApplicationName,
            """
            O Database:ApplicationName ainda é o do TEMPLATE.

            Dele saem os nomes no Postgres — db_{slug}, hangfire_{slug} e {slug}_user — então esta
            aplicação vai apontar para o MESMO banco e o MESMO usuário do template (e de qualquer
            outra app que também tenha esquecido de trocar).

            A colisão é SILENCIOSA: a aplicação sobe e funciona. O estrago aparece em produção,
            quando duas aplicações se atropelam.

            Corrija em src/<SuaApi>/appsettings.json:
                "Database": { "ApplicationName": "MinhaApp.Backend" }
            """);
    }

    /// <summary>
    /// O slug é o que vira nome de banco e de role no Postgres — e o Postgres tem regras sobre
    /// isso. Um nome que gere um slug vazio (ex.: <c>"!!!"</c>) produziria <c>db_</c>, e o
    /// <c>CREATE DATABASE</c> falharia no bootstrap com uma mensagem que não aponta para a causa.
    /// </summary>
    [Fact]
    public void O_ApplicationName_gera_nomes_validos_no_Postgres()
    {
        var applicationName = ReadApplicationNameFromAppSettings();

        var naming = new DatabaseNaming(
            new DatabaseOptions { ApplicationName = applicationName },
            Assembly.GetExecutingAssembly());

        naming.DatabaseName.ShouldNotBe("db_", "o ApplicationName não gerou slug nenhum");
        naming.HangfireDatabaseName.ShouldNotBe("hangfire_");
        naming.AppUsername.ShouldNotBe("_user");

        // O bootstrap interpola estes nomes em CREATE DATABASE / CREATE ROLE. Um nome com aspas,
        // ponto-e-vírgula ou espaço seria um vetor de injeção — o Slugify já os remove, e este
        // teste trava esse contrato.
        foreach (var name in new[] { naming.DatabaseName, naming.HangfireDatabaseName, naming.AppUsername })
        {
            name.ShouldMatch(
                "^[a-z0-9_]+$",
                $"'{name}' tem caractere que não pode ir para um CREATE DATABASE/ROLE sem escape");
        }
    }

    /// <summary>
    /// Lê o <c>appsettings.json</c> da <b>aplicação</b> (não o dos testes).
    ///
    /// <para>
    /// O arquivo é copiado para o diretório de saída da API, que é o mesmo dos testes (eles
    /// referenciam o projeto). Ler o arquivo — e não a configuração já montada — é deliberado: é o
    /// arquivo que a pessoa esqueceu de editar, e é sobre ele que o teste tem de falar.
    /// </para>
    /// </summary>
    private static string ReadApplicationNameFromAppSettings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        File.Exists(path).ShouldBeTrue($"appsettings.json não encontrado em {path}");

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement
            .GetProperty("Database")
            .GetProperty("ApplicationName")
            .GetString()!;
    }
}
