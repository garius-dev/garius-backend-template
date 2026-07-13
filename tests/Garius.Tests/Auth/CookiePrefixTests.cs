using Garius.Api.Infrastructure.Authorization;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Garius.Tests.Auth;

/// <summary>
/// O prefixo <c>__Host-</c> dos cookies — e por que ele TEM de cair em desenvolvimento.
///
/// <para>
/// <b>Este teste nasceu de um loop de redirecionamento em produção-de-mentira.</b> O prefixo
/// <c>__Host-</c> <b>exige</b> a flag <c>Secure</c>: o navegador <b>descarta em silêncio</b> um
/// cookie <c>__Host-</c> que venha sem ela. Em <c>http://localhost</c> não há HTTPS, então a
/// política (<c>SameAsRequest</c>) omitia o <c>Secure</c> — e o cookie de sessão ia direto para
/// o lixo.
/// </para>
///
/// <para>
/// O sintoma era cruel: o login <b>funcionava</b> (o log dizia <c>login.success</c>), o
/// <c>Set-Cookie</c> ia na resposta, e mesmo assim <c>/scalar</c> devolvia para o formulário.
/// Um loop infinito, sem um único erro em lugar nenhum. Duas decisões corretas — o nome
/// <c>__Host-</c> e o <c>SameAsRequest</c> em dev — que juntas se anulavam.
/// </para>
/// </summary>
public class CookiePrefixTests
{
    /// <summary>
    /// <b>Produção mantém o <c>__Host-</c>.</b> É a proteção mais forte que existe contra um
    /// subdomínio comprometido sobrescrever o cookie de sessão — e lá há HTTPS, então o
    /// <c>Secure</c> vai junto e o navegador aceita.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Em_PRODUCAO_o_cookie_MANTEM_o_prefixo___Host(string environmentName)
    {
        var environment = Environment(environmentName);

        CookieAuthSetup.AuthCookie(environment).ShouldStartWith("__Host-");
        CookieAuthSetup.RefreshCookie(environment).ShouldStartWith("__Host-");
        CsrfProtection.CsrfCookie(environment).ShouldStartWith("__Host-");
    }

    /// <summary>
    /// <b>Desenvolvimento NÃO pode ter o prefixo</b> — senão o navegador descarta o cookie e o
    /// login entra em loop. É o bug que este teste existe para impedir de voltar.
    /// </summary>
    [Fact]
    public void Em_DESENVOLVIMENTO_o_cookie_NAO_pode_ter_o_prefixo___Host()
    {
        var environment = Environment("Development");

        CookieAuthSetup.AuthCookie(environment).ShouldNotStartWith("__Host-",
            customMessage: "sem HTTPS o cookie sai sem Secure, e o navegador DESCARTA um __Host- sem Secure");

        CookieAuthSetup.RefreshCookie(environment).ShouldNotStartWith("__Host-");
        CsrfProtection.CsrfCookie(environment).ShouldNotStartWith("__Host-");
    }

    /// <summary>
    /// Tirar o prefixo não pode virar cookie sem nome nem trocar o cookie de lugar: é o MESMO
    /// cookie, só sem os cinco caracteres que o navegador rejeitaria.
    /// </summary>
    [Fact]
    public void Cai_APENAS_o_prefixo_o_resto_do_nome_e_o_mesmo()
    {
        var dev = Environment("Development");
        var prod = Environment("Production");

        CookieAuthSetup.AuthCookie(dev)
            .ShouldBe(CookieAuthSetup.AuthCookie(prod).Replace("__Host-", string.Empty, StringComparison.Ordinal));

        CookieAuthSetup.RefreshCookie(dev)
            .ShouldBe(CookieAuthSetup.RefreshCookie(prod).Replace("__Host-", string.Empty, StringComparison.Ordinal));
    }

    private static Microsoft.Extensions.Hosting.Internal.HostingEnvironment Environment(string name) =>
        new() { EnvironmentName = name };
}
