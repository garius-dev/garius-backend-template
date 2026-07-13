using System.Reflection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Garius.Api.Features.Auth;
using Garius.Api.Features.Machine;
using Garius.Api.Features.Permissions;
using Garius.Api.Infrastructure.Authorization;
using Garius.Api.Infrastructure.Database;
using Garius.Api.Features.Admin;
using Garius.Api.Infrastructure.Documentation;
using Garius.Api.Infrastructure.Errors;
using Garius.Api.Infrastructure.Health;
using Garius.Api.Infrastructure.Idempotency;
using Garius.Api.Infrastructure.Jobs;
using Garius.Api.Infrastructure.Logging;
using Garius.Api.Infrastructure.Networking;
using Garius.Api.Infrastructure.RateLimiting;
using Garius.Api.Infrastructure.Security;
using Garius.Core.Results;
using Garius.Core.Security;
using Garius.Infrastructure.Caching;
using Garius.Infrastructure.Database;
using Garius.Infrastructure.Idempotency;
using Garius.Infrastructure.Messaging;
using Garius.Infrastructure.RateLimiting;
using Garius.Infrastructure.Secrets;
using Garius.Infrastructure.Security;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuração
// ---------------------------------------------------------------------------

// Cascata: Google Secret Manager > variável de ambiente > appsettings.{Env} > appsettings.
// (No .NET o último provider registrado vence; o Secret Manager entra por último.)
builder.Configuration.AddSecretSources(builder.Environment);

// MODO BOOTSTRAP. O mesmo binário, dois modos:
//   MIGRATE_ONLY=true  -> cria banco/roles/grants, aplica migrations, morre (exit 0)
//   sem a flag         -> a API sobe normalmente, e nem toca no bootstrap
//
// O container de migrations roda primeiro e é ÚNICO — por isso não há concorrência de
// CREATE DATABASE nem de migrations entre réplicas.
var migrateOnly = builder.Configuration.IsMigrateOnly();

builder.ConfigureSerilog();

// Criptografia de campo (LGPD): AES-256-GCM + índice cego HMAC.
// Falha no boot se as chaves não estiverem configuradas — uma app que sobe sem chave
// gravaria PII em claro ou quebraria no primeiro insert.
builder.Services.AddHttpContextAccessor();

// QUEM está fazendo a operação (a auditoria da criptografia precisa disto).
//
// São DUAS implementações, uma por modo — e não é preciosismo:
//
// O HttpCurrentUser depende do IPermissionResolver, que só é registrado lá embaixo
// (AddPermissionAuthorization), DEPOIS do `return` do migrateOnly. Registrá-lo no bootstrap
// fazia o builder.Build() estourar na validação do container — e o MIGRATE_ONLY NÃO RODAVA.
// Nenhuma app derivada conseguia criar o próprio banco.
//
// No bootstrap não há HTTP, não há requisição e não há usuário: o SystemCurrentUser diz
// exatamente isso, e nega toda permissão (falha fechada). Ver SystemCurrentUser.
if (migrateOnly)
{
    builder.Services.AddScoped<ICurrentUser, SystemCurrentUser>();
}
else
{
    builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
}

builder.Services.AddFieldEncryption(builder.Configuration);

var naming = builder.Services.AddPersistence(
    builder.Configuration,
    Assembly.GetExecutingAssembly(),
    migrateOnly);

if (migrateOnly)
{
    return await MigrateRunner.RunAsync(builder);
}

// ---------------------------------------------------------------------------
// Serviços (só no modo normal)
// ---------------------------------------------------------------------------

builder.Services.Configure<SecurityOptions>(
    builder.Configuration.GetSection(SecurityOptions.SectionName));

builder.ConfigureKestrelLimits();

builder.Services.AddConfiguredForwardedHeaders(builder.Configuration, builder.Environment);
builder.Services.AddConfiguredCors(builder.Configuration, builder.Environment);
builder.Services.AddConfiguredHealthChecks(builder.Configuration, naming);

// Redis: dependência OBRIGATÓRIA (refresh tokens + DataProtection, que cifra o cookie).
// Também registra o DataProtection com o keyring NO REDIS — sem isso, duas réplicas não
// conseguem ler o cookie uma da outra.
builder.Services.AddRedis(builder.Configuration, naming.AppUsername);

