using System.Net;
using Garius.Tests.Infrastructure;

namespace Garius.Tests.Health;

/// <summary>
/// <b>A prova de ponta a ponta do encerramento gracioso:</b> sob carga real, com um
/// <c>SIGTERM</c> de verdade, <b>nenhuma requisição se perde</b>.
///
/// <para>
/// É a garantia que o item inteiro existe para dar, e a única que os testes unitários de
/// <see cref="GracefulShutdownTests"/> <b>não</b> conseguem provar — eles verificam as peças
/// (o check reprova, o cache invalida), não a soma delas sob carga.
/// </para>
///
/// <para>
/// <b>⚠️ Este teste é LENTO</b> (builda a imagem e sobe três containers) e é a categoria que
/// mais gera instabilidade. Ele está numa collection própria, sem paralelismo. Se ele começar a
/// piscar vermelho sem motivo, <b>não o apague</b>: investigue, ou mova-o para uma execução
/// separada do CI. Um teste instável treina as pessoas a ignorarem falha — e esta é a defesa
/// que protege todo deploy.
/// </para>
///
/// <para>
/// <b>Por que container.</b> Duas alternativas foram tentadas e descartadas, com medição:
/// </para>
/// <list type="number">
///   <item><c>WebApplicationFactory</c>: o <c>TestServer</c> <b>se descarta</b> no
///         <c>StopApplication()</c> e toda requisição seguinte estoura
///         <c>ObjectDisposedException</c>. Ele não tem janela de drenagem para medir.</item>
///   <item><c>Process.Start</c> + <c>CTRL_BREAK</c>: no Windows o sinal exige
///         <c>CREATE_NEW_PROCESS_GROUP</c>, flag que o <c>ProcessStartInfo</c> não expõe.
///         Medido: o processo seguia vivo após 30s e o log de encerramento nunca saía.</item>
/// </list>
///
/// <para>
/// O <c>docker stop</c> manda <c>SIGTERM</c> com grace period — literalmente o que o Kubernetes
/// faz. Ver <see cref="ContainerizedApi"/>.
/// </para>
/// </summary>
[Collection(ShutdownCollection.Name)]
[Trait("Category", "EndToEnd")]
public class ShutdownE2ETests
{
    /// <summary>
    /// <b>O teste central.</b> Carga contínua, <c>SIGTERM</c> no meio, <b>zero</b> requisição
    /// perdida.
    ///
    /// <para>
    /// As requisições são lentas de propósito (<c>/__test/slow</c>): é o que garante que várias
    /// estejam <b>em voo</b> no instante do sinal. Com endpoints instantâneos não haveria
    /// sobreposição — elas terminariam antes de o sinal chegar, e o teste passaria sem
    /// exercitar a drenagem.
    /// </para>
    ///
    /// <para>
    /// <b>Dentes:</b> remova o <c>ApplicationStopping.Register</c> que liga o
    /// <c>ShutdownState</c> (em <c>HealthSetup</c>) e o teste acusa — o pod deixaria de sair do
    /// balanceamento e as requisições em voo seriam abortadas.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Sob_carga_um_SIGTERM_nao_perde_nenhuma_requisicao()
    {
        await using var api = new ContainerizedApi();

        await api.StartAsync(TestContext.Current.CancellationToken);

        using var client = new HttpClient
        {
            BaseAddress = new Uri(api.BaseAddress),
            Timeout = TimeSpan.FromSeconds(30)
        };

        // Requisições de ~2s cada, disparadas em paralelo. Elas ainda estarão em voo quando o
        // sinal chegar.
        var inFlight = Enumerable.Range(0, 12)
            .Select(_ => client.GetAsync("/__test/slow?ms=2000", TestContext.Current.CancellationToken))
            .ToList();

        // Meio segundo: tempo de todas saírem e nenhuma terminar.
        await Task.Delay(500, TestContext.Current.CancellationToken);

        var outcome = await api.StopWithSigtermAsync(graceSeconds: 30);

        // ─── O que se mede ──────────────────────────────────────────────────

        var results = new List<HttpStatusCode?>();
        var failures = new List<string>();

        foreach (var request in inFlight)
        {
            try
            {
                results.Add((await request).StatusCode);
            }
            catch (HttpRequestException ex)
            {
                // ESTE é o erro que o encerramento gracioso existe para eliminar: a conexão
                // morrendo no meio porque o servidor parou de aceitar enquanto ainda havia
                // requisição em voo.
                failures.Add(ex.Message);
            }
            catch (TaskCanceledException)
            {
                failures.Add("timeout");
            }
        }

        failures.ShouldBeEmpty(
            $"""
             {failures.Count} de {inFlight.Count} requisições MORRERAM durante o encerramento.

             É exatamente a perda que o encerramento gracioso existe para eliminar, e ela
             acontece em todo deploy quando o mecanismo não funciona.

             Encerramento levou: {outcome.Duration.TotalSeconds:0.0}s

             Logs do container:
             {outcome.Logs}
             """);

        results.ShouldAllBe(
            status => status == HttpStatusCode.OK,
            "as requisições em voo tinham de ser ATENDIDAS, não recusadas — sair do " +
            "balanceamento não é o mesmo que parar de servir");
    }

