using System.Text.RegularExpressions;
using Garius.Tests.Infrastructure;

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
