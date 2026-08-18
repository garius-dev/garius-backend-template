using System.Diagnostics;
using System.Text.RegularExpressions;
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

            // ⚠️ NÃO PODE haver healthcheck aqui.
            //
            // Ele existia, e fazia a requisição com o /dev/tcp do bash. A imagem de runtime
            // virou CHISELED (ver Dockerfile): sem shell, sem bash, sem curl. O comando não
            // teria com o que rodar.
            //
            // E o modo de falha seria traiçoeiro: o Docker marcaria o container como
            // `unhealthy` PARA SEMPRE — porque o COMANDO falha, não porque a aplicação está
            // doente. Um container saudável eternamente marcado como quebrado, e a "correção"
            // tentadora seria apagar o healthcheck sem entender o motivo.
            //
            // Este teste trava o par: se alguém reintroduzir o healthcheck sem trocar a imagem
            // base de volta, ele acusa aqui — e não em produção.
            output.ShouldNotContain(
                "healthcheck",
                Case.Insensitive,
                "a imagem chiseled não tem shell: um healthcheck no compose marcaria o " +
                "container como unhealthy para sempre. Quem checa a saúde é quem está de " +
                "FORA — a httpGet probe no Kubernetes, o Traefik aqui");
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
    /// A aplicação precisa conseguir <b>ler a própria credencial</b> dentro do container.
    ///
    /// <para>
    /// <b>Este teste nasceu de um deploy que falhou DUAS vezes.</b> O container morria com:
    /// </para>
    ///
    /// <code>Access to the path '/run/secrets/gcp_service_account' is denied.</code>
    ///
    /// <para>
    /// Duas decisões corretas que, juntas, se anulavam: o container roda como <b>não-root</b>
    /// (<c>app</c>, uid 1654) e o <c>deploy.ps1</c> gravava a service account com modo
    /// <b>600</b>. O Docker monta o secret como um <b>bind mount comum</b>, preservando o dono
    /// do host — e o processo, sendo outro usuário, ficava trancado do lado de fora.
    /// </para>
    ///
    /// <para>
    /// <b>E a primeira correção não funcionou:</b> pôr <c>uid:</c> no compose. Isso só vale no
    /// Docker <b>Swarm</b> — num <c>docker compose</c> normal o Docker IGNORA (e avisa que está
    /// ignorando). O teste da época verificava que a linha <i>existia</i> no YAML, não que ela
    /// <i>funcionava</i>. Era decoração, e o deploy falhou de novo.
    /// </para>
    ///
    /// <para>
    /// Este teste é diferente: ele <b>monta o arquivo num container real</b>, como o usuário
    /// real, e tenta lê-lo. É o comportamento, não o texto do YAML.
    /// </para>
    /// </summary>
    [Fact]
    public void Um_container_NAO_ROOT_consegue_ler_o_secret_com_a_permissao_que_o_deploy_grava()
    {
        // O modo que o deploy.ps1 grava no servidor.
        var deploy = File.ReadAllText(FindDeployScript());

        var mode = Regex.Match(deploy, @"chmod (\d{3}) '\$remoteFolder/secrets/gcp-service-account\.json'")
            .Groups[1].Value;

        mode.ShouldNotBeNullOrEmpty("o deploy.ps1 precisa definir a permissão da service account");

        // O uid do usuário da imagem (o Dockerfile roda como não-root).
        const string containerUid = "1654";

        var temp = Directory.CreateTempSubdirectory();

        try
        {
            var secret = Path.Combine(temp.FullName, "sa.json");
            File.WriteAllText(secret, "{}");

            // Reproduz o servidor: o arquivo pertence ao usuário do DEPLOY (outro uid), com o
            // modo que o deploy.ps1 grava.
            Docker($"run --rm -v \"{temp.FullName}:/w\" alpine sh -c \"chown 1002:1002 /w/sa.json && chmod {mode} /w/sa.json\"");

            // E o container tenta ler, como o usuário NÃO-ROOT da imagem.
            var (exitCode, _, _) = Docker(
                $"run --rm --user {containerUid}:{containerUid} " +
                $"-v \"{secret}:/run/secrets/s:ro\" alpine cat /run/secrets/s");

            exitCode.ShouldBe(
                0,
                $"""
                 O container NÃO consegue ler a service account (modo {mode}).

                 A aplicação morre no boot com:
                     Access to the path '/run/secrets/gcp_service_account' is denied.

                 O container roda como não-root (uid {containerUid}) e o Docker monta o secret
                 preservando o dono do host. Um `uid:` no compose NÃO resolve — só funciona no
                 Swarm. A permissão tem de vir do arquivo (a pasta secrets/ é 700, então isso
                 não expõe nada a mais no host).
                 """);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static (int ExitCode, string Output, string Error) Docker(string arguments) =>
        Run("docker", arguments, Path.GetTempPath());

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
