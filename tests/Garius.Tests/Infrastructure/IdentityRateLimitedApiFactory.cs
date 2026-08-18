using System.Net;
using System.Net.Http.Headers;
using Garius.Core.Authorization;
using Garius.Core.Identity;
using Garius.Core.Security;
using Garius.Core.Tenancy;
using Garius.Infrastructure.Database;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Garius.Tests.Infrastructure;

/// <summary>
/// A API com o rate limit <b>por identidade</b> ligado e com teto baixo.
///
/// <para>
/// <b>Fábrica separada da <see cref="RateLimitedApiFactory"/></b>, e não é duplicação por
/// preguiça: as duas camadas exigem configurações opostas para serem exercitadas. Ali o limite
/// por IP é baixo (para o teste de brute force estourá-lo em duas tentativas) e o de identidade
/// fica desligado; aqui é o contrário — o de IP fica folgado, para que quando a requisição for
/// barrada não haja dúvida sobre <b>qual</b> das duas camadas mordeu.
/// </para>
///
/// <para>
/// Um teste que não sabe qual defesa disparou prova a coisa errada.
/// </para>
/// </summary>
public sealed class IdentityRateLimitedApiFactory : ApiFactory
{
    private const string Password = "SenhaForte123!@#";

    protected override void ConfigureRateLimit()
    {
        SetVariable("RateLimit__Enabled", "true");

        // FOLGADO de propósito: se a camada de IP mordesse antes, o teste passaria pelo motivo
        // errado — e continuaria passando mesmo se o middleware de identidade fosse removido.
        SetVariable("RateLimit__Global__PermitLimit", "100");
        SetVariable("RateLimit__Global__WindowSeconds", "60");

        // O que se quer exercitar. Três é o suficiente para estourar rápido sem depender de
        // tempo (um teste que precisa esperar 60 segundos é um teste que ninguém roda).
        SetVariable("RateLimit__Identity__Enabled", "true");
        SetVariable("RateLimit__Identity__PermitLimit", "3");
        SetVariable("RateLimit__Identity__WindowSeconds", "60");
    }

    /// <summary>
    /// Zera os contadores no Redis. Chamado no início de cada teste: eles rodam em série, mas
    /// dentro da MESMA janela de 60 segundos — sem isto, o segundo teste herdaria a cota já
    /// gasta pelo primeiro e receberia 429 na primeira requisição.
    /// </summary>
    public async Task ResetRateLimitsAsync()
    {
        var redis = Services.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>();
        var options = Services.GetRequiredService<Garius.Infrastructure.Caching.RedisOptions>();

        var server = redis.GetServer(redis.GetEndPoints().First());

        foreach (var key in server.Keys(pattern: $"{options.InstanceName}:rl:*"))
        {
            await redis.GetDatabase().KeyDeleteAsync(key);
        }
    }

    /// <summary>
    /// Um cliente <b>autenticado</b>, com um usuário novo a cada chamada.
    ///
    /// <para>
    /// Usuário novo por chamada é o que torna os testes independentes: a cota é <b>por
    /// identidade</b>, então reaproveitar o mesmo usuário faria um teste herdar a cota gasta
    /// pelo anterior — e a falha dependeria da ordem de execução, que o xUnit não garante.
    /// </para>
    /// </summary>
    public async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var email = await SeedUserAsync();

        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });

        var response = await client.PostAsync(
            "/admin/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = email,
                ["password"] = Password,
                ["returnUrl"] = "/"
            }),
            TestContext.Current.CancellationToken);

        if (response.StatusCode != HttpStatusCode.Redirect)
        {
            throw new InvalidOperationException(
                $"O login de teste falhou com {(int)response.StatusCode}. " +
                "Sem sessão, o middleware por identidade nem age — e o teste provaria nada.");
        }

        return client;
    }

    private async Task<string> SeedUserAsync()
    {
        using var scope = Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();

        var email = $"rl-{Guid.NewGuid():N}@empresa.com";

        var user = new ApplicationUser
        {
            EmailPii = Pii.Create(PiiScope.Email, email),
            Cpf = Pii.Empty(PiiScope.Cpf),
            DisplayName = "Cliente"
        };

        user.UserName = user.Id.ToString();

        var created = await users.CreateAsync(user, Password);

        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                "Não foi possível criar o usuário de teste: " +
                string.Join(", ", created.Errors.Select(e => e.Description)));
        }

        // UM tenant só: com vários, o login emitiria um cookie parcial (falta escolher o
        // tenant) e o usuário não chegaria autenticado ao endpoint. Ver AdminEndpoints.
        var tenant = new Tenant
        {
            Name = "Empresa",
            Slug = $"emp-{Guid.NewGuid():N}"[..20]
        };

        db.Tenants.Add(tenant);
        db.UserTenants.Add(new ApplicationUserTenant
        {
            UserId = user.Id,
            TenantId = tenant.Id,
            IsDefault = true
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await resolver.InvalidateAllAsync(TestContext.Current.CancellationToken);

        return email;
    }
}

/// <summary>
/// As classes que exercitam o rate limit <b>por identidade</b>.
///
/// <para>
/// Collection própria pelo mesmo motivo de todas as outras: a fábrica configura a aplicação por
/// variável de ambiente, que é estado global do processo. Duas fábricas em paralelo se
/// atropelam — ver <see cref="ApiCollection"/>.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IdentityRateLimitCollection : ICollectionFixture<IdentityRateLimitedApiFactory>
{
    public const string Name = "IdentityRateLimit";
}
