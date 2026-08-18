namespace Garius.Tests.Configuration;

/// <summary>
/// O guarda do <c>REGRAS-DA-APLICACAO.md</c> — o arquivo onde a app derivada registra as
/// <b>próprias</b> regras.
///
/// <para>
/// <b>O atrito que este teste existe para pegar</b>, e ele é real (relatado por quem usou o
/// template): a pessoa deriva a aplicação, pede a uma IA para integrar login com o Google, e a IA
/// <b>recusa</b> — citando o README e dizendo que "está fora das regras". Nada em Google OAuth
/// viola as 10 regras; o que aconteceu é que o único material disponível descrevia o
/// <b>template</b>, e a IA respondeu sobre ele.
/// </para>
///
/// <para>
/// A causa é a ausência de um lugar para as regras da aplicação. Sem esse arquivo preenchido, o
/// template é a única especificação que existe — e ele vira, por omissão, uma jaula.
/// </para>
///
/// <para>
/// Este teste não verifica qualidade nenhuma do conteúdo. Ele só cobra que o arquivo <b>deixou de
/// ser o formulário em branco</b>, do mesmo jeito que <see cref="TemplateDerivationTests"/> cobra
/// que o <c>ApplicationName</c> deixou de ser o do template.
/// </para>
/// </summary>
public class ApplicationRulesTests
{
    /// <summary>
    /// O prefixo dos assemblies do template.
    ///
    /// <para>
    /// ⚠️ Montado por concatenação, <b>pela mesma razão</b> explicada em
    /// <see cref="TemplateDerivationTests"/>: o <c>dotnet new</c> substitui o nome do template em
    /// todo o conteúdo dos arquivos — inclusive dentro deste. Escrito por extenso, o literal
    /// viraria o nome da app derivada e o guarda se autodesativaria em silêncio.
    /// </para>
    /// </summary>
    private static string TemplateAssemblyPrefix => "Gari" + "us" + ".";

    /// <summary>
    /// Uma marca que só existe no arquivo <b>não preenchido</b>: os comentários HTML de instrução.
    /// Some naturalmente conforme a pessoa escreve as próprias regras.
    /// </summary>
    private const string UnfilledMarker = "<!--";

    /// <summary>
    /// ⚠️ <b>Ignorado no próprio template</b> (onde o arquivo em branco é o correto) e
    /// <b>obrigatório</b> em qualquer aplicação derivada.
    ///
    /// <para>
    /// A distinção é pelo nome do assembly, como no <see cref="TemplateDerivationTests"/>: um teste
    /// permanentemente vermelho no template seria um teste que todo mundo aprende a ignorar —
    /// inclusive quando ele passar a dizer a verdade.
    /// </para>
    /// </summary>
    [Fact]
    public void Uma_aplicacao_DERIVADA_precisa_preencher_o_REGRAS_DA_APLICACAO()
    {
        var assemblyName = typeof(ApplicationRulesTests).Assembly.GetName().Name!;

        Assert.SkipWhen(
            assemblyName.StartsWith(TemplateAssemblyPrefix, StringComparison.Ordinal),
            "Este é o próprio template — aqui o arquivo em branco é o estado correto.");

        var path = FindInRepositoryRoot("REGRAS-DA-APLICACAO.md");

        File.Exists(path).ShouldBeTrue(
            "O REGRAS-DA-APLICACAO.md sumiu. Ele é onde esta aplicação registra as PRÓPRIAS " +
            "regras — autenticação real, papéis, domínio. Sem ele, uma IA só tem o template " +
            "para consultar, e vai responder sobre o template.");

        var content = File.ReadAllText(path);

        content.ShouldNotContain(
            UnfilledMarker,
            Case.Sensitive,
            """
            O REGRAS-DA-APLICACAO.md ainda está com os comentários de exemplo — ou seja, ninguém
            registrou as regras DESTA aplicação.

            Isso não é burocracia. É o que causa o atrito de uma IA recusar features legítimas
            (integrar login com o Google, por exemplo) citando o README: sem este arquivo, o
            template é a única especificação que existe, e ele vira uma jaula por omissão.

            Preencha as seções e apague os comentários <!-- --> conforme escrever.
            """);
    }

    /// <summary>
    /// Sobe do diretório de saída até a raiz do repositório (onde está a solução).
    ///
    /// <para>
    /// O arquivo de regras vive na raiz, não no diretório de saída — diferente do
    /// <c>appsettings.json</c>, que é copiado no build.
    /// </para>
    /// </summary>
    private static string FindInRepositoryRoot(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && directory.GetFiles("*.slnx").Length == 0)
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException("Não encontrei a raiz do repositório (*.slnx).")
            : Path.Combine(directory.FullName, fileName);
    }
}
