using Garius.Core.Security;

namespace Garius.Api.Infrastructure.Security;

/// <summary>
/// O "usuário" do bootstrap (<c>MIGRATE_ONLY</c>): ninguém.
///
/// <para>
/// No modo migração não existe HTTP, não existe requisição e não existe usuário autenticado —
/// e é por isso que o <see cref="HttpCurrentUser"/> <b>não serve</b> ali: ele depende do
/// <c>IPermissionResolver</c>, que por sua vez depende do Redis e de toda a pilha de
/// autorização, nenhum deles registrado no bootstrap. Registrá-lo mesmo assim fazia o
/// <c>builder.Build()</c> estourar na validação do container, e o bootstrap <b>não rodava</b>.
/// </para>
///
/// <para>
/// A interface já previa este caso ("<c>null</c> em um job, no bootstrap ou num request
/// anônimo") — faltava a implementação.
/// </para>
///
/// <para>
/// <b><see cref="HasPermissionAsync"/> devolve sempre <c>false</c>, não <c>true</c>.</b> É
/// deliberado, e é a escolha que falha FECHADA: se algum dia um código de bootstrap passar a
/// consultar permissão, ele será NEGADO — em vez de rodar com um curinga implícito de
/// superusuário que ninguém escreveu e ninguém revisou. O bootstrap cria schema e roles; ele
/// não tem por que perguntar "posso?".
/// </para>
/// </summary>
internal sealed class SystemCurrentUser : ICurrentUser
{
    public Guid? UserId => null;

    public string? ClientIp => null;

    public string? TraceId => null;

    public Task<bool> HasPermissionAsync(
        string permission,
        CancellationToken cancellationToken = default) => Task.FromResult(false);
}