    /// <summary>
    /// O encerramento é <b>rápido</b> quando não há nada em voo.
    ///
    /// <para>
    /// Importa porque o oposto é um modo de falha real: se a aplicação sempre esperasse o
    /// <c>ShutdownTimeout</c> inteiro, cada deploy ficaria N × 15s mais lento e o
    /// <c>terminationGracePeriodSeconds</c> do cluster precisaria ser enorme — aumentando a
    /// janela em que um pod morto ainda ocupa vaga.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Sem_nada_em_voo_o_encerramento_e_imediato()
    {
        await using var api = new ContainerizedApi();

        await api.StartAsync(TestContext.Current.CancellationToken);

        var outcome = await api.StopWithSigtermAsync(graceSeconds: 30);

        outcome.Duration.ShouldBeLessThan(
            TimeSpan.FromSeconds(10),
            $"""
             O encerramento levou {outcome.Duration.TotalSeconds:0.0}s sem nenhuma requisição em
             voo. Ele deveria ser quase instantâneo — esperar o ShutdownTimeout inteiro a cada
             deploy tornaria o rollout lento e exigiria um grace period enorme no cluster.

             Logs:
             {outcome.Logs}
             """);
    }

    /// <summary>
    /// <b>Durante a drenagem, o pod diz que NÃO está pronto — e continua servindo.</b>
    ///
    /// <para>
    /// Esta é a asserção que prova a corrida do orquestrador, e ela existe porque a ausência
    /// dela foi <b>medida</b>: com o <c>MarkAsShuttingDown()</c> neutralizado, os outros três
    /// testes desta classe <b>continuavam passando</b>. Eles olham só o resultado final —
    /// nenhum observava o instante intermediário, que é onde a defesa age.
    /// </para>
    ///
    /// <para>
    /// O Kubernetes remove o endpoint do Service <i>em paralelo</i> ao <c>SIGTERM</c>, e essa
    /// remoção leva segundos para propagar. É nessa janela que o pod precisa responder "não
    /// estou pronto" — para o balanceador parar de mandar tráfego novo — <b>sem</b> parar de
    /// atender o que já chegou.
    /// </para>
    ///
    /// <para>
    /// <b>Dentes (verificado):</b> comente o <c>shutdown.MarkAsShuttingDown()</c> em
    /// <c>HealthSetup.MapConfiguredHealthChecks</c> e este teste falha — nenhum 503 é observado,
    /// porque o readiness continua aprovando enquanto o pod morre.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Durante_a_drenagem_o_readiness_reprova()
    {
        await using var api = new ContainerizedApi();

        await api.StartAsync(TestContext.Current.CancellationToken);

        using var client = new HttpClient
        {
            BaseAddress = new Uri(api.BaseAddress),
            Timeout = TimeSpan.FromSeconds(30)
        };

        // Uma requisição lenta segura a drenagem aberta pelo tempo necessário para a enquete do
        // readiness enxergar a janela. Sem ela o encerramento é rápido demais e o teste passaria
        // por não ter observado nada — o pior tipo de verde.
        var inFlight = client.GetAsync("/__test/slow?ms=3000", TestContext.Current.CancellationToken);

        await Task.Delay(300, TestContext.Current.CancellationToken);

        var observation = await api.StopAndWatchReadinessAsync();

        observation.ReadinessDuringDrain.ShouldContain(
            HttpStatusCode.ServiceUnavailable,
            "O /health/ready NUNCA reprovou durante o encerramento. Sem isso, o balanceador " +
            "continua mandando tráfego para um pod que está morrendo — e cada requisição " +
            "dessas é um erro para um cliente real, em TODO deploy. Respostas observadas: " +
            string.Join(", ", observation.ReadinessDuringDrain) +
            ". Logs: " + observation.Outcome.Logs);

        // E a requisição que já estava em voo foi ATENDIDA. As duas metades importam: sair do
        // balanceamento sem terminar o que se começou seria só trocar o erro de lugar.
        (await inFlight).StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "o pod saiu do balanceamento mas tinha de terminar o que já estava em voo");
    }

    /// <summary>
    /// O log de encerramento <b>aparece</b> — prova de que o <c>SIGTERM</c> chegou ao
    /// <c>ApplicationStopping</c> e o <c>ShutdownState</c> foi acionado.
    ///
    /// <para>
    /// Sem esta asserção, os dois testes acima poderiam passar <b>pela razão errada</b>: um
    /// encerramento tão rápido que nada é perdido simplesmente porque nada estava em voo, ou um
    /// sinal que nunca chegou e um container morto por <c>SIGKILL</c> no fim do grace.
    /// </para>
    /// </summary>
    [Fact]
    public async Task O_SIGTERM_aciona_o_ShutdownState()
    {
        await using var api = new ContainerizedApi();

        await api.StartAsync(TestContext.Current.CancellationToken);

        var outcome = await api.StopWithSigtermAsync(graceSeconds: 30);

        outcome.Logs.ShouldContain(
            "Encerrando",
            Case.Sensitive,
            $"""
             O log de encerramento não apareceu: o SIGTERM não chegou ao ApplicationStopping, ou
             o ShutdownState não está registrado (ver HealthSetup.MapConfiguredHealthChecks).

             Sem isso, o /health/ready continua aprovando durante o encerramento e o balanceador
             segue mandando tráfego para um pod que está morrendo.

             Logs:
             {outcome.Logs}
             """);
    }
}

/// <summary>
/// Os testes ponta a ponta de encerramento.
///
/// <para>
/// Collection própria e <b>sem paralelismo</b>: cada teste builda a imagem e sobe três
/// containers. Rodá-los em paralelo com o resto da suíte competiria por CPU e por porta, e
/// transformaria um teste de timing num gerador de falso negativo.
/// </para>
/// </summary>
[CollectionDefinition(ShutdownCollection.Name, DisableParallelization = true)]
public sealed class ShutdownCollection
{
    public const string Name = "ShutdownE2E";
}
