using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Garius.Core.Authorization;
using Garius.Core.Machine;
using Garius.Core.Tenancy;
using Garius.Infrastructure.Database;
using Garius.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Garius.Tests.Auth;

/// <summary>
/// Autenticação de <b>máquina</b> ponta a ponta, contra a API real: OAuth2 client credentials
/// (JWT) e chaves de API.
///
/// <para>
/// A tese que estes testes provam é uma só: <b>pessoa, client OAuth e chave de API passam pelo
/// mesmo <c>.RequirePermission(...)</c></b>. Não há um segundo vocabulário de escopos, nem um
/// segundo handler de autorização.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public class MachineAuthTests(ApiFactory factory)
{
    [Fact]
    public async Task Client_credentials_troca_o_segredo_por_um_JWT()
    {
        var (clientId, secret, _) = await SeedClientAsync([Permissions.Users.Read.Value]);

        var response = await RequestTokenAsync(clientId, secret);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        // A resposta segue a RFC 6749: os campos ficam na RAIZ, não dentro do envelope `data`
        // da API. É a única exceção deliberada ao contrato de resposta — e existe para que um
        // cliente OAuth padrão funcione sem adaptação nenhuma.
        body.TryGetProperty("data", out _).ShouldBeFalse(
            "a resposta de token segue a RFC 6749, não o envelope da API — " +
            "toda biblioteca OAuth espera access_token na raiz");

        body.GetProperty("access_token").GetString().ShouldNotBeNullOrEmpty();
        body.GetProperty("token_type").GetString().ShouldBe("Bearer");
        body.GetProperty("expires_in").GetInt32().ShouldBeGreaterThan(0);
        body.GetProperty("scope").GetString().ShouldBe(Permissions.Users.Read.Value);
    }

    /// <summary>
    /// <b>O teste central.</b> Um client OAuth chega a um endpoint protegido por permissão
    /// usando o mesmo <c>.RequirePermission(...)</c> que serve um usuário logado por cookie.
    /// </summary>
    [Fact]
    public async Task Um_JWT_com_o_escopo_certo_autoriza_o_endpoint_protegido()
    {
        var (clientId, secret, _) = await SeedClientAsync([Permissions.Users.Read.Value]);

        var token = await GetAccessTokenAsync(clientId, secret);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/__test/protected", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "o endpoint exige a PERMISSÃO users.read e não se importa com quem a tem — " +
            "é o ponto do desenho inteiro");
    }

    [Fact]
    public async Task Um_JWT_SEM_o_escopo_do_endpoint_recebe_403()
    {
        // O client existe e o token é válido — mas o escopo é outro.
        var (clientId, secret, _) = await SeedClientAsync([Permissions.Roles.Read.Value]);

        var token = await GetAccessTokenAsync(clientId, secret);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/__test/protected", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "autenticado não é autorizado: o token é válido, mas não carrega users.read");
    }

    [Fact]
    public async Task Segredo_errado_e_client_inexistente_dao_a_MESMA_resposta()
    {
        var (clientId, _, _) = await SeedClientAsync([Permissions.Users.Read.Value]);

        var wrongSecret = await RequestTokenAsync(clientId, "segredo-errado");
        var unknownClient = await RequestTokenAsync("cid_nao_existe", "qualquer-coisa");

        wrongSecret.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknownClient.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var a = await wrongSecret.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        var b = await unknownClient.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        // Idênticas. Distinguir "este client não existe" de "o segredo está errado" faria do
        // endpoint um oráculo que enumera os client_ids válidos da aplicação.
        a.GetProperty("error").GetString().ShouldBe(b.GetProperty("error").GetString());
        a.GetProperty("error_description").GetString()
            .ShouldBe(b.GetProperty("error_description").GetString());
    }

    [Fact]
    public async Task Um_client_revogado_nao_consegue_mais_emitir_token()
    {
        var (clientId, secret, id) = await SeedClientAsync([Permissions.Users.Read.Value]);

        // Funciona antes.
        (await RequestTokenAsync(clientId, secret)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await RevokeAsync<OAuthClient>(id);

        // E não funciona depois. (O JWT que ele JÁ tenha em mãos continua valendo até expirar —
        // é a consequência inevitável de um token stateless. Ver MachineAuthService.RevokeClientAsync.)
        (await RequestTokenAsync(clientId, secret)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Um_grant_type_diferente_de_client_credentials_e_rejeitado()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/token",
            new { grant_type = "password", client_id = "x", client_secret = "y" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        body.GetProperty("error").GetString().ShouldBe("unsupported_grant_type");
    }

    // --- chaves de API -------------------------------------------------------

    [Fact]
    public async Task Uma_chave_de_API_com_o_escopo_certo_autoriza_o_endpoint_protegido()
    {
        var (key, _) = await SeedApiKeyAsync([Permissions.Users.Read.Value]);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(MachineAuth.ApiKeyHeader, key);

        var response = await client.GetAsync("/__test/protected", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "a chave de API chega ao MESMO .RequirePermission que serve o cookie e o JWT");
    }

    [Fact]
    public async Task Uma_chave_de_API_invalida_recebe_401()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(MachineAuth.ApiKeyHeader, "gk_chave-que-nao-existe");

        var response = await client.GetAsync("/__test/protected", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A diferença que justifica a existência das duas coisas: revogar uma chave de API tem
    /// efeito <b>imediato</b> (ela vai ao banco a cada request), enquanto um JWT já emitido
    /// sobrevive até expirar.
    /// </summary>
    [Fact]
    public async Task Revogar_uma_chave_de_API_tem_efeito_IMEDIATO()
    {
        var (key, id) = await SeedApiKeyAsync([Permissions.Users.Read.Value]);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(MachineAuth.ApiKeyHeader, key);

        (await client.GetAsync("/__test/protected", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await RevokeAsync<ApiKey>(id);

        (await client.GetAsync("/__test/protected", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(
                HttpStatusCode.Unauthorized,
                "a chave consulta o banco a cada request — é o custo que compra a revogação imediata");
    }

    /// <summary>
    /// A <b>quota</b>: é ela que transforma um vazamento silencioso e ilimitado num vazamento
    /// que trava e aparece.
    /// </summary>
    [Fact]
    public async Task Uma_chave_de_API_para_de_funcionar_quando_a_quota_acaba()
    {
        var (key, _) = await SeedApiKeyAsync([Permissions.Users.Read.Value], callLimit: 2);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(MachineAuth.ApiKeyHeader, key);

        // As duas chamadas da quota passam.
        (await client.GetAsync("/__test/protected", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/__test/protected", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // A terceira, não.
        (await client.GetAsync("/__test/protected", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(
                HttpStatusCode.Unauthorized,
                "a quota acabou: a chave para de funcionar e alguém precisa olhar para ela");
    }

    [Fact]
    public async Task O_contador_de_uso_da_chave_e_incrementado_a_cada_chamada()
    {
        var (key, id) = await SeedApiKeyAsync([Permissions.Users.Read.Value]);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(MachineAuth.ApiKeyHeader, key);

        await client.GetAsync("/__test/protected", TestContext.Current.CancellationToken);
        await client.GetAsync("/__test/protected", TestContext.Current.CancellationToken);
        await client.GetAsync("/__test/protected", TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stored = await db.ApiKeys
            .IgnoreQueryFilters()
            .SingleAsync(k => k.Id == id, TestContext.Current.CancellationToken);

        stored.CallCount.ShouldBe(3);
        stored.LastUsedAt.ShouldNotBeNull();
    }

    // --- o segredo não vaza ---------------------------------------------------

    [Fact]
    public async Task O_segredo_do_client_NAO_e_gravado_em_claro()
    {
        var (_, secret, id) = await SeedClientAsync([Permissions.Users.Read.Value]);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stored = await db.OAuthClients
            .IgnoreQueryFilters()
            .SingleAsync(c => c.Id == id, TestContext.Current.CancellationToken);

        stored.SecretHash.ShouldNotBe(secret, "o banco guarda o HASH, nunca o segredo");
        stored.SecretHash.ShouldBe(MachineCredential.Hash(secret));
    }

    /// <summary>
    /// Uma requisição SEM credencial nenhuma continua sendo barrada — o forwarder de esquema
    /// não abriu nenhuma porta nova.
    /// </summary>
    [Fact]
    public async Task Sem_credencial_nenhuma_o_endpoint_protegido_continua_fechado()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/__test/protected", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // --- helpers -------------------------------------------------------------

    private Task<HttpResponseMessage> RequestTokenAsync(string clientId, string secret) =>
        factory.CreateClient().PostAsJsonAsync(
            "/auth/token",
            new
            {
                grant_type = MachineAuth.ClientCredentialsGrant,
                client_id = clientId,
                client_secret = secret
            },
            TestContext.Current.CancellationToken);

    private async Task<string> GetAccessTokenAsync(string clientId, string secret)
    {
        var response = await RequestTokenAsync(clientId, secret);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        return body.GetProperty("access_token").GetString()!;
    }

    private async Task<(string ClientId, string Secret, Guid Id)> SeedClientAsync(string[] scopes)
    {
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var secret = MachineCredential.Generate();

        var client = new OAuthClient
        {
            ClientId = $"cid_{Guid.NewGuid():N}"[..24],
            SecretHash = MachineCredential.Hash(secret),
            Name = "Client de Teste",
            Scopes = [.. scopes],
            TenantId = await DefaultTenantAsync(scope.ServiceProvider, db)
        };

        db.OAuthClients.Add(client);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (client.ClientId, secret, client.Id);
    }

    private async Task<(string Key, Guid Id)> SeedApiKeyAsync(string[] scopes, long? callLimit = null)
    {
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var key = MachineCredential.GenerateApiKey();

        var apiKey = new ApiKey
        {
            KeyPrefix = MachineCredential.PrefixOf(key),
            KeyHash = MachineCredential.Hash(key),
            Name = "Chave de Teste",
            Scopes = [.. scopes],
            CallLimit = callLimit,
            TenantId = await DefaultTenantAsync(scope.ServiceProvider, db)
        };

        db.ApiKeys.Add(apiKey);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (key, apiKey.Id);
    }

    private async Task RevokeAsync<TEntity>(Guid id)
        where TEntity : class
    {
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Set<TEntity>()
            .IgnoreQueryFilters()
            .Where(e => EF.Property<Guid>(e, "Id") == id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => EF.Property<bool>(e, "Enabled"), false),
                TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// O tenant em que a credencial nasce. Em single-tenant (o modo padrão do template) é o
    /// <c>DefaultTenantId</c> — e é ele que o query filter vai exigir.
    /// </summary>
    private static async Task<Guid> DefaultTenantAsync(IServiceProvider services, AppDbContext db)
    {
        var tenancy = services.GetRequiredService<IOptions<TenancyOptions>>().Value;

        var exists = await db.Tenants
            .IgnoreQueryFilters()
            .AnyAsync(t => t.Id == tenancy.DefaultTenantId, TestContext.Current.CancellationToken);

        if (!exists)
        {
            db.Tenants.Add(new Tenant
            {
                Id = tenancy.DefaultTenantId,
                Name = "Tenant Padrão",
                Slug = "default"
            });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        return tenancy.DefaultTenantId;
    }
}
