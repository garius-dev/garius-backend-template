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

    /// <summary>
    /// O <b>compose</b> e o <b>deploy.ps1</b> têm de concordar no nome da service account.
    ///
    /// <para>
    /// O arquivo que o GCP entrega tem um nome gerado (<c>garius-tcm-7d83fa2633d3.json</c>), mas
    /// o compose monta um caminho <b>fixo</b>. Se o script não renomeia no envio, o compose morre
    /// no servidor com <i>"no such file"</i> — depois de já ter rodado os testes, buildado a
    /// imagem, publicado no Docker Hub e enviado tudo. O erro mais caro possível.
    /// </para>
    /// </summary>
    [Fact]
    public void O_compose_e_o_deploy_concordam_no_nome_da_service_account()
    {
        var appDir = FindAppDirectory();

        var compose = File.ReadAllText(Path.Combine(appDir, "docker-compose.app.yml"));
        var deploy = File.ReadAllText(FindDeployScript());

        // O nome que o compose monta no container.
        const string expected = "gcp-service-account.json";

        compose.ShouldContain(
            expected,
            customMessage: "o compose precisa apontar para um nome FIXO de service account");

        deploy.ShouldContain(
            expected,
            customMessage:
                "o deploy.ps1 precisa RENOMEAR a service account para o nome que o compose " +
                "espera — o arquivo do GCP vem com um nome gerado, e sem a renomeação o " +
                "compose falha no servidor com 'no such file'");
    }

    /// <summary>
    /// O secret precisa ser montado com o <b>uid do usuário do container</b> — senão a aplicação
    /// não consegue ler a própria credencial.
    ///
    /// <para>
    /// <b>Este teste nasceu no primeiro deploy que chegou ao servidor.</b> O container morreu com:
    /// </para>
    ///
    /// <code>Access to the path '/run/secrets/gcp_service_account' is denied.</code>
    ///
    /// <para>
    /// Duas decisões corretas que, juntas, se anulavam: o <c>deploy.ps1</c> grava a service
    /// account com modo <b>600</b> (é uma credencial — com 777 qualquer usuário do servidor lê a
    /// chave que abre a senha do banco e as chaves de criptografia); e o container roda como
    /// <b>não-root</b> (<c>app</c>, uid 1654). O Docker monta o secret <b>preservando o dono do
    /// host</b> — e o processo, sendo outro usuário, fica trancado do lado de fora.
    /// </para>
    ///
    /// <para>
    /// A saída não é afrouxar a permissão: é o compose declarar <b>para quem</b> montar. Assim o
    /// arquivo continua 600 no host <b>e</b> legível dentro do container.
    /// </para>
    /// </summary>
    [Fact]
    public void O_secret_e_montado_com_o_uid_do_usuario_do_container()
    {
        var appDir = FindAppDirectory();

        var compose = File.ReadAllText(Path.Combine(appDir, "docker-compose.app.yml"));
        var dockerfile = File.ReadAllText(FindRepositoryFile("Dockerfile"));

        // O container NÃO roda como root — é o que torna o uid necessário.
        dockerfile.ShouldContain(
            "USER app",
            customMessage: "a imagem precisa rodar como não-root");

        // E o compose precisa dizer ao Docker para quem montar o secret.
        compose.ShouldContain(
            "uid:",
            customMessage:
                """
                O compose monta o secret sem declarar o uid.

                O Docker preserva o dono do arquivo no host (o seu usuário), mas o container roda
                como `app` (uid 1654, não-root) — e o arquivo é 600. A aplicação morre no boot com:

                    Access to the path '/run/secrets/gcp_service_account' is denied.

                Use a sintaxe longa, com `uid: "1654"`.
                """);
    }

    private static string FindRepositoryFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, fileName)))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine(directory.FullName, fileName);
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
