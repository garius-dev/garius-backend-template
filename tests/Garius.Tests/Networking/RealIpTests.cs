using System.Net;
using Garius.Api.Infrastructure.Networking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Garius.Tests.Networking;

/// <summary>
/// A resolução do IP real é a base de rate limit, lockout e auditoria. Se ela puder ser
/// forjada, essas três proteções caem juntas — foi a falha do template anterior.
///
/// O contrato que estes testes travam: <c>CF-Connecting-IP</c> só é acreditado quando o
/// request chega <b>de dentro de um range da Cloudflare</b>.
/// </summary>
public class RealIpTests
{
    /// <summary>Pertence a 104.16.0.0/13, um range real da Cloudflare.</summary>
    private const string CloudflareIp = "104.16.0.1";

    private const string AttackerIp = "203.0.113.7";
    private const string VictimIp = "198.51.100.42";

    [Fact]
    public async Task Confia_no_CF_Connecting_IP_quando_o_request_vem_da_Cloudflare()
    {
        var resolved = await ResolveIpAsync(remoteIp: CloudflareIp, cfConnectingIp: VictimIp);

        resolved.ShouldBe(VictimIp);
    }

    [Fact]
    public async Task Ignora_o_CF_Connecting_IP_forjado_por_um_cliente_qualquer()
    {
        // Um atacante que NÃO está atrás da Cloudflare envia o header para se passar por
        // outro IP e escapar do rate limit / lockout. O header deve ser ignorado.
        var resolved = await ResolveIpAsync(remoteIp: AttackerIp, cfConnectingIp: VictimIp);

        resolved.ShouldBe(AttackerIp);
        resolved.ShouldNotBe(VictimIp);
    }

    [Fact]
    public async Task Usa_o_IP_da_conexao_quando_nao_ha_header()
    {
        var resolved = await ResolveIpAsync(remoteIp: AttackerIp, cfConnectingIp: null);

        resolved.ShouldBe(AttackerIp);
    }

    [Fact]
    public async Task Ignora_um_CF_Connecting_IP_malformado_vindo_da_Cloudflare()
    {
        var resolved = await ResolveIpAsync(remoteIp: CloudflareIp, cfConnectingIp: "não-é-um-ip");

        resolved.ShouldBe(CloudflareIp);
    }

    private static async Task<string> ResolveIpAsync(string remoteIp, string? cfConnectingIp)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .ConfigureServices(services =>
                    services.Configure<SecurityOptions>(options => options.TrustCloudflareIps = true))
                .Configure(app =>
                {
                    app.UseMiddleware<RealIpMiddleware>();
                    app.Run(context => context.Response.WriteAsync(context.GetClientIp()));
                }))
            .StartAsync(TestContext.Current.CancellationToken);

        var context = await host.GetTestServer().SendAsync(ctx =>
        {
            ctx.Request.Path = "/";
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);

            if (cfConnectingIp is not null)
            {
                ctx.Request.Headers[RealIpMiddleware.CloudflareHeader] = cfConnectingIp;
            }
        }, TestContext.Current.CancellationToken);

        return context.GetClientIp();
    }
}
