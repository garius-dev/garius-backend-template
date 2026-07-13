using Garius.Core.Identity;
using Garius.Infrastructure.Caching;
using Garius.Infrastructure.Identity;
using Garius.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Garius.Tests.Identity;

/// <summary>
/// A rotação de refresh token é a peça mais fácil de errar em toda a autenticação — e o
/// template anterior errou: rotação não-atômica e família regerada a cada rotação faziam a
/// "detecção de replay" <b>não detectar nada</b>.
///
/// <para>
/// Estes testes rodam contra um Redis <b>real</b> (Testcontainers). Um mock não exercitaria o
/// script Lua, que é exatamente onde mora a atomicidade.
/// </para>
/// </summary>
public class RefreshTokenTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid TenantId = Guid.CreateVersion7();

    [Fact]
    public async Task Rotacionar_consome_o_antigo_e_emite_um_novo()
    {
        var store = await BuildAsync();

        var first = await store.IssueAsync(UserId, TenantId, TestContext.Current.CancellationToken);

        var rotated = await store.RotateAsync(first.Token, TestContext.Current.CancellationToken);

        rotated.ShouldNotBeNull();
        rotated.Value.Token.Token.ShouldNotBe(first.Token);
        rotated.Value.Data.UserId.ShouldBe(UserId);
        rotated.Value.Data.TenantId.ShouldBe(TenantId);
    }

    /// <summary>
    /// <b>A família é a SESSÃO</b>, e atravessa todas as rotações.
    ///
    /// <para>
    /// O template anterior gerava uma família nova a cada rotação — então cada "família" tinha
    /// um token só, e "revogar a família" não revogava nada. Sem esta propriedade, a detecção
    /// de reuso é decorativa.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_familia_e_PRESERVADA_atraves_das_rotacoes()
    {
        var store = await BuildAsync();

        var token = await store.IssueAsync(UserId, TenantId, TestContext.Current.CancellationToken);

        var first = await store.RotateAsync(token.Token, TestContext.Current.CancellationToken);
        var second = await store.RotateAsync(first!.Value.Token.Token, TestContext.Current.CancellationToken);
        var third = await store.RotateAsync(second!.Value.Token.Token, TestContext.Current.CancellationToken);

        first.Value.Data.FamilyId.ShouldBe(second!.Value.Data.FamilyId);
        second.Value.Data.FamilyId.ShouldBe(third!.Value.Data.FamilyId);
    }

    /// <summary>
    /// <b>O teste central.</b> Um token roubado é usado pelo atacante; depois o usuário
    /// legítimo tenta usar o seu (que já foi consumido). O reuso é detectado e a sessão inteira
    /// cai — o atacante perde o acesso, e o legítimo percebe (tem que logar de novo).
    /// </summary>
    [Fact]
    public async Task Reusar_um_token_consumido_REVOGA_a_familia_inteira()
    {
        var store = await BuildAsync();

        var original = await store.IssueAsync(UserId, TenantId, TestContext.Current.CancellationToken);

        // O atacante rouba o token e o usa primeiro.
        var stolen = await store.RotateAsync(original.Token, TestContext.Current.CancellationToken);
        stolen.ShouldNotBeNull();

        // O usuário legítimo tenta usar o MESMO token (que já foi consumido) → reuso.
        var reuse = await store.RotateAsync(original.Token, TestContext.Current.CancellationToken);
        reuse.ShouldBeNull("um token já consumido não pode ser aceito");

        // E o token que o ATACANTE obteve também morreu: a família inteira foi revogada.
        var attackerNext = await store.RotateAsync(
            stolen.Value.Token.Token, TestContext.Current.CancellationToken);

        attackerNext.ShouldBeNull(
            "detectar o reuso precisa derrubar a sessão INTEIRA — do contrário o atacante " +
            "continua com um token válido em mãos");
    }

    /// <summary>
    /// <b>Atomicidade.</b> Duas requisições concorrentes com o mesmo token: exatamente uma pode
    /// ganhar. Sem o script Lua, ambas leriam "ainda não consumido" e as duas emitiriam um
    /// token novo — o replay que a detecção deveria pegar.
    /// </summary>
    [Fact]
    public async Task Rotacoes_CONCORRENTES_com_o_mesmo_token_nao_produzem_dois_vencedores()
    {
        var store = await BuildAsync();

        var token = await store.IssueAsync(UserId, TenantId, TestContext.Current.CancellationToken);

        // 10 requisições disparadas ao mesmo tempo com o mesmo token.
        var attempts = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ =>
                store.RotateAsync(token.Token, TestContext.Current.CancellationToken)));

        var winners = attempts.Count(a => a is not null);

        winners.ShouldBeLessThanOrEqualTo(
            1,
            "duas rotações do mesmo token seriam um replay — é o que o script Lua impede");
    }

    [Fact]
    public async Task Um_token_inexistente_e_rejeitado()
    {
        var store = await BuildAsync();

        var result = await store.RotateAsync("token-que-nunca-existiu", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Logout_revoga_a_sessao()
    {
        var store = await BuildAsync();

        var token = await store.IssueAsync(UserId, TenantId, TestContext.Current.CancellationToken);
        var rotated = await store.RotateAsync(token.Token, TestContext.Current.CancellationToken);

        await store.RevokeAsync(rotated!.Value.Token.Token, TestContext.Current.CancellationToken);

        var afterLogout = await store.RotateAsync(
            rotated.Value.Token.Token, TestContext.Current.CancellationToken);

        afterLogout.ShouldBeNull();
    }

    /// <summary>"Sair de todos os dispositivos" — e o que se faz quando a senha é trocada.</summary>
    [Fact]
    public async Task Revogar_o_usuario_derruba_TODAS_as_sessoes_dele()
    {
        var store = await BuildAsync();

        var userId = Guid.CreateVersion7();

        // Três dispositivos: três famílias diferentes.
        var phone = await store.IssueAsync(userId, TenantId, TestContext.Current.CancellationToken);
        var laptop = await store.IssueAsync(userId, TenantId, TestContext.Current.CancellationToken);
        var tablet = await store.IssueAsync(userId, TenantId, TestContext.Current.CancellationToken);

        await store.RevokeAllForUserAsync(userId, TestContext.Current.CancellationToken);

        (await store.RotateAsync(phone.Token, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await store.RotateAsync(laptop.Token, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await store.RotateAsync(tablet.Token, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    /// <summary>
    /// O token nunca é gravado — só o seu SHA-256. Um dump do Redis não entrega tokens
    /// utilizáveis, apenas hashes.
    /// </summary>
    [Fact]
    public async Task O_token_NAO_e_gravado_em_claro_no_Redis()
    {
        var store = await BuildAsync();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);

        var token = await store.IssueAsync(UserId, TenantId, TestContext.Current.CancellationToken);

        var server = connection.GetServer(connection.GetEndPoints()[0]);
        var keys = server.Keys(pattern: "*").Select(k => k.ToString()).ToList();

        keys.ShouldNotBeEmpty();
        keys.ShouldNotContain(
            k => k.Contains(token.Token, StringComparison.Ordinal),
            "o token em claro não pode aparecer em nenhuma chave do Redis");
    }

    private async Task<IRefreshTokenStore> BuildAsync()
    {
        var multiplexer = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);

        // Cada teste usa um prefixo próprio: não interferem entre si.
        var options = new RedisOptions
        {
            ConnectionString = fixture.RedisConnectionString,
            InstanceName = $"test-{Guid.NewGuid():N}"
        };

        return new RedisRefreshTokenStore(
            multiplexer,
            options,
            TimeProvider.System,
            NullLogger<RedisRefreshTokenStore>.Instance);
    }
}
