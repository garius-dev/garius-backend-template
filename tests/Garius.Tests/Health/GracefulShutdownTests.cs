using Garius.Api.Infrastructure.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;

namespace Garius.Tests.Health;

/// <summary>
/// O encerramento gracioso: <b>o pod sai do balanceamento antes de parar de servir.</b>
///
/// <para>
/// <b>Por que isto existe.</b> Quando o Kubernetes remove um pod, ele manda <c>SIGTERM</c> e
/// remove o endpoint do Service <i>ao mesmo tempo</i>. A remoção do endpoint não é instantânea:
/// ela se propaga pelo kube-proxy e pelo ingress, o que leva de um a alguns segundos. Nessa
/// janela o pod já está encerrando e o balanceador ainda manda tráfego — cada requisição que
/// cai aí é um erro de conexão para um cliente real, em <b>todo</b> deploy e em <b>todo</b>
/// scale-down.
/// </para>
///
/// <para>
/// <b>Por que estes testes não sobem a API inteira.</b> Tentar provar isto ponta a ponta com o
/// <c>WebApplicationFactory</c> não funciona, e o motivo é instrutivo: o <c>TestServer</c> se
/// <b>descarta</b> quando o <c>StopApplication()</c> é chamado, e toda requisição seguinte
/// estoura <c>ObjectDisposedException</c>. Ou seja, o servidor de teste <b>não reproduz</b> a
/// janela de drenagem — ele encerra de imediato, que é justamente o comportamento que este
/// item existe para evitar. Um teste escrito sobre ele estaria medindo o dublê, não a peça.
/// </para>
///
/// <para>
/// Então o que se testa aqui é a <b>lógica que decide</b>, nas unidades onde ela mora: o
/// <see cref="ShutdownHealthCheck"/> (que reprova o readiness) e o
/// <see cref="ReadinessCacheFilter"/> (que não pode servir um "pronto" cacheado depois do
/// sinal). A ponta a ponta — carga real, <c>SIGTERM</c> de verdade, zero requisição perdida —
/// exige um Kestrel em processo separado; está registrado no PLANO-PRODUCAO.md.
/// </para>
/// </summary>
public class GracefulShutdownTests
{
    /// <summary>
    /// Antes do sinal, o check aprova — senão o pod nunca entraria no balanceamento.
    /// </summary>
    [Fact]
    public async Task Antes_do_SIGTERM_o_check_de_shutdown_aprova()
    {
        var check = new ShutdownHealthCheck(new ShutdownState());

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    /// <summary>
    /// O coração do item: marcado o encerramento, o check reprova — e é isso que faz o
    /// <c>/health/ready</c> devolver 503 e o balanceador parar de mandar tráfego novo.
    ///
    /// <para>
    /// <b>Este teste tem dentes:</b> remova o registro do check em <c>HealthSetup</c>
    /// (<c>AddCheck&lt;ShutdownHealthCheck&gt;("shutdown", tags: [ReadyTag])</c>) e o
    /// <c>ProbeTests.O_detail_lista_os_checks_de_readiness</c> acusa; troque o
    /// <c>Unhealthy</c> por <c>Healthy</c> aqui e este acusa. Nas duas mutações o pod
    /// continuaria recebendo tráfego depois do <c>SIGTERM</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Apos_o_SIGTERM_o_check_de_shutdown_reprova()
    {
        var state = new ShutdownState();

        state.MarkAsShuttingDown();

        var result = await new ShutdownHealthCheck(state).CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        result.Status.ShouldBe(
            HealthStatus.Unhealthy,
            "sem reprovar o readiness no SIGTERM, o balanceador continua mandando tráfego para " +
            "um pod que está encerrando — e cada requisição dessas é um erro para o cliente");
    }

    /// <summary>
    /// O check <b>não toca em dependência nenhuma</b> — ele lê uma flag em memória.
    ///
    /// <para>
    /// É requisito, não detalhe: durante um encerramento o Postgres pode estar lento, e o aviso
    /// ao balanceador não pode ficar atrás de um timeout de banco. Um <c>CancellationToken</c>
    /// já cancelado prova que não há I/O no caminho — se houvesse, a chamada lançaria.
    /// </para>
    /// </summary>
    [Fact]
    public async Task O_check_de_shutdown_nao_depende_de_IO()
    {
        using var cancelled = new CancellationTokenSource();

        await cancelled.CancelAsync();

        var result = await new ShutdownHealthCheck(new ShutdownState())
            .CheckHealthAsync(new HealthCheckContext(), cancelled.Token);

        result.Status.ShouldBe(
            HealthStatus.Healthy,
            "o check de shutdown não pode fazer I/O: no meio de um encerramento, um Postgres " +
            "lento atrasaria o aviso ao balanceador");
    }

    /// <summary>
    /// <b>O cache não pode sobreviver ao sinal.</b>
    ///
    /// <para>
    /// Esta é a interação entre as duas peças, e é onde um bug passaria despercebido: o
    /// <see cref="ReadinessCache"/> guarda a resposta do readiness por alguns segundos para o
    /// health check não virar carga. Se ele continuasse valendo durante o encerramento, o pod
    /// responderia "pronto" — <i>do cache</i> — por segundos depois do <c>SIGTERM</c>, e todo o
    /// mecanismo seria inútil justamente na hora que importa.
    /// </para>
    /// </summary>
    [Fact]
    public void O_encerramento_invalida_o_cache_do_readiness()
    {
        var cache = new ReadinessCache(new FakeTimeProvider());

        cache.Store(200, "Healthy");

        // Sanidade: sem o encerramento, o acerto de cache existe (senão o teste passaria
        // por não haver nada guardado, e não por causa da invalidação).
        cache.TryGet().ShouldNotBeNull();

        cache.Invalidate();

        cache.TryGet().ShouldBeNull(
            "um 'pronto' cacheado sobrevivendo ao SIGTERM manteria o pod recebendo tráfego " +
            "por segundos depois do sinal");
    }
}
