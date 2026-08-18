using System.Diagnostics;

namespace Garius.Tests.Configuration;

/// <summary>
/// O repositório publicado <b>contém tudo o que é preciso para compilar</b>.
///
/// <para>
/// <b>A falha que este teste existe para pegar, e ela aconteceu de verdade.</b> Uma regra do
/// <c>.gitignore</c> pensada para proteger a service account montada no container
/// (<c>**/secrets/*</c>) pegava <b>também</b> a pasta de <b>código</b>
/// <c>src/*.Infrastructure/Secrets/</c> — o provedor do Google Secret Manager — porque o Git no
/// Windows compara caminhos sem diferenciar maiúsculas.
/// </para>
///
/// <para>
/// Resultado: quatro arquivos <c>.cs</c> <b>nunca foram versionados</b>. Na máquina de quem
/// escreveu o template, tudo compilava e a suíte ficava verde. Quem clonasse o repositório
/// recebia <c>CS0234: namespace 'Secrets' does not exist</c> — e nada na suíte apontava para a
/// causa, porque a suíte roda sobre os arquivos locais, não sobre o que está no Git.
/// </para>
///
/// <para>
/// <b>É o pior tipo de falha deste projeto:</b> silenciosa, invisível para quem a causou, e fatal
/// para todo mundo depois. Um teste que compara o disco com o índice do Git é o único jeito de
/// pegá-la — nenhum build local jamais acusaria.
/// </para>
/// </summary>
public class RepositoryCompletenessTests
{
    /// <summary>
    /// <b>Todo <c>.cs</c> que existe em <c>src/</c> no disco existe também no Git.</b>
    ///
    /// <para>
    /// Compara o <b>disco</b> com o <b>índice do Git</b> — e não com o que o <c>.gitignore</c>
    /// diz. A diferença foi descoberta escrevendo este teste: a primeira versão usava
    /// <c>git ls-files --ignored</c> e <b>não acusava nada</b> depois que os arquivos eram
    /// adicionados, porque um arquivo já rastreado vence o <c>.gitignore</c>. O guarda tinha um
    /// ponto cego exatamente no estado em que o repositório fica <i>depois</i> da correção — ou
    /// seja, ele nunca mais avisaria se a regra ampla voltasse.
    /// </para>
    ///
    /// <para>
    /// Comparar disco com índice não tem esse ponto cego: pega o arquivo ausente seja qual for o
    /// motivo — regra de ignore ampla demais, <c>git add</c> esquecido, ou o que ainda não
    /// inventamos.
    /// </para>
    /// </summary>
    [Fact]
    public void Todo_codigo_de_src_esta_versionado()
    {
        var root = FindRepositoryRoot();

        var (exitCode, tracked) = RunGit(root, "ls-files -- src/");

        Assert.SkipWhen(
            exitCode != 0,
            "Não foi possível consultar o Git (o teste não roda fora de um clone).");

        var inGit = tracked
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var onDisk = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            // bin/ e obj/ são artefatos de build: ficarem de fora é o correto.
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            // O Git sempre reporta com barra normal; o Windows entrega com contrabarra.
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();

        var missing = onDisk.Where(path => !inGit.Contains(path)).ToList();

        missing.ShouldBeEmpty(
            "Há código-fonte em src/ que existe no disco mas NÃO está no Git:\n\n" +
            string.Join("\n", missing.Select(p => "  " + p)) +
            "\n\nIsto NÃO quebra o build de quem escreveu — quebra o de quem CLONA, com um " +
            "erro que nem menciona o .gitignore. Já aconteceu: a regra `**/secrets/*` pegou a " +
            "pasta de código src/*.Infrastructure/Secrets/, quatro arquivos nunca foram " +
            "versionados, e o repositório publicado não compilava. Prefira caminhos " +
            "explícitos a padrões amplos.");
    }

    /// <summary>
    /// O contrapeso do teste acima: os <b>segredos de verdade</b> continuam fora do Git.
    ///
    /// <para>
    /// Sem esta asserção, a "correção" preguiçosa para o teste anterior seria esvaziar o
    /// <c>.gitignore</c> — trocando um repositório que não compila por um que vaza a chave que
    /// destrava todos os outros segredos. Os dois testes juntos fecham a tenaz.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("docker/prod/apps/gariustech-backend-template/secrets/gcp-service-account.json")]
    [InlineData("docker/prod/apps/gariustech-backend-template/.env")]
    public void Os_segredos_continuam_ignorados(string path)
    {
        var root = FindRepositoryRoot();

        var (exitCode, _) = RunGit(root, $"check-ignore -q \"{path}\"");

        Assert.SkipWhen(exitCode > 1, "Não foi possível consultar o Git.");

        exitCode.ShouldBe(
            0,
            $"{path} NÃO está sendo ignorado. É a credencial que destrava o Secret Manager " +
            "(ou o arquivo de ambiente): versioná-la entrega, de uma vez, a senha do banco, as " +
            "chaves de criptografia e a do JWT. No template ANTERIOR isso aconteceu — havia uma " +
            "exceção no .gitignore que vazou a service account para dentro do repositório.");
    }

    private static (int ExitCode, string Output) RunGit(string workingDirectory, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (process is null)
        {
            return (-1, string.Empty);
        }

        var output = process.StandardOutput.ReadToEnd();

        process.WaitForExit(TimeSpan.FromSeconds(30));

        return (process.ExitCode, output);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && directory.GetFiles("*.slnx").Length == 0)
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Não encontrei a raiz do repositório (*.slnx).");
    }
}
