using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Garius.Tests.Infrastructure;

namespace Garius.Tests.Observability;

/// <summary>
/// Traços: o sinal que responde <b>"onde foi o tempo"</b>.
///
/// <para>
/// Log responde "o que aconteceu". Nenhum dos dois substitui o outro, e num incidente de
/// latência é o traço que serve — ele decompõe "o request demorou 3 segundos" em "2.9s foram
/// nesta query".
/// </para>
///
/// <para>
/// Os testes usam um <see cref="ActivityListener"/> em vez de um exportador OTLP: o que
/// interessa provar é que as <b>activities existem e têm o formato certo</b>, não que o
/// protocolo de rede funciona (isso é responsabilidade da biblioteca).
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public class TracingTests(ApiFactory factory)
{
    /// <summary>
    /// Um request real produz um traço.
    ///
    /// <para>
    /// <b>Tem dentes:</b> remova o <c>AddAspNetCoreInstrumentation</c> do
    /// <c>ObservabilitySetup</c> e nenhuma activity de request é criada — o teste acusa.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Um_request_produz_um_traco()
    {
        using var recorder = new ActivityRecorder();

        var client = factory.CreateClient();

        await client.GetAsync("/", TestContext.Current.CancellationToken);

        recorder.Activities.ShouldContain(
            a => a.OperationName.Contains("GET", StringComparison.Ordinal)
                 || a.DisplayName.Contains('/', StringComparison.Ordinal),
            "sem traço do request, a pergunta 'por que isto demorou?' não tem resposta");
    }

    /// <summary>
    /// As probes <b>não</b> geram traço.
    ///
    /// <para>
    /// Não é economia de bytes: o kubelet chama o readiness de cada réplica a cada poucos
    /// segundos. Instrumentá-las faria delas a esmagadora maioria dos traços exportados —
    /// afogando os requests reais, que são os que se quer olhar, e inflando a conta do backend.
    /// </para>
    /// </summary>
    [Fact]
    public async Task As_probes_NAO_geram_traco()
    {
        using var recorder = new ActivityRecorder();

        var client = factory.CreateClient();

        await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        recorder.Activities
            .Where(a => a.DisplayName.Contains("health", StringComparison.OrdinalIgnoreCase))
            .ShouldBeEmpty(
                "as probes afogariam os traços reais: cada réplica é chamada a cada poucos segundos");
    }

    /// <summary>
    /// <b>O traceId da resposta é o do traço.</b>
    ///
    /// <para>
    /// É o que fecha o ciclo de um incidente: o cliente reporta o <c>traceId</c> que veio na
    /// resposta, e o operador cola esse valor no Grafana e acha exatamente aquele request. Se os
    /// dois formatos divergirem, o campo vira decoração — parece útil e não acha nada.
    /// </para>
    ///
    /// <para>
    /// O formato W3C tem 32 caracteres hexadecimais. O <c>TraceIdentifier</c> cru do ASP.NET
    /// tem outro (<c>0HN7A...:00000001</c>) — é essa diferença que o teste trava.
    /// </para>
    /// </summary>
    [Fact]
    public async Task O_traceId_da_resposta_esta_no_formato_do_traco()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        var traceId = body.GetProperty("traceId").GetString();

        traceId.ShouldNotBeNullOrWhiteSpace();

        traceId!.Length.ShouldBe(
            32,
            "o traceId da resposta tem de ser o do W3C trace context (32 hex), o mesmo que vai " +
            "para o Tempo — senão o cliente reporta um id que não acha nada no Grafana");

        traceId.ShouldAllBe(c => Uri.IsHexDigit(c));
    }

    /// <summary>
    /// Grava as activities criadas durante o teste. É o equivalente em memória de um
    /// exportador — sem rede, sem collector, sem flush assíncrono.
    /// </summary>
    private sealed class ActivityRecorder : IDisposable
    {
        private readonly ActivityListener _listener;

        internal List<Activity> Activities { get; } = [];

        internal ActivityRecorder()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = _ => true,

                // AllDataAndRecorded: sem isto o .NET cria activities "vazias" (sem tags nem
                // amostragem) e o teste não veria nada — um falso negativo silencioso.
                Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,

                ActivityStopped = activity =>
                {
                    lock (Activities)
                    {
                        Activities.Add(activity);
                    }
                }
            };

            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }
}
