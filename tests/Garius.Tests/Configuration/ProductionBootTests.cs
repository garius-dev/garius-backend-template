using System.Text.RegularExpressions;

namespace Garius.Tests.Configuration;

/// <summary>
/// <b>O compose de produção precisa dar à aplicação tudo o que ela COBRA para subir.</b>
///
/// <para>
/// <b>Estes testes nasceram do primeiro boot em produção — que não aconteceu.</b> A aplicação
/// tem vários <i>fail-fast</i> em <c>Production</c> (e eles estão certos: uma configuração
/// faltando deve derrubar o boot, nunca "degradar"). Só que o <c>docker-compose.app.yml</c>
/// <b>não passava nenhum deles</b>: o deploy morria em três exceções seguidas —
/// <c>TrustedProxies</c>, <c>Redis:ConnectionString</c> e o endereço do Postgres.
/// </para>
///
/// <para>
/// A suíte inteira roda em <c>Development</c>, então <b>nada disso era exercitado</b>. O único
/// ambiente quebrado era o único que ninguém testava — e só se descobre no deploy, que é onde
/// custa caro.
/// </para>
///
/// <para>
/// Estes testes leem o compose e o <c>.env.example</c> como <b>texto</b>. É de propósito: o que
/// se quer garantir é que a variável ESTÁ LÁ, escrita, para quem for derivar o template. Subir
/// um Docker de verdade aqui testaria o Docker, não o template.
/// </para>
/// </summary>
public class ProductionBootTests
{
    /// <summary>
    /// Cada uma destas é um <c>throw</c> no boot em <c>Production</c>. A mensagem entre
    /// parênteses é o que a aplicação cospe quando a variável falta.
    /// </summary>
    [Theory]
    // "Security:TrustedProxies está vazio em Production."
    [InlineData("Security__TrustedProxies__0")]
    // "Redis:ConnectionString não foi configurado. O Redis é obrigatório."
    [InlineData("Redis__ConnectionString")]
    // "Cors:AllowedOrigins está vazio em Production."
    [InlineData("Cors__AllowedOrigins__0")]
    // Sem o host, a app tenta o localhost — que, DENTRO de um container, é ele mesmo.
    [InlineData("Database__Host")]
    public void O_compose_passa_a_variavel_que_a_aplicacao_EXIGE(string variable)
    {
        var compose = ReadDeploymentFile("docker-compose.app.yml");

        Assert.Contains(
            variable,
            compose,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Toda <c>${VARIAVEL}</c> usada no compose tem de existir no <c>.env.example</c> — senão
    /// quem derivar o template copia o exemplo, sobe, e o Docker substitui por <b>string
    /// vazia</b>, em silêncio. O container então falha com "TrustedProxies está vazio", e a
    /// pessoa não tem ideia de onde declarar aquilo.
    /// </summary>
    [Fact]
    public void Toda_variavel_do_compose_esta_no_env_example()
    {
        var compose = ReadDeploymentFile("docker-compose.app.yml");
        var example = ReadDeploymentFile(".env.example");

        var declared = example
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && line.Contains('=', StringComparison.Ordinal))
            .Select(line => line.Split('=', 2)[0].Trim())
            .ToHashSet(StringComparer.Ordinal);

        var used = Regex.Matches(compose, @"\$\{([A-Z_][A-Z0-9_]*)\}")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = used.Except(declared).OrderBy(name => name, StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0,
            $"O compose usa ${{{string.Join("}}, ${{", missing)}}} — e o .env.example NÃO declara. " +
            "O Docker substitui por string VAZIA, em silêncio, e o container falha no boot sem " +
            "que ninguém saiba onde configurar aquilo.");
    }

    /// <summary>
    /// O <c>.env.example</c> não pode conter <b>senha nenhuma</b>. Ele vai para o git; o
    /// <c>.env</c> de verdade, não. As senhas vivem no Secret Manager.
    /// </summary>
    [Theory]
    [InlineData("Database__RootPassword")]
    [InlineData("Database__AppPassword")]
    [InlineData("Redis__Password")]
    [InlineData("Jwt__SigningKey")]
    [InlineData("Encryption__")]
    public void O_env_example_NAO_carrega_segredo(string secret)
    {
        var example = ReadDeploymentFile(".env.example");
        var compose = ReadDeploymentFile("docker-compose.app.yml");

        Assert.DoesNotContain(secret, example, StringComparison.OrdinalIgnoreCase);

        // No compose também não: ele é versionado.
        Assert.DoesNotContain(secret, compose, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sobe do <c>bin</c> dos testes até a raiz do repositório e lê o arquivo de deploy.
    /// </summary>
    private static string ReadDeploymentFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "docker")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var apps = Path.Combine(directory.FullName, "docker", "prod", "apps");

        // Uma só app por template derivado — é o que o deploy.ps1 também assume.
        var app = Directory.GetDirectories(apps).Single();

        return File.ReadAllText(Path.Combine(app, fileName));
    }
}
