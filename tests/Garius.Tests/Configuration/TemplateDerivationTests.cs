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
    /// O <c>GcpSecrets:SecretName</c> que vem no template.
    ///
    /// <para>
    /// Quebrado pela mesma razão que <see cref="Marker"/> — o <c>dotnet new</c> substitui esta
    /// string <b>inteira</b> pelo secret derivado, inclusive aqui dentro. Escrita por extenso, o
    /// guarda passaria a comparar o valor com ele mesmo e nunca falharia.
    /// </para>
    /// </summary>
    private static string TemplateSecretName =>
        $"{Marker.ToLowerInvariant()}tech-backend-template-secrets";

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
    /// O <b>segundo</b> valor que uma aplicação derivada precisa trocar — e o mais perigoso dos
    /// dois, porque o erro dele <b>não colide, ele VAZA</b>.
    ///
    /// <para>
    /// O <c>GcpSecrets:SecretName</c> diz de qual secret do Google Secret Manager a aplicação lê
    /// suas chaves. Deixado com o valor do template, a app derivada lê as <b>chaves de criptografia
    /// do template</b> — e funciona, sem reclamar de nada. Duas aplicações passariam a cifrar dados
    /// pessoais com a <b>mesma chave</b>, e a rotação de uma quebraria a outra. Pior: quem tiver
    /// acesso ao secret de uma aplicação consegue decifrar a PII de <b>todas</b> as que o
    /// compartilham.
    /// </para>
    ///
    /// <para>
    /// Ao contrário da colisão de banco (que ao menos <b>quebra</b> visivelmente quando duas apps
    /// se atropelam), este erro é <b>completamente silencioso</b>. Nada nunca falha.
    /// </para>
    /// </summary>
    [Fact]
    public void Uma_aplicacao_DERIVADA_precisa_trocar_o_GcpSecrets_SecretName()
    {
        var assemblyName = typeof(TemplateDerivationTests).Assembly.GetName().Name!;

        Assert.SkipWhen(
            assemblyName.StartsWith(TemplateAssemblyPrefix, StringComparison.Ordinal),
            "Este é o próprio template. O teste vale para as aplicações derivadas dele.");

        var secretName = ReadFromAppSettings("appsettings.Development.json", "GcpSecrets", "SecretName");

        // Um SecretName vazio é legítimo: significa que a app não usa o Secret Manager (as chaves
        // vêm de env var). O que não pode é apontar para o secret DO TEMPLATE.
        if (string.IsNullOrWhiteSpace(secretName))
        {
            return;
        }

        secretName.ShouldNotBe(
            TemplateSecretName,
            """
            O GcpSecrets:SecretName ainda é o do TEMPLATE.

            Esta aplicação vai ler as CHAVES DE CRIPTOGRAFIA do template — e vai funcionar, sem
            reclamar de nada. Duas aplicações cifrando dados pessoais com a MESMA chave: a rotação
            de uma quebra a outra, e quem tiver acesso ao secret de uma decifra a PII de todas.

            Diferente da colisão de banco, este erro NUNCA falha sozinho. É silencioso para sempre.

            Corrija em src/<SuaApi>/appsettings.Development.json (e no .Production.json):
                "GcpSecrets": { "SecretName": "minha-app-secrets" }
            """);
    }

    /// <summary>
    /// <b>Se o Secret Manager está LIGADO em produção, as coordenadas têm de estar lá.</b>
    ///
    /// <para>
    /// <b>Este teste nasceu de um bug que quebrava TODA app derivada no primeiro deploy.</b> O
    /// <c>appsettings.Production.json</c> ligava <c>GcpSecrets:Enabled = true</c> e <b>não</b>
    /// definia <c>ProjectId</c> nem <c>SecretName</c> — eles só existiam no
    /// <c>appsettings.Development.json</c>. Em produção a aplicação morria no boot com
    /// "GcpSecrets:Enabled=true, mas ProjectId ou SecretName não foram configurados".
    /// </para>
    ///
    /// <para>
    /// Passou despercebido porque <b>toda a suíte roda com o Secret Manager desligado</b> (os
    /// testes não podem depender de rede nem de credencial) — e a app, em desenvolvimento, tem
    /// as coordenadas. O único cenário quebrado era exatamente o único que ninguém exercitava.
    /// </para>
    /// </summary>
    [Fact]
    public void Producao_com_o_Secret_Manager_LIGADO_precisa_das_coordenadas()
    {
        var enabled = ReadFromAppSettings("appsettings.Production.json", "GcpSecrets", "Enabled");

        // Desligado é legítimo: as chaves podem vir de variável de ambiente.
        if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var projectId = ReadFromAppSettings("appsettings.Production.json", "GcpSecrets", "ProjectId");
        var secretName = ReadFromAppSettings("appsettings.Production.json", "GcpSecrets", "SecretName");

        projectId.ShouldNotBeNullOrWhiteSpace(
            """
            GcpSecrets:Enabled = true no appsettings.Production.json, mas SEM o ProjectId.

            A aplicação NÃO SOBE em produção — morre no boot, antes de servir uma requisição.
            (ProjectId e SecretName não são segredo: são coordenadas. O segredo é o CONTEÚDO.)
            """);

        secretName.ShouldNotBeNullOrWhiteSpace(
            """
            GcpSecrets:Enabled = true no appsettings.Production.json, mas SEM o SecretName.

            A aplicação NÃO SOBE em produção — morre no boot, antes de servir uma requisição.
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
    private static string ReadApplicationNameFromAppSettings() =>
        ReadFromAppSettings("appsettings.json", "Database", "ApplicationName")
        ?? throw new InvalidOperationException("Database:ApplicationName não está no appsettings.json.");

    /// <summary>
    /// Lê uma chave do <c>appsettings</c> da <b>aplicação</b> (não o dos testes).
    ///
    /// <para>
    /// Os arquivos são copiados para o diretório de saída da API, que é o mesmo dos testes (eles
    /// referenciam o projeto). Lê-se o <b>arquivo</b>, e não a configuração já montada, de
    /// propósito: é o arquivo que a pessoa esqueceu de editar, e é sobre ele que o teste tem de
    /// falar.
    /// </para>
    /// </summary>
    /// <returns><c>null</c> se o arquivo ou a chave não existirem.</returns>
    private static string? ReadFromAppSettings(string fileName, string section, string key)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);

        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        if (!document.RootElement.TryGetProperty(section, out var sectionElement)
            || !sectionElement.TryGetProperty(key, out var value))
        {
            return null;
        }

        // GetString() SÓ funciona em JsonValueKind.String — num booleano ele lança. E há chaves
        // booleanas aqui (GcpSecrets:Enabled), então o texto cru é o que serve para todas.
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
    }
}
