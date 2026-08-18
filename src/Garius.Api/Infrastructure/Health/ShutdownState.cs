using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Garius.Api.Infrastructure.Health;

/// <summary>
/// Sabe se a aplicação já recebeu a ordem de encerrar.
///
/// <para>
/// <b>Existe por causa de uma corrida do orquestrador que custa requisição de cliente real.</b>
/// Quando o Kubernetes decide remover um pod, ele faz DUAS coisas <i>ao mesmo tempo</i>:
/// manda <c>SIGTERM</c> ao processo e remove o endpoint do Service. A segunda parte não é
/// instantânea — ela precisa se propagar pelo kube-proxy e pelo ingress, o que leva de um a
/// alguns segundos.
/// </para>
///
/// <para>
/// Nessa janela o pod <b>já começou a encerrar</b> mas o balanceador <b>ainda manda tráfego</b>.
/// Cada requisição que cai aí vira erro de conexão para alguém do outro lado. E isso não é um
/// caso raro: acontece em todo deploy, em todo scale-down do HPA e em toda migração de nó.
/// </para>
///
/// <para>
/// <b>A correção é o pod se declarar "não pronto" ANTES de parar de aceitar conexões.</b> Ele
/// continua servindo normalmente o que chega — só avisa ao balanceador, pelo
/// <c>/health/ready</c>, que pode parar de mandar coisa nova. Quando o endpoint some do
/// Service, o pod encerra de verdade, sem ter derrubado ninguém. Ver
/// <see cref="ShutdownHealthCheck"/>, que é quem traduz esta flag para o readiness.
/// </para>
///
/// <para>
/// <b>Singleton, e registrado no <c>ApplicationStopping</c>.</b> O <c>ApplicationStopping</c>
/// dispara no <c>SIGTERM</c>, antes de o servidor parar de aceitar conexões — que é
/// exatamente o instante em que se quer reprovar o readiness. O <c>ApplicationStopped</c>
/// seria tarde demais: nele o servidor já fechou.
/// </para>
/// </summary>
internal sealed class ShutdownState
{
    private volatile bool _isShuttingDown;

    /// <summary>
    /// A aplicação está encerrando? <c>volatile</c> porque quem escreve é a thread do
    /// lifetime e quem lê são as threads de requisição.
    /// </summary>
    internal bool IsShuttingDown => _isShuttingDown;

    internal void MarkAsShuttingDown() => _isShuttingDown = true;
}

/// <summary>
/// Reprova o <c>/health/ready</c> assim que o encerramento começa — ver <see cref="ShutdownState"/>.
///
/// <para>
/// Este check <b>não toca em dependência nenhuma</b>: ele lê uma flag em memória. É de
/// propósito. Ele precisa responder na hora, e um Postgres lento no meio de um shutdown não
/// pode atrasar o aviso ao balanceador.
/// </para>
/// </summary>
internal sealed class ShutdownHealthCheck(ShutdownState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(state.IsShuttingDown
            ? HealthCheckResult.Unhealthy("A aplicação está encerrando.")
            : HealthCheckResult.Healthy());
}
