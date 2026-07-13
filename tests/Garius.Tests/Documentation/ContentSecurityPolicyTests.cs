using System.Text.RegularExpressions;
using Garius.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Garius.Tests.Documentation;

/// <summary>
/// O CSP: fechado para a API, e o mínimo necessário para as páginas HTML.
///
/// <para>
/// <b>Estes testes nasceram de um bug silencioso.</b> O CSP era
/// <c>default-src 'none'</c> para <b>TODAS</b> as respostas — inclusive as três páginas HTML do
/// template. O navegador <b>descartava o CSS</b> da página de login, que era servida como HTML
/// cru, sem estilo nenhum. Ninguém percebeu: um CSP bloqueado não quebra a página, só a deixa
/// feia — e o <c>&lt;style&gt;</c> estava lá no código, parecendo que funcionava.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public class ContentSecurityPolicyTests(ApiFactory factory)
{
    /// <summary>
    /// Sem seguir o redirect: <c>/scalar</c> e <c>/jobs</c> mandam um anônimo para o login, e o
    /// que interessa aqui é o CSP que ELES emitem — que vai no header de qualquer forma.
    /// </summary>
    private static readonly WebApplicationFactoryClientOptions NoRedirect =
        new() { AllowAutoRedirect = false };

    /// <summary>
    /// O que o bug quebrava: o CSS da página de login tem de poder EXECUTAR. O nonce do header
    /// precisa ser o mesmo carimbado no <c>&lt;style&gt;</c> — se divergirem, o navegador
    /// descarta o CSS e ninguém fica sabendo.
    /// </summary>
    [Fact]
    public async Task O_nonce_do_header_e_o_MESMO_do_style_da_pagina()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/login", TestContext.Current.CancellationToken);

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var fromHeader = Regex.Match(csp, @"'nonce-([^']+)'").Groups[1].Value;
        var fromStyle = Regex.Match(html, @"<style nonce=""([^""]+)""").Groups[1].Value;

        Assert.False(string.IsNullOrEmpty(fromHeader), "o CSP da página precisa trazer um nonce");
        Assert.False(string.IsNullOrEmpty(fromStyle), "o <style> precisa carimbar o nonce");

        Assert.Equal(fromHeader, fromStyle);
    }

    /// <summary>
    /// O nonce é inútil se for previsível: ele tem de ser NOVO a cada resposta. Um nonce fixo
    /// seria o mesmo que <c>'unsafe-inline'</c> — um XSS aprenderia o valor e o reutilizaria.
    /// </summary>
    [Fact]
    public async Task O_nonce_MUDA_a_cada_requisicao()
    {
        using var client = factory.CreateClient();

        var first = await Nonce(client);
        var second = await Nonce(client);

        Assert.NotEqual(first, second);

        async Task<string> Nonce(HttpClient http)
        {
            var response = await http.GetAsync("/admin/login", TestContext.Current.CancellationToken);
            var csp = response.Headers.GetValues("Content-Security-Policy").Single();

            return Regex.Match(csp, @"'nonce-([^']+)'").Groups[1].Value;
        }
    }

    /// <summary>
    /// <b>A API NÃO afrouxa.</b> Relaxar o CSP para as páginas não pode vazar para as respostas
    /// JSON — que nunca carregam nada, nunca executam script, e devem seguir no
    /// <c>default-src 'none'</c> mais fechado que existe.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData("/health/ready")]
    public async Task A_API_continua_com_o_CSP_TOTALMENTE_fechado(string path)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();

        Assert.Equal("default-src 'none'; frame-ancestors 'none'", csp);

        // Se um destes aparecer numa resposta JSON, o relaxamento vazou.
        Assert.DoesNotContain("nonce", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A REGRA DE OURO do CSP, e a que fez a página do Scalar ficar EM BRANCO.</b>
    ///
    /// <para>
    /// Pela spec, o navegador <b>IGNORA</b> o <c>'unsafe-inline'</c> quando há um nonce na mesma
    /// diretiva. Não é um fallback — é uma <b>anulação</b>. Mandar os dois juntos não dá "o
    /// melhor dos dois mundos": dá o comportamento do nonce, e tudo que não o carrega é
    /// bloqueado.
    /// </para>
    ///
    /// <para>
    /// Foi exatamente o que aconteceu: o CSP mandava <c>'nonce-...' 'unsafe-inline'</c> para
    /// TODAS as páginas. O Scalar monta a interface em runtime por JavaScript, e um
    /// <c>&lt;style&gt;</c> criado por <c>document.createElement</c> <b>não carrega nonce</b> —
    /// então tudo foi bloqueado e a página veio em branco.
    /// </para>
    ///
    /// <para>
    /// Ou um, ou outro. <b>Nunca os dois.</b>
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("/admin/login")]
    [InlineData("/scalar")]
    [InlineData("/jobs")]
    public async Task Nonce_e_unsafe_inline_NUNCA_na_mesma_diretiva(string path)
    {
        using var client = factory.CreateClient(NoRedirect);

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();

        foreach (var directive in csp.Split(';', StringSplitOptions.TrimEntries))
        {
            var hasNonce = directive.Contains("nonce-", StringComparison.Ordinal);
            var hasUnsafeInline = directive.Contains("'unsafe-inline'", StringComparison.Ordinal);

            Assert.False(
                hasNonce && hasUnsafeInline,
                $"'{directive}' tem nonce E 'unsafe-inline'. O navegador IGNORA o 'unsafe-inline' " +
                "quando há nonce — o que faz o Scalar (que injeta CSS por JS, sem nonce) vir em branco.");
        }
    }

    /// <summary>
    /// O Scalar e o Hangfire PRECISAM do <c>'unsafe-inline'</c> — eles injetam CSS e JS em
    /// runtime, por JavaScript, e um nó criado assim não tem como carregar nonce.
    /// </summary>
    [Theory]
    [InlineData("/scalar")]
    [InlineData("/jobs")]
    public async Task As_paginas_de_TERCEIROS_permitem_inline_e_NAO_usam_nonce(string path)
    {
        using var client = factory.CreateClient(NoRedirect);

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();

        Assert.Contains("style-src 'self' 'unsafe-inline'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self' 'unsafe-inline'", csp, StringComparison.Ordinal);

        // Um nonce aqui anularia o 'unsafe-inline' e deixaria a página em branco.
        Assert.DoesNotContain("nonce-", csp, StringComparison.Ordinal);
    }

    /// <summary>
    /// A NOSSA página (o login) usa nonce e <b>não</b> tem <c>'unsafe-inline'</c> — nós
    /// escrevemos o HTML, então o nonce funciona. É a página que recebe senha e guarda o cookie
    /// de sessão: é onde a proteção mais importa.
    /// </summary>
    [Fact]
    public async Task A_NOSSA_pagina_usa_nonce_e_NAO_permite_inline()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/login", TestContext.Current.CancellationToken);

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();

        Assert.Contains("nonce-", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-inline'", csp, StringComparison.Ordinal);

        // A página de login não tem JavaScript nenhum.
        Assert.Contains("script-src 'none'", csp, StringComparison.Ordinal);
    }

    /// <summary>
    /// Mesmo relaxado, o CSP da página mantém as travas que impedem os ataques clássicos:
    /// clickjacking, sequestro de &lt;base&gt; e post do formulário para fora.
    /// </summary>
    [Fact]
    public async Task O_CSP_da_pagina_mantem_as_travas_que_importam()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/login", TestContext.Current.CancellationToken);

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();

        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("base-uri 'none'", csp, StringComparison.Ordinal);

        // O login não pode postar a senha para outra origem.
        Assert.Contains("form-action 'self'", csp, StringComparison.Ordinal);
    }
}