// Autenticação e autorização por PERMISSÃO — não por papel.
//
// Três formas de se autenticar, um só modelo de autorização:
//   cookie HttpOnly     -> pessoa, no navegador
//   Authorization: Bearer -> máquina (OAuth2 client credentials)
//   X-Api-Key           -> terceiro (chave de longa duração, com quota)
//
// Os três chegam à autorização como um principal com claims de `permission`. Um endpoint
// declara .RequirePermission(...) e serve os três — sem um segundo vocabulário de escopos.
builder.Services.AddCookieAuthentication(builder.Environment, builder.Configuration);
builder.Services.AddPermissionAuthorization();
builder.Services.AddCsrfProtection(builder.Environment);

builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<MachineAuthService>();

// Rate limit por IP REAL, com contadores NO REDIS.
//
// No Redis, e não em memória (que é o que o RateLimiter nativo do .NET faz), porque com N
// réplicas o limite em memória vira N × o limite configurado — em silêncio, e mudando toda vez
// que a aplicação escala.
builder.Services.Configure<RateLimitOptions>(
    builder.Configuration.GetSection(RateLimitOptions.SectionName));
builder.Services.AddSingleton<RedisRateLimiter>();

// Idempotência: torna uma requisição repetível sem duplicar efeito, quando o cliente manda
// um Idempotency-Key. É OPT-IN — ver IdempotencyMiddleware.
builder.Services.AddSingleton<RedisIdempotencyStore>();

// Outbox: o evento é gravado na MESMA transação do dado. Uma app derivada registra os
// handlers com AddEventHandler<TEvento, THandler>().
builder.Services.AddOutbox();

// Jobs de background. O banco (hangfire_{slug}) já é criado pelo bootstrap.
builder.Services.AddBackgroundJobs(naming);

// Toda exceção que escapa vira ProblemDetails. Nada de interno vaza para o cliente.
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// OpenAPI + Scalar. O transformer declara os TRÊS esquemas de auth (cookie, Bearer,
// X-Api-Key) — sem ele, nenhum endpoint protegido é testável pela própria página.
builder.Services.AddApiDocumentation(builder.Configuration);

var app = builder.Build();

// ---------------------------------------------------------------------------
// Pipeline — a ORDEM importa e não deve ser reorganizada sem entender o efeito.
// ---------------------------------------------------------------------------

// 1. Resolve HTTPS e IP a partir dos headers do proxy. Precisa vir ANTES de tudo que
//    depende de IP ou de esquema (logs, rate limit, cookies Secure).
app.UseForwardedHeaders();

// 2. IP real (CF-Connecting-IP, validado contra os ranges da Cloudflare).
//    Depois do ForwardedHeaders, antes de qualquer coisa que consuma o IP.
app.UseMiddleware<RealIpMiddleware>();

// 3. Converte exceções em ProblemDetails. Cedo, para cobrir todo o resto do pipeline.
app.UseExceptionHandler();

// 4. Headers de segurança em toda resposta, inclusive nas de erro.
app.UseMiddleware<SecurityHeadersMiddleware>();

// 5. Log de acesso: uma linha por request, com IP real e traceId.
app.UseRequestLogging();

app.UseCors(CorsSetup.PolicyName);

// 6. RATE LIMIT. Cedo, e por um motivo: ele é a defesa contra VOLUME, e só serve se for
//    BARATO. Colocá-lo depois da autenticação faria cada requisição de um ataque de brute
//    force pagar um PBKDF2 (~100ms de CPU) ANTES de ser recusada — e o "rate limit" viraria
//    o próprio vetor de DoS. Aqui ele custa uma ida ao Redis e corta o request.
//
//    Depois do RealIpMiddleware, porque particiona pelo IP real (o RemoteIpAddress cru é o
//    do proxy — todos os usuários teriam o mesmo IP e o limite global seria compartilhado
//    por todos eles).
app.UseMiddleware<RateLimitMiddleware>();

// 7. Faz 401/403 saírem em ProblemDetails, como todo erro da API. Precisa envolver o
//    UseAuthorization (age na VOLTA), que responderia só o status, sem corpo.
app.UseMiddleware<AuthProblemDetailsMiddleware>();

