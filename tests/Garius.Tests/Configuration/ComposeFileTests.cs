using System.Diagnostics;
using Shouldly;

namespace Garius.Tests.Configuration;

/// <summary>
/// O <c>docker-compose.app.yml</c> precisa ser YAML <b>válido</b> — e ninguém verificava.
///
/// <para>
/// <b>Este teste nasceu no primeiro deploy real.</b> Tudo funcionou — testes, build, push,
/// upload dos arquivos — e o compose morreu <b>no servidor</b>, no último passo:
/// </para>
///
/// <code>yaml: line 78, column 112: mapping values are not allowed in this context</code>
///
/// <para>
/// A causa era o comando do healthcheck, que contém <c>Host: localhost</c>. Sem aspas, o YAML
/// vê aquele <c>:</c> e tenta ler um mapeamento no meio da string. O arquivo <b>parecia</b>
/// certo, e o erro só apareceu depois de publicar a imagem e subir os arquivos — o pior lugar
/// possível para descobrir.
/// </para>
///
/// <para>
/// Quem valida é o <b>próprio Docker</b> (<c>docker compose config</c>), não um parser de YAML
/// qualquer: é ele que vai ler o arquivo em produção, e é a opinião dele que importa.
/// </para>
/// </summary>
public class ComposeFileTests
{
    [Fact]
    public void O_compose_e_um_YAML_valido_para_o_DOCKER()
    {
        var appDir = FindAppDirectory();

        // O `docker compose config` exige as variáveis do .env. Numa app recém-derivada só
        // existe o .env.example — então é ele que serve de entrada.
        var envFile = Path.Combine(appDir, ".env");
        var createdEnv = false;

        if (!File.Exists(envFile))
        {
            File.Copy(Path.Combine(appDir, ".env.example"), envFile);
            createdEnv = true;
        }

        try
        {
            var (exitCode, output, error) = Run(
                "docker",
                "compose -f docker-compose.app.yml config",
                appDir);

            exitCode.ShouldBe(
                0,
                $"""
                 O docker-compose.app.yml NÃO é um YAML válido:

                 {error}

                 Este erro só apareceria NO SERVIDOR — depois de rodar os testes, buildar a
                 imagem, publicá-la no Docker Hub e enviar os arquivos. O pior lugar possível
                 para descobrir.
                 """);

            // E o healthcheck tem de sobreviver inteiro. Ele contém `Host: localhost` — aquele
            // `:` é exatamente o que quebrava o YAML, e é o que as aspas protegem.
            output.ShouldContain("/health/live");
            output.ShouldContain("200 OK");
        }
        finally
        {
            if (createdEnv)
            {
                File.Delete(envFile);
            }
        }
    }

    private static string FindAppDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "docker")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var apps = Path.Combine(directory.FullName, "docker", "prod", "apps");

        return Directory.GetDirectories(apps).Single();
    }

    private static (int ExitCode, string Output, string Error) Run(
        string fileName,
        string arguments,
        string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();

        process.WaitForExit();

        return (process.ExitCode, output, error);
    }
}
