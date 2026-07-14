using System.Text;
using Shouldly;

namespace Garius.Tests.Configuration;

/// <summary>
/// O <c>deploy.ps1</c> precisa rodar no PowerShell que a pessoa TEM — e no Windows, o que vem
/// instalado é o <b>5.1</b>.
///
/// <para>
/// <b>Este teste nasceu de um erro que não tinha nada a ver com o que dizia.</b> O script foi
/// salvo em UTF-8 <b>sem BOM</b>. O PowerShell 5.1 assume <b>ANSI</b> num <c>.ps1</c> sem BOM:
/// os acentos (<c>não</c>, <c>versão</c>, <c>senha</c>) viram bytes lixo, e o parser morre com
/// <i>"Missing closing '}' in statement block"</i> — apontando para uma linha aleatória, sem
/// chave nenhuma faltando.
/// </para>
///
/// <para>
/// O PowerShell 7 lê UTF-8 por padrão. Ou seja: <b>o script funciona para quem o escreveu e
/// quebra para quem o usa</b> — e o erro não dá nenhuma pista da causa.
/// </para>
/// </summary>
public class DeployScriptTests
{
    [Fact]
    public void O_deploy_ps1_esta_em_UTF8_COM_BOM()
    {
        var script = FindDeployScript();

        var firstBytes = File.ReadAllBytes(script).Take(3).ToArray();

        var bom = Encoding.UTF8.GetPreamble();   // EF BB BF

        firstBytes.ShouldBe(
            bom,
            """
            O deploy.ps1 está SEM o BOM de UTF-8.

            No PowerShell 5.1 (o que vem no Windows) isso faz o arquivo ser lido como ANSI: os
            acentos viram lixo e o parser morre com "Missing closing '}'" — num lugar onde não
            falta chave nenhuma. No PowerShell 7 funciona, então o bug é invisível para quem
            escreveu o script.

            Regrave o arquivo em UTF-8 COM BOM.
            """);
    }

    /// <summary>
    /// Se um dia o script perder os acentos, o BOM deixa de ser necessário — mas aí este teste
    /// perderia o sentido sem ninguém notar. Esta asserção o mantém honesto: enquanto houver
    /// acento, o BOM é obrigatório.
    /// </summary>
    [Fact]
    public void O_teste_do_BOM_ainda_faz_sentido_o_script_TEM_acentos()
    {
        var script = FindDeployScript();

        var content = File.ReadAllText(script, Encoding.UTF8);

        content.ShouldContain(
            "ã",
            customMessage: "sem acentos, o BOM deixaria de importar — e este teste seria decoração");
    }

    private static string FindDeployScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "deploy.ps1")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine(directory.FullName, "deploy.ps1");
    }
}