// 8. Autenticação e autorização. Depois do CORS (o preflight não carrega credencial) e
//    antes dos endpoints.
app.UseAuthentication();

// 9. CSRF: depois da autenticação (precisa saber se veio cookie ou Bearer), antes da
//    autorização. O cookie HttpOnly protege contra roubo por XSS, mas o navegador o envia
//    sozinho — inclusive numa requisição disparada por um site malicioso.
app.UseCsrfProtection();

app.UseAuthorization();

// 10. IDEMPOTÊNCIA. Depois da autorização, de propósito.
//
//     Uma requisição que vai ser recusada com 401/403 NÃO PODE reservar a chave de
//     idempotência: um anônimo qualquer "queimaria" a chave que o cliente legítimo vai usar
//     em seguida, e o request dele voltaria como um 403 replayado — um DoS trivial contra uma
//     operação específica, disparado por quem nem está autenticado.
app.UseIdempotency();

// Dashboard do Hangfire (protegido pela permissão jobs.read) e o job que drena o outbox.
//
// ⚠️ O dashboard PERMITE DISPARAR E APAGAR JOBS. Exposto sem autenticação — que é o padrão do
// Hangfire — vira execução remota de código. Ver HangfireSetup.
app.UseBackgroundJobs();

app.MapConfiguredHealthChecks();
app.MapAuthEndpoints();
app.MapMachineEndpoints();
app.MapPermissionEndpoints();

// Login das páginas administrativas (/jobs e /scalar), que são HTML abertas no NAVEGADOR.
// Reusa o AuthService — mesmo lockout, mesmo rate limit, mesmo cookie. Ver AdminEndpoints.
app.MapAdminEndpoints();

// Documentação (Scalar). Protegida por docs.read INCLUSIVE EM PRODUÇÃO: a documentação é o
// mapa completo da API, e servi-la a anônimos é entregar reconhecimento pronto.
app.MapApiDocumentation();

// AllowAnonymous é obrigatório: a FallbackPolicy exige autenticação em todo endpoint que
// não declare o contrário. É a falha FECHADA — esquecer de proteger um endpoint novo o
// deixa fechado, não aberto ao mundo.
app.MapGet("/", (HttpContext http) =>
        Result<object>.Success(new { service = builder.Environment.ApplicationName, status = "ok" })
            .ToHttpResult(http))
   .AllowAnonymous()
   .WithSummary("Ping. Confirma que a API está no ar.");

// Endpoints que exercitam o contrato de resposta nos testes de integração.
// NUNCA são mapeados em produção.
if (!app.Environment.IsProduction())
{
    app.MapTestEndpoints();
}

// ONDE a aplicação está escutando.
//
// Parece supérfluo — o ASP.NET Core já loga "Now listening on: ...". Só que esse log sai sob
// Microsoft.Hosting.Lifetime, e o filtro do Serilog põe "Microsoft" em Warning (é a regra dos
// logs limpos, e ela é deliberada). Resultado: o boot termina com "Iniciando {App}" e MAIS
// NADA — a aplicação está no ar, escutando, e PARECE TRAVADA. Já custou uma sessão inteira de
// diagnóstico, e custaria de novo a cada pessoa que derivasse este template.
//
// Sob o namespace da própria aplicação, este log NÃO é silenciado.
//
// No ApplicationStarted, e não antes do Run(): só aqui o Kestrel já resolveu os endereços
// REAIS. Antes disso eles não existem — e num container eles não são os do launchSettings.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Services
        .GetService<IServer>()?
        .Features.Get<IServerAddressesFeature>()?
        .Addresses;

    if (addresses is { Count: > 0 })
    {
        Log.Information("Escutando em {Addresses}", string.Join(", ", addresses));
    }
});

try
{
    Log.Information("Iniciando {Application} em {Environment}",
        builder.Environment.ApplicationName, builder.Environment.EnvironmentName);

    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "A aplicação encerrou de forma inesperada.");
    return 1;
}
finally
{
    // Sem isto, os logs do erro que derrubou a app podem nunca chegar ao Loki —
    // exatamente o log de que se precisa quando um deploy falha.
    await Log.CloseAndFlushAsync();
}

/// <summary>
/// Necessário para que os testes de integração possam usar WebApplicationFactory&lt;Program&gt;.
/// </summary>
public partial class Program;
