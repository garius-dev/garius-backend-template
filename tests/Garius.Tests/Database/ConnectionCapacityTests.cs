using System.Reflection;
using Garius.Infrastructure.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Garius.Tests.Database;

/// <summary>
/// O aviso de capacidade de conexões.
///
/// <para>
/// <b>A falha que ele antecipa.</b> <c>MaxPoolSize</c> é por réplica, e ninguém multiplica. Com
/// 10 réplicas e pool 20 são 200 conexões, mais o Hangfire — contra um <c>max_connections</c>
/// que no Postgres é 100 por padrão. Isso <b>não falha</b> em teste nem em staging com uma
/// réplica: falha quando o autoscaler escala no pico. E aí o <c>too many clients already</c>
/// chega junto com o incidente que causou o pico, parecendo consequência dele.
/// </para>
///
/// <para>
/// <b>É AVISO, não falha fechada</b> — e a distinção importa. O template derruba o boot em
/// configuração <i>inválida</i> (regra 9). Isto é um alerta sobre uma <i>estimativa</i>
/// (<c>ExpectedReplicas</c>), que a aplicação não tem como verificar. Derrubar o boot por causa
/// de um palpite impediria uma app saudável de subir.
/// </para>
/// </summary>
public class ConnectionCapacityTests
{
    [Fact]
    public void Avisa_quando_replicas_vezes_pool_passa_do_teto()
    {
        var warnings = Capture(replicas: 10, maxPoolSize: 20, postgresMax: 100);

        warnings.ShouldNotBeEmpty(
            "10 réplicas × (20 + 8) + 5 = 285 conexões contra max_connections=100. Sem este " +
            "aviso, o estouro só apareceria no pico — junto com o incidente que o causou");

        warnings[0].ShouldContain("max_connections=100");
    }

    [Fact]
    public void NAO_avisa_quando_a_conta_cabe()
    {
        // 2 × (10 + 8) + 5 = 41, contra 100. Bem abaixo dos 70%.
        Capture(replicas: 2, maxPoolSize: 10, postgresMax: 100).ShouldBeEmpty(
            "um alarme que toca à toa é um alarme que ninguém olha");
    }

    /// <summary>
    /// Sem <c>ExpectedReplicas</c>, silêncio.
    ///
    /// <para>
    /// É o default, e é deliberado: num template, adivinhar a topologia de quem deriva seria
    /// gritar errado em toda aplicação nova.
    /// </para>
    /// </summary>
    [Fact]
    public void Sem_estimativa_de_replicas_nao_avisa()
    {
        Capture(replicas: 0, maxPoolSize: 100, postgresMax: 10).ShouldBeEmpty(
            "sem ExpectedReplicas não há o que calcular — e um aviso chutado seria ruído");
    }

    /// <summary>
    /// O default do template cabe no <b>mínimo</b> de réplicas do chart (3).
    ///
    /// <para>
    /// Trava contra alguém subir o <c>MaxPoolSize</c> default sem pensar: se o piso do chart
    /// já não couber, toda app derivada nasce estourada.
    /// </para>
    /// </summary>
    [Fact]
    public void O_default_do_template_cabe_no_minimo_de_replicas_do_chart()
    {
        var defaults = new DatabaseOptions();

        // 3 é o minReplicas do deploy/helm/values.yaml.
        var estimated = (3 * (defaults.MaxPoolSize + 8)) + 5;

        estimated.ShouldBeLessThanOrEqualTo(
            (int)(defaults.PostgresMaxConnections * 0.7),
            $"o default (MaxPoolSize={defaults.MaxPoolSize}) não cabe nem nas 3 réplicas " +
            "mínimas do chart");
    }

    /// <summary>
    /// <b>Acima de poucas réplicas, NENHUM MaxPoolSize resolve — é preciso um pooler.</b>
    ///
    /// <para>
    /// Este teste existe para registrar um fato desconfortável que a conta revela, e que é
    /// fácil de não enxergar: contra um Postgres de fábrica (<c>max_connections=100</c>), nem
    /// <c>MaxPoolSize=5</c> sustenta 10 réplicas — o Hangfire sozinho já pede 8 por réplica.
    /// </para>
    ///
    /// <para>
    /// A conclusão não é "diminua o pool até caber": um pool pequeno demais faz as requisições
    /// ficarem na fila esperando conexão, trocando um erro visível por uma lentidão difícil de
    /// diagnosticar. A conclusão é <b>pgBouncer/PgCat</b>, ou subir o <c>max_connections</c>.
    /// Está documentado no README, e o aviso de boot aponta para lá.
    /// </para>
    /// </summary>
    [Fact]
    public void Em_alta_escala_nem_o_menor_pool_cabe_sem_pooler()
    {
        // O menor pool que ainda é utilizável, contra o Postgres de fábrica.
        var warnings = Capture(replicas: 10, maxPoolSize: 5, postgresMax: 100);

        warnings.ShouldNotBeEmpty(
            "10 réplicas × (5 + 8) + 5 = 135 > 100: acima de poucas réplicas o pooler não é " +
            "otimização, é requisito — e o aviso precisa dizer isso ANTES do pico");

        warnings[0].ShouldContain(
            "pgBouncer",
            Case.Sensitive,
            "o aviso tem de apontar a saída real, não só reclamar do número");
    }

    private static List<string> Capture(int replicas, int maxPoolSize, int postgresMax)
    {
        var warnings = new List<string>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ApplicationName"] = "CapacityTests",
                ["Database:ExpectedReplicas"] = replicas.ToString(),
                ["Database:MaxPoolSize"] = maxPoolSize.ToString(),
                ["Database:PostgresMaxConnections"] = postgresMax.ToString(),
                // Chaves de criptografia: o AddPersistence não as usa, mas o binding da seção
                // acontece antes e um valor ausente deixaria o teste dependente de ordem.
                ["Encryption:ActiveKeyVersion"] = "1"
            })
            .Build();

        var services = new ServiceCollection();

        services.AddPersistence(
            configuration,
            new TestEnvironment(),
            Assembly.GetExecutingAssembly(),
            migrateOnly: false,
            onHostResolved: null,
            onCapacityWarning: warnings.Add);

        return warnings;
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "CapacityTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider
        {
            get => new Microsoft.Extensions.FileProviders.NullFileProvider();
            set => throw new NotSupportedException();
        }
    }
}
