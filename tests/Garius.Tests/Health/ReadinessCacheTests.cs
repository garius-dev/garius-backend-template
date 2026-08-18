using Garius.Api.Infrastructure.Health;
using Microsoft.Extensions.Time.Testing;

namespace Garius.Tests.Health;

/// <summary>
/// O cache do readiness — a peça que impede o health check de virar carga.
///
/// <para>
/// <b>O problema que ele resolve.</b> O kubelet chama o readiness de cada pod a cada poucos
/// segundos. Com 10 réplicas e período de 5s, são duas consultas por segundo ao Postgres e ao
/// Redis <i>só de monitoramento</i>. Quando o banco está sofrendo — que é exatamente quando o
/// readiness importa — esse tráfego extra <b>piora</b> o problema que ele deveria só observar.
/// </para>
///
/// <para>
/// Testes unitários, com <c>FakeTimeProvider</c>: a janela é de segundos, e um teste que
/// esperasse de verdade seria lento e instável.
/// </para>
/// </summary>
public class ReadinessCacheTests
{
    [Fact]
    public void Dentro_da_janela_devolve_a_resposta_guardada()
    {
        var time = new FakeTimeProvider();
        var cache = new ReadinessCache(time);

        cache.Store(200, "Healthy");

        time.Advance(TimeSpan.FromSeconds(1));

        cache.TryGet().ShouldNotBeNull().Body.ShouldBe("Healthy");
    }

    [Fact]
    public void Passada_a_janela_o_cache_expira()
    {
        var time = new FakeTimeProvider();
        var cache = new ReadinessCache(time);

        cache.Store(200, "Healthy");

        // Além da janela de 3s. Expirar é o que garante que uma falha REAL seja detectada
        // rápido: um cache longo esconderia o pod adoecendo.
        time.Advance(TimeSpan.FromSeconds(5));

        cache.TryGet().ShouldBeNull(
            "um cache que não expira esconde o pod adoecendo — ele continuaria recebendo tráfego");
    }

    /// <summary>
    /// A invalidação existe por causa do encerramento: uma resposta "pronto" guardada
    /// continuaria atraindo tráfego por segundos depois do <c>SIGTERM</c>.
    /// </summary>
    [Fact]
    public void A_invalidacao_descarta_o_que_estava_guardado()
    {
        var time = new FakeTimeProvider();
        var cache = new ReadinessCache(time);

        cache.Store(200, "Healthy");
        cache.Invalidate();

        cache.TryGet().ShouldBeNull();
    }

    [Fact]
    public void Sem_nada_guardado_nao_ha_acerto()
    {
        new ReadinessCache(new FakeTimeProvider()).TryGet().ShouldBeNull();
    }
}
