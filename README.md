# Garius Backend Template

Template backend .NET 10 para APIs de produção. Foco em segurança e integração limpa com o frontend, sem overengineering.

**Stack:** .NET 10 · PostgreSQL · Redis · Hangfire · Serilog → Loki/Grafana · Docker + Traefik + Cloudflare · Google Secret Manager · Scalar

> **Se você é uma IA lendo este repositório:** leia este arquivo inteiro antes de escrever código. As seções [Regras invioláveis](#regras-invioláveis) e [Como escrever código aqui](#como-escrever-código-aqui) não são sugestões — violar qualquer uma delas quebra segurança ou o contrato com o frontend. As decisões têm o *porquê* junto; se algo parecer redundante, provavelmente existe porque a alternativa óbvia falhou em produção.

---

## Índice

- [Estado atual](#estado-atual)
- [Derivando uma nova aplicação](#derivando-uma-nova-aplicação) ← **comece aqui**
- [Arquitetura](#arquitetura)
- [Regras invioláveis](#regras-invioláveis)
- [Como escrever código aqui](#como-escrever-código-aqui)
- [Configuração e segredos](#configuração-e-segredos)
- [Banco de dados](#banco-de-dados)
- [Autenticação (para o frontend)](#autenticação-para-o-frontend)
- [Autenticação de máquina (M2M e terceiros)](#autenticação-de-máquina-m2m-e-terceiros)
- [Rate limit, idempotência e jobs](#rate-limit-idempotência-e-jobs)
- [Documentação e painel de jobs](#documentação-e-painel-de-jobs)
- [Eventos de domínio (outbox)](#eventos-de-domínio-outbox)
- [Dados pessoais (LGPD)](#dados-pessoais-lgpd)
- [Deploy](#deploy)
- [Desenvolvimento local](#desenvolvimento-local)

---

## Estado atual

**Tudo o que está aqui está pronto e testado** — 166 testes contra Postgres e Redis reais (Testcontainers), build estrito (warnings = erro) e auditoria de CVE no build.

| Área | Estado |
|---|---|
| Estrutura, build estrito, auditoria de CVE | ✅ |
| Envelope de resposta + ProblemDetails | ✅ |
| Configuração em cascata (GCP → env → appsettings) | ✅ |
| Logs limpos (Serilog → Loki), IP real via Cloudflare | ✅ |
| Health checks | ✅ |
| EF Core, soft delete, tenancy, auto-bootstrap do banco | ✅ |
| Criptografia LGPD (AES-GCM + blind index + auditoria) | ✅ |
| Identity com e-mail criptografado, navegações explícitas | ✅ |
| Permissões granulares (estilo GCP IAM) | ✅ |
| Login (1 e N tenants), refresh rotativo, CSRF, DataProtection no Redis | ✅ |
| M2M (OAuth2 client credentials) e API keys de terceiros | ✅ |
| Cache de permissões no Redis (invalidação entre réplicas) | ✅ |
| Rate limit por IP (contadores no Redis) | ✅ |
| Idempotência (`Idempotency-Key`) | ✅ |
| Hangfire (dashboard protegido por permissão) | ✅ |
| Outbox transacional | ✅ |
| Documentação (Scalar) e painel de jobs, protegidos por permissão | ✅ |

**O template está completo.**

Não documente aqui o que ainda não existe. Documentação que mente é pior que documentação ausente.

---

## Derivando uma nova aplicação

### 1. Renomeie a aplicação

**Obrigatório.** Em `src/Garius.Api/appsettings.json`:

```json
"Database": {
  "ApplicationName": "MinhaApp.Backend"
}
```

Daí saem os nomes no Postgres: `db_minhaapp_backend`, `hangfire_minhaapp_backend`, `minhaapp_backend_user`.

> **Se você esquecer disto, duas aplicações colidem no mesmo banco e no mesmo usuário.** O nome não vem do assembly de propósito: o assembly de entrada é `Garius.Api` em *toda* app derivada deste template.

### 2. Crie o secret no Google Secret Manager

Um secret por aplicação, com um **JSON flat** (chaves no formato `Section:Key`):

```json
{
  "Database:RootPassword": "<a mesma em todas as apps — é a chave mestra que cria bancos>",
  "Database:AppPassword": "<única desta aplicação>",
  "Redis:Password": "<a senha do seu Redis>",

  "Encryption:Keys:1": "<32 bytes em base64>",
  "Encryption:ActiveKeyVersion": "1",
  "Encryption:BlindIndexKey": "<32 bytes em base64>",

  "Jwt:SigningKey": "<32 bytes em base64>"
}
```

Gere as chaves com:

```bash
openssl rand -base64 32     # uma para Encryption:Keys:1
openssl rand -base64 32     # outra para Encryption:BlindIndexKey
openssl rand -base64 32     # outra para Jwt:SigningKey
```

> ⚠️ **Guarde estas chaves.** Sem `Encryption:Keys:1`, todo dado pessoal cifrado com ela vira lixo irrecuperável. Sem `BlindIndexKey`, ninguém mais faz login (a busca por e-mail para de funcionar). Elas nunca devem ir para o repositório.

> ⚠️ **`Jwt:SigningKey` é obrigatória — a aplicação não sobe sem ela**, mesmo que você nunca vá usar M2M. É deliberado: a alternativa (gerar uma chave aleatória em memória) faria os tokens morrerem a cada restart e cada réplica rejeitar os tokens das outras — falhas intermitentes de autenticação em produção, sem causa aparente.

E o issuer/audience no `appsettings` (não são segredo):

```json
"Jwt": {
  "Issuer":   "https://api.suaapp.com",
  "Audience": "https://api.suaapp.com"
}
```

> ⚠️ **O Redis é dependência obrigatória** — a autenticação (refresh tokens) e o DataProtection (que cifra o cookie) dependem dele. Se ele não estiver acessível, **a aplicação não sobe**, e isso é deliberado: subir sem Redis faria toda requisição de login falhar com 500.

Aponte para ele em `appsettings.Development.json` e `appsettings.Production.json`:

```json
"GcpSecrets": {
  "Enabled": true,
  "ProjectId": "seu-projeto",
  "SecretName": "minhaapp-backend-secrets"
}
```

> A service account precisa apenas de **leitura** (`secretmanager.versions.access`). Ela não cria secrets — isso é feito por você, no console ou no `gcloud`.

### 3. Configure produção

Em `appsettings.Production.json`:

```json
{
  "Security": {
    "TrustedProxies": [ "172.18.0.0/16" ]
  },
  "Cors": {
    "AllowedOrigins": [ "https://app.seudominio.com" ]
  },
  "Serilog": {
    "Loki": { "Enabled": true, "Url": "http://loki:3100" }
  }
}
```

- **`TrustedProxies` é obrigatório em produção** — a rede Docker do Traefik. Sem ele **a aplicação não sobe** (falha explícita e proposital, ver [Regras invioláveis](#regras-invioláveis)).
- **`AllowedOrigins` vazio nega tudo.** Se houver frontend, declare a origem.

### 4. Decida a tenancy

```json
"Tenancy": { "Mode": "SingleTenant" }   // ou "MultiTenant"
```

**Alternar não muda o schema** — a coluna `TenantId` e o query filter existem nos dois modos. Muda apenas qual `ITenantResolver` é registrado. Você pode migrar de single para SaaS depois sem migration destrutiva.

### 5. Rode

```bash
# cria banco, roles, grants e aplica migrations
MIGRATE_ONLY=true dotnet run --project src/Garius.Api

# sobe a API
dotnet run --project src/Garius.Api
```

### 6. Renomeie os projetos (opcional)

`Garius.Api` / `Garius.Core` / `Garius.Infrastructure` podem virar `MinhaApp.*`. Se fizer isso, atualize os `InternalsVisibleTo` nos `.csproj` e o `MigrationsAssembly` em `PersistenceExtensions.cs`.

---

## Arquitetura

**Modular monolith com vertical slices.** Não é Clean Architecture de 5 camadas — sem MediatR, sem AutoMapper, sem repositório genérico, sem CQRS. O alvo é um dev solo conseguir manter isso por anos.

```
src/
  Garius.Core/            Domínio, regras, contratos. NÃO conhece EF, Redis, HTTP.
  Garius.Infrastructure/  Implementa os contratos: EF/Postgres, Redis, Secret Manager, cripto.
  Garius.Api/             Host: Program.cs, Features/, Infrastructure/ (middlewares, DI).
tests/
  Garius.Tests/           Integração com Testcontainers (Postgres + Redis REAIS).
```

**Organize por feature, não por camada.** Cada pasta em `Features/` contém os endpoints, o serviço e os DTOs daquela feature, juntos:

```
Features/
  Users/
    UserEndpoints.cs
    UserService.cs
    UserDtos.cs
  Invoices/
    ...
```

Abrir uma pasta = ver a feature inteira. Não crie `Services/` ou `Repositories/` na raiz — é o anti-padrão que esta arquitetura evita.

---

## Regras invioláveis

Cada uma existe porque a alternativa falhou de verdade.

### 1. Toda resposta segue o contrato

Sucesso → envelope. Erro → ProblemDetails (RFC 9457). **Sem exceção.**

```jsonc
// 200 OK
{ "success": true, "data": { ... }, "traceId": "0af7651916cd43dd" }

// 4xx / 5xx  (RFC 9457)
{
  "type": "...", "title": "...", "status": 404,
  "detail": "Usuário não encontrado.",
  "traceId": "0af7651916cd43dd",
  "code": "user.not_found",
  "errors": { "email": ["E-mail já cadastrado."] }   // só em validação
}
```

O **`code`** é contrato com o frontend: ele decide comportamento por ele, sem parsear texto. Mudar um `code` é breaking change; mudar a `message` não é.
O **`traceId`** é o mesmo na resposta e no log — cole no Grafana e ache o request exato.

### 2. Erro de negócio é `Result`, não exceção

```csharp
// ✅
return Error.NotFound("user.not_found", "Usuário não encontrado.");

// ❌ — exceção é para BUG e falha de infra, nunca para fluxo esperado
throw new NotFoundException("Usuário não encontrado.");
```

É isso que mantém o log limpo: só o que é genuinamente inesperado vira `LogError` com stack trace.

### 3. Nada interno vaza para o cliente

O `GlobalExceptionHandler` converte qualquer exceção em `500` genérico + `traceId`. **Nem em Development** o detalhe vai para a resposta — ele vai para o log. Sem isso, o texto de uma `NpgsqlException` entrega host, usuário e schema do banco.

### 4. IP real sempre por `GetClientIp()`

```csharp
// ✅
var ip = context.GetClientIp();

// ❌ — atrás do Traefik isto é o IP do proxy, não o do cliente
var ip = context.Connection.RemoteIpAddress;
```

`CF-Connecting-IP` **só é acreditado se o request chegou de um range oficial da Cloudflare**. Sem essa validação, qualquer um forja o header e escapa de rate limit, lockout e auditoria — de uma vez só.

### 5. Segredo nunca no appsettings

`appsettings*.json` guarda host, porta, nome de usuário. **Senha, chave e token vêm do Secret Manager** (ou env var). O `SensitiveDataMasker` também redige por nome de propriedade (`password`, `token`, `cpf`, `email`) em qualquer log.

### 6. Índice único é sempre parcial

```csharp
builder.HasIndex(x => x.Email)
       .IsUnique()
       .HasFilter("\"Enabled\" = true");   // ← obrigatório
```

Sem o filtro, um registro soft-deleted mantém o e-mail **ocupado para sempre** — quem excluiu a conta nunca mais se recadastra. Em multi-tenant, o índice é composto: `(TenantId, Email)`.

### 7. Dado pessoal é `Pii`, nunca `string`

```csharp
public Pii Email { get; set; }        // ✅ cifrado no banco, mascarado em log e JSON
public byte[] EmailIndex { get; set; } // ✅ índice cego — é por aqui que se busca

public string Email { get; set; }     // ❌ vai em claro para o banco, o log e a resposta
```

`Pii` é um **tipo**, não uma string, e isso é o ponto: `ToString()` e a serialização JSON devolvem a versão **mascarada**. Uma interpolação descuidada (`$"usuário {user.Email}"`) ou um `Pii` esquecido num DTO de resposta **não vazam o dado**.

Para ler em claro, use `IPiiReader` — ele autoriza, revela **e audita**:

```csharp
var email = await piiReader.RevealAsync(
    user.Email, PiiScope.Email, nameof(User), user.Id,
    reason: "Exibição no perfil do titular", ct);
```

Chamar `Pii.Reveal()` direto pula a autorização e a auditoria. Faça isso **apenas** onde não há usuário na jogada (o próprio login, um job com auditoria própria) — e diga no código por quê.

### 8. Autorize por PERMISSÃO, nunca por papel

```csharp
group.MapPost("/{id}/approve", ...).RequirePermission(Permissions.Invoices.Approve);   // ✅
group.MapPost("/{id}/approve", ...).RequireAuthorization(new AuthorizeAttribute { Roles = "Financeiro" });  // ❌
```

Papel é um **conjunto nomeado de permissões** — configuração, não código. No dia em que o "Gerente" também precisar aprovar fatura, isso se resolve no banco. É o que separa um sistema de autorização que envelhece bem daquele que degenera em `if (role == "Admin" || role == "Gerente" || ...)`.

Declare a permissão em `Permissions` (o catálogo é descoberto por reflexão; não há segunda lista para manter):

```csharp
public static class Invoices
{
    public static readonly Permission Approve = new("invoices", "approve", "Aprovar faturas");
}
```

**Endpoint sem proteção fica FECHADO.** A `FallbackPolicy` exige autenticação em tudo que não declare `.AllowAnonymous()` ou `.RequirePermission(...)`. Esquecer de proteger um endpoint novo o torna inacessível — não aberto ao mundo.

Curinga só à **direita**: `*` (superadministrador), `invoices.*`. Não existe `*.delete` — "apagar qualquer coisa" é uma permissão que ninguém consegue auditar.

### 9. Falha de configuração falha FECHADA

Onde a aplicação **não sobe** de propósito:

| Situação | Comportamento |
|---|---|
| `Security:TrustedProxies` vazio em produção | **boot falha** |
| Secret Manager indisponível em produção | **boot falha** |
| `Cors:AllowedOrigins` vazio | nega toda origem |
| `Health:ApiKey` vazio em produção | `/health/detail` não é sequer mapeado |

Subir com metade da configuração é pior do que não subir: a app fica de pé, aceita tráfego e falha de formas obscuras.

### 10. A ordem do pipeline não se mexe

```
 1. UseForwardedHeaders()        resolve HTTPS e IP a partir do proxy
 2. RealIpMiddleware             IP real (CF-Connecting-IP validado)
 3. UseExceptionHandler()        tudo vira ProblemDetails
 4. SecurityHeadersMiddleware    headers em TODA resposta, inclusive de erro
 5. UseRequestLogging()          uma linha por request
 6. UseCors()
 7. RateLimitMiddleware          429 por IP  ← ANTES da autenticação
 8. AuthProblemDetailsMiddleware faz 401/403 saírem em ProblemDetails
 9. UseAuthentication()
10. UseCsrfProtection()
11. UseAuthorization()
12. UseIdempotency()             ← DEPOIS da autorização
```

Qualquer coisa que consuma IP tem de vir **depois** de (1) e (2).

**O rate limit vem antes da autenticação** porque ele é a defesa contra **volume**, e só serve se for **barato**. Depois da autenticação, cada requisição de um brute force pagaria um PBKDF2 (~100 ms de CPU) *antes* de ser recusada — e o "rate limit" viraria o próprio vetor de DoS.

**A idempotência vem depois da autorização** porque uma requisição que vai levar 401/403 **não pode reservar a chave**: um anônimo qualquer "queimaria" a chave que o cliente legítimo vai usar em seguida, e o request dele voltaria como um 403 replayado. É um DoS trivial contra uma operação específica, disparado por quem nem está autenticado.

---

## Como escrever código aqui

### Um endpoint completo

Crie `src/Garius.Api/Features/Products/`:

**`ProductDtos.cs`** — nunca exponha a entidade; sempre um DTO.

```csharp
namespace Garius.Api.Features.Products;

public sealed record ProductDto(Guid Id, string Name, decimal Price);

public sealed record CreateProductRequest(string Name, decimal Price);
```

**`ProductService.cs`** — regra de negócio. Devolve `Result`, nunca lança para fluxo esperado.

```csharp
using Garius.Core.Results;
using Garius.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace Garius.Api.Features.Products;

public sealed class ProductService(AppDbContext db)
{
    public async Task<Result<ProductDto>> GetAsync(Guid id, CancellationToken ct)
    {
        // O global query filter já esconde os soft-deleted e os de outro tenant.
        var product = await db.Products
            .Where(p => p.Id == id)
            .Select(p => new ProductDto(p.Id, p.Name, p.Price))
            .FirstOrDefaultAsync(ct);

        return product is null
            ? Error.NotFound("product.not_found", "Produto não encontrado.")
            : product;   // conversão implícita para Result<ProductDto>
    }

    public async Task<Result<ProductDto>> CreateAsync(CreateProductRequest request, CancellationToken ct)
    {
        if (await db.Products.AnyAsync(p => p.Name == request.Name, ct))
        {
            return Error.Conflict("product.name_taken", "Já existe um produto com esse nome.");
        }

        var product = new Product { Name = request.Name, Price = request.Price };

        // Id (UUID v7), TenantId, CreatedAt e UpdatedAt são preenchidos pelo interceptor.
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);

        return new ProductDto(product.Id, product.Name, product.Price);
    }
}
```

**`ProductEndpoints.cs`** — Minimal API. `ToHttpResult` é a única forma de responder.

```csharp
using Garius.Api.Infrastructure.Errors;

namespace Garius.Api.Features.Products;

public static class ProductEndpoints
{
    public static void MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/products").WithTags("Products");

        group.MapGet("/{id:guid}", async (
            Guid id,
            ProductService service,
            HttpContext http,
            CancellationToken ct) =>
                (await service.GetAsync(id, ct)).ToHttpResult(http))
            .RequirePermission(Permissions.Products.Read)
            .WithSummary("Busca um produto pelo id.");

        group.MapPost("/", async (
            CreateProductRequest request,
            ProductService service,
            HttpContext http,
            CancellationToken ct) =>
                (await service.CreateAsync(request, ct))
                    .ToCreatedResult(http, "/products"))
            .RequirePermission(Permissions.Products.Create)
            .WithSummary("Cria um produto.");
    }
}
```

Declare as permissões em `Garius.Core/Authorization/Permissions.cs` (o catálogo é descoberto por reflexão — basta declarar):

```csharp
public static class Products
{
    public static readonly Permission Read   = new("products", "read",   "Ver produtos");
    public static readonly Permission Create = new("products", "create", "Criar produtos");
}
```

Registre no `Program.cs`:

```csharp
builder.Services.AddScoped<ProductService>();
// ...
app.MapProductEndpoints();
```

### Uma entidade

```csharp
using Garius.Core.Entities;
using Garius.Core.Tenancy;

namespace Garius.Core.Entities;

// ITenantEntity: o query filter de tenant passa a valer automaticamente.
// Omita-a apenas se a entidade for global (rara).
public sealed class Product : BaseEntity, ITenantEntity
{
    public Guid TenantId { get; set; }

    public required string Name { get; set; }
    public required decimal Price { get; set; }
}
```

`BaseEntity` já dá `Id` (UUID v7), `Enabled` (soft delete), `CreatedAt` e `UpdatedAt` — **todos preenchidos automaticamente**. Nunca defina timestamp à mão.

Configure em `src/Garius.Infrastructure/Database/Configurations/ProductConfiguration.cs`:

```csharp
internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products");
        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Price).HasPrecision(18, 2);

        // Único por tenant, e só entre os ativos. Ver Regra 6.
        builder.HasIndex(p => new { p.TenantId, p.Name })
               .IsUnique()
               .HasFilter("\"Enabled\" = true");
    }
}
```

Adicione o `DbSet` em `AppDbContext` (é o que permite escrever `db.Products` no serviço):

```csharp
public DbSet<Product> Products => Set<Product>();
```

E gere a migration:

```bash
dotnet ef migrations add AddProducts \
  --project src/Garius.Infrastructure \
  --startup-project src/Garius.Infrastructure \
  --output-dir Database/Migrations
```

O `ProductConfiguration` é descoberto automaticamente (`ApplyConfigurationsFromAssembly`), e o `AppDbContext` aplica sozinho: os dois query filters (soft delete + tenant), o tipo `timestamptz` nos timestamps e os índices de `Enabled` e `TenantId`.

### Um teste

Testes de integração contra Postgres e Redis **reais** (Testcontainers). Não use mock de banco: o template depende de comportamento específico do Postgres (índice parcial, `timestamptz`, ordenação de `uuid`) — testar contra um substituto validaria o substituto.

Duas fixtures, e **escolher a errada quebra a suíte inteira**:

```csharp
// Só precisa do BANCO (repositório, entidade, query filter):
public class ProductTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>

// Precisa da API REAL (endpoint, auth, pipeline completo):
[Collection(ApiCollection.Name)]              // ⚠️ obrigatório
public class ProductEndpointTests(ApiFactory factory)
```

> ⚠️ **Nunca use `IClassFixture<ApiFactory>`.** A `ApiFactory` configura a aplicação por **variável de ambiente** (é o único jeito de vencer o `appsettings.Development.json`, que aponta para o Postgres *local*), e variável de ambiente é **estado global do processo**. Com `IClassFixture`, o xUnit roda as classes em paralelo, cada uma com seus containers — a primeira a terminar **apaga as variáveis e destrói os containers das outras**, que morrem com `"target machine actively refused it"`. Como é uma corrida, o número de falhas muda a cada execução e **some numa build incremental** — o que a torna especialmente difícil de diagnosticar.

> **Rode `dotnet clean && dotnet build && dotnet test` antes de dar algo por pronto.** Uma suíte verde num build incremental não prova nada quando há estado global em jogo.

> **Teste de segurança tem de ter dentes.** Neutralize a defesa e confirme que o teste **falha**. Se ele passa dos dois jeitos, é decoração.

### Convenções

- **Minimal APIs**, não Controllers.
- **Nunca** exponha uma entidade na resposta — sempre um DTO.
- `sealed` por padrão; `record` para DTOs.
- Namespaces *file-scoped*.
- **Warnings são erros** (`TreatWarningsAsErrors`), e pacote com CVE quebra o build (`NuGetAudit`). Não desligue: já pegou uma vulnerabilidade HIGH no primeiro build deste template.

---

## Configuração e segredos

Cascata, da **maior** para a menor precedência:

```
1. Google Secret Manager        (fonte padrão em teste e produção)
2. Variável de ambiente         (separador de seção: __  →  Database__AppPassword)
3. appsettings.{Environment}.json
4. appsettings.json
```

**A aplicação prefere o Secret Manager, mas não depende dele.** Remova o token e a configuração cai sozinha para env var ou appsettings — é o que permite entregar esta aplicação a um terceiro que não usa GCP.

Política de falha: fora de produção, GCP indisponível **degrada** para as camadas de baixo. Em produção, **falha o boot**.

---

## Banco de dados

### Nomes (derivados de `Database:ApplicationName`)

```
db_{slug}          banco de negócio
hangfire_{slug}    banco do Hangfire (banco separado, não schema)
{slug}_user        usuário de runtime — só CRUD, sem DDL
postgres (root)    superusuário. A MESMA senha em todas as apps: é a chave mestra
                   que cria bancos. Usada SÓ na janela de bootstrap.
```

### Auto-bootstrap

A aplicação cria tudo sozinha, sem script externo. Com `MIGRATE_ONLY=true` ela:

1. cria a role `{slug}_user` com a senha do Secret Manager (a senha não é gerada — ela já existe);
2. cria os dois bancos;
3. **`REVOKE ALL ... FROM PUBLIC`** — sem isto o isolamento é ilusório (ver abaixo);
4. concede ao `{slug}_user` apenas CRUD;
5. aplica as migrations;
6. cria o tenant padrão;
7. sai com código 0.

> ### A armadilha do `PUBLIC`
> O Postgres concede `CONNECT` a **`PUBLIC`** em todo banco novo — **toda role do servidor já nasce podendo conectar em qualquer banco**. Um `GRANT CONNECT` no próprio banco é redundante; o que isola de verdade é o `REVOKE`.
>
> Isto não é teoria: na primeira implementação, o usuário desta aplicação conectava nos bancos de todas as outras. Dois testes travam o comportamento hoje.

### Soft delete

`db.Products.Remove(p)` **não apaga** — o interceptor converte em `Enabled = false`. O registro some das consultas (global query filter) mas permanece no banco, que é o que a LGPD exige para a trilha de auditoria.

Para enxergar os removidos: `.IgnoreQueryFilters()`.

---

## Autenticação (para o frontend)

### O fluxo

```
POST /auth/login          { email, password }
  ├─ 1 tenant   → { authenticated: true,  user: {...} }   sessão criada
  └─ N tenants  → { authenticated: false, tenants: [...] } escolha necessária

POST /auth/select-tenant  { tenantId }   → sessão criada
GET  /auth/me                            → usuário + permissões efetivas
POST /auth/refresh                       → renova a sessão
POST /auth/logout                        → encerra
```

### CSRF — o que o front precisa fazer

O login devolve dois cookies. Um deles, **`garius.csrf-token`**, é legível pelo JavaScript. Leia-o e reenvie no header em **toda requisição que altera estado** (POST/PUT/PATCH/DELETE):

```js
const csrf = document.cookie
  .split('; ')
  .find(c => c.startsWith('garius.csrf-token='))
  ?.split('=')[1];

await fetch('/api/products', {
  method: 'POST',
  credentials: 'include',              // envia os cookies
  headers: { 'X-CSRF-Token': csrf },   // prova a mesma origem
  body: JSON.stringify(product)
});
```

Sem o header, a API responde **403**. O cookie de sessão é `HttpOnly` — o navegador o envia sozinho, inclusive numa requisição disparada por um site malicioso. O token de CSRF é a prova de que a chamada partiu de código rodando na **mesma origem**.

> Requisições autenticadas por `Authorization: Bearer` ou `X-Api-Key` (ver [Autenticação de máquina](#autenticação-de-máquina-m2m-e-terceiros)) dispensam CSRF: não há cookie ambiente que o navegador envie sozinho, então não há o que explorar.

### Permissões

Vêm na **resposta** de `/auth/me`, para o front decidir o que renderizar:

```json
{ "data": { "id": "...", "permissions": ["invoices.read", "users.*"] } }
```

**Nunca dentro do cookie.** Medido: 1000 permissões como claims produziriam um cookie de **50 KB** — 12× o limite do navegador, que o **descarta em silêncio** (o login simplesmente para de funcionar, sem nada nos logs). O cookie tem tamanho constante (~688 bytes).

Como consequência, **a revogação é imediata**: um cookie não carrega permissões velhas porque não carrega permissão nenhuma.

### Sessão

| Item | Valor |
|---|---|
| Cookie de sessão | `HttpOnly`, `Secure`, `SameSite=Lax`, prefixo `__Host-` |
| Refresh token | Rotacionado a cada uso; 7 dias |
| Detecção de roubo | Reapresentar um token já consumido **revoga a sessão inteira** |

O `SameSite=Lax` só funciona porque o front e a API compartilham o apex (`app.X` + `api.X`). Ver [Deploy](#deploy).

---

## Autenticação de máquina (M2M e terceiros)

Há **três** formas de se autenticar, e **um só** modelo de autorização:

| Credencial | Quem usa | Como |
|---|---|---|
| Cookie `HttpOnly` | uma **pessoa**, no navegador | `POST /auth/login` |
| JWT (OAuth2 *client credentials*) | um **sistema seu** | `POST /auth/token` → `Authorization: Bearer <token>` |
| Chave de API | um **terceiro** | `X-Api-Key: <chave>` |

As três chegam à autorização como um principal com claims de permissão. **Um endpoint declara a permissão e serve as três** — não existe um segundo vocabulário de escopos para máquina:

```csharp
group.MapGet("/", ...).RequirePermission(Permissions.Invoices.Read);
// vale para cookie, Bearer e X-Api-Key. O mesmo código.
```

### M2M — client credentials

```bash
# 1. o admin cria o client (exige a permissão clients.create)
POST /machine/clients
{ "name": "ERP", "scopes": ["invoices.read", "invoices.create"] }

# → devolve o client_secret UMA ÚNICA VEZ. O banco só guarda o hash.
{ "data": { "clientId": "cid_...", "clientSecret": "<guarde agora>" } }

# 2. o sistema troca o segredo por um token (RFC 6749)
POST /auth/token
{ "grant_type": "client_credentials", "client_id": "cid_...", "client_secret": "..." }

# → { "access_token": "eyJ...", "token_type": "Bearer", "expires_in": 3600 }

# 3. e o usa
GET /invoices
Authorization: Bearer eyJ...
```

> `POST /auth/token` é a **única exceção ao envelope da API**: a resposta segue a RFC 6749 (`access_token` na raiz), porque é o que toda biblioteca OAuth do mundo espera. Consistência interna não vale mais que interoperabilidade num protocolo público.

### Chaves de API

Para um parceiro que só chama dois endpoints, o fluxo OAuth de duas etapas é atrito sem ganho — a chave é uma linha de `curl`:

```bash
POST /machine/api-keys
{ "name": "Parceiro X", "scopes": ["invoices.read"], "callLimit": 10000 }

# → devolve a chave UMA ÚNICA VEZ: "gk_a1b2c3d4..."

curl -H "X-Api-Key: gk_a1b2c3d4..." https://api.suaapp.com/invoices
```

O **`callLimit` é uma quota total**, não um rate limit. É o que transforma um vazamento silencioso e ilimitado num vazamento que **trava e aparece**. Uma chave estourada para de funcionar, e alguém precisa olhar para ela.

### As duas diferenças que importam

|  | JWT (M2M) | Chave de API |
|---|---|---|
| Custo por request | **zero** — stateless, nada é consultado | vai ao banco a cada chamada |
| Revogar tem efeito | **só quando o token expira** (≤ `Jwt:LifetimeMinutes`, 1h) | **imediato** |

> ⚠️ **Revogar um client OAuth não mata os JWTs já emitidos.** É a consequência inevitável de um token stateless — que é justamente o que o torna barato. A janela é o tempo de vida do token. Se precisar de revogação verdadeiramente imediata, o caminho é uma blacklist de `jti` no Redis consultada a cada request — e aí o token deixa de ser stateless, e o custo volta.

### ⚠️ Ninguém delega um poder que não tem

Quem cria um client escolhe os escopos dele. Se pudesse escolher **qualquer** escopo, teria uma **escalada de privilégio em dois passos**: cria um client com escopo `*`, autentica-se com ele, vira superadministrador — usando uma permissão (`clients.create`) que parecia inócua.

Por isso a criação **valida os escopos contra as permissões de quem está criando**:

```
usuário com users.read + clients.create
  → criar client com escopo users.read    ✅
  → criar client com escopo users.delete  ❌ 403 client.scope_escalation
  → criar client com escopo *             ❌ 403 client.scope_escalation
```

`clients.create` é, na prática, uma permissão perigosa. É por isso que ela **não vem de carona** em "administrar usuários" — é uma permissão à parte, para ser concedida a pouca gente.

### Só pessoa, só máquina

Algumas operações não fazem sentido vindas de um sistema (trocar a própria senha, aceitar termos). A permissão sozinha não expressa "isto exige um humano":

```csharp
group.MapPost("/change-password", ...).RequireAuthorization(MachineAuthSetup.HumanOnlyPolicy);
group.MapPost("/sync",            ...).RequireAuthorization(MachineAuthSetup.MachineOnlyPolicy);
```

---

## Rate limit, idempotência e jobs

### Rate limit

Por **IP real** (o `CF-Connecting-IP` validado), com contadores **no Redis**. Configurado em `appsettings`:

```json
"RateLimit": {
  "Global":  { "PermitLimit": 100, "WindowSeconds": 60 },
  "Login":   { "PermitLimit": 5,   "WindowSeconds": 60 },
  "Token":   { "PermitLimit": 10,  "WindowSeconds": 60 },
  "Refresh": { "PermitLimit": 20,  "WindowSeconds": 60 }
}
```

Estourou: **429** com `Retry-After`, em ProblemDetails como todo erro da API.

> **Por que no Redis e não com o `RateLimiter` nativo do .NET.** O nativo guarda os contadores **em memória, por processo**. Com N réplicas, o limite efetivo vira **N × o limite configurado** — em silêncio, e mudando toda vez que você escala a aplicação. Um limite de 5 tentativas de login vira 15 com três réplicas, e o teste que passou com uma réplica não diz nada sobre produção.

> **O limite de login é uma dimensão independente do lockout do Identity.** O lockout é por **conta** (mil senhas contra uma conta). Este é por **IP** (uma senha contra mil contas — o *password spraying*, que passa despercebido pelo lockout porque cada conta erra uma única vez). Faltando uma das duas, um dos dois ataques passa. O template anterior tinha só a de IP.

### Idempotência

**Opt-in.** O cliente manda um `Idempotency-Key`; a repetição recebe a mesma resposta, sem reexecutar:

```bash
curl -X POST https://api.suaapp.com/pedidos \
  -H "Idempotency-Key: 7f3a9c21-..." \
  -d '{...}'
```

| Situação | Resposta |
|---|---|
| Primeira vez | executa, e a resposta é guardada (24h) |
| Repetição | a **mesma** resposta, com o header `Idempotency-Replayed: true` |
| Ainda executando | **409** — "repita daqui a pouco" |
| A operação **falhou** | a reserva é **liberada**: a chave pode ser reusada |

> **Por que é opt-in.** O middleware não infere uma chave a partir do corpo ou do usuário. Uma chave inferida transformaria duas operações **legitimamente iguais** (comprar o mesmo item duas vezes, de propósito) numa só — em silêncio. Só o cliente sabe se duas requisições idênticas são a mesma intenção ou duas intenções.

> **Um erro nunca é gravado como resposta idempotente.** Se um 500 ficasse guardado, o cliente receberia aquele mesmo erro a cada retry **por 24 horas** — mesmo depois de o problema ter sido resolvido. A chave ficaria envenenada, e nada no sistema explicaria por quê.

### Jobs (Hangfire)

Banco próprio (`hangfire_{slug}`), criado pelo bootstrap. Dashboard em **`/jobs`**.

```csharp
BackgroundJob.Enqueue<IEmailSender>(x => x.SendAsync(userId, CancellationToken.None));
RecurringJob.AddOrUpdate<Report>("diario", r => r.RunAsync(CancellationToken.None), Cron.Daily);
```

> ⚠️ **O dashboard não é só leitura: ele permite DISPARAR e APAGAR jobs.** Exposto sem autenticação — que é o comportamento **padrão** do Hangfire fora de localhost — ele vira execução remota de código. Aqui ele exige a permissão `jobs.read`, pelo mesmo modelo de autorização de todo o resto. Ver [Documentação e painel de jobs](#documentação-e-painel-de-jobs).

---

## Documentação e painel de jobs

Duas páginas, **acessíveis em produção sem ficarem abertas**:

| Página | Exige | O que é |
|---|---|---|
| `/scalar` | `docs.read` | A documentação da API (OpenAPI + Scalar) |
| `/openapi/v1.json` | `docs.read` | O documento OpenAPI cru |
| `/jobs` | `jobs.read` | O painel do Hangfire |

### Como entrar

Abra qualquer uma delas no navegador. Sem sessão, você é redirecionado para **`/admin/login`** — um formulário mínimo que chama o **mesmo `AuthService`** do `/auth/login`, e portanto herda o lockout por conta, o rate limit por IP e o mesmo cookie `HttpOnly`. Depois de logar, você volta para onde estava indo.

> **Por que uma página de login, e não só o cookie da API.** O cookie funciona — mas ele só é emitido pelo `POST /auth/login`, que é uma chamada de *API*. Sem a página, acessar `/jobs` em produção exigiria logar no Postman, copiar o cookie e injetá-lo à mão no navegador. Funciona, e é exatamente o tipo de atrito que faz alguém abrir o dashboard "temporariamente" e nunca mais fechar.

> **A página não inventa autenticação nenhuma.** Um segundo caminho de login seria um segundo lugar para esquecer uma defesa.

### Por que a documentação exige permissão

Ela é o **mapa completo** da aplicação: todos os endpoints, parâmetros, esquemas de autenticação e códigos de erro. Servida a anônimos, é reconhecimento pronto — um atacante não precisa descobrir nada, basta ler.

**Não há flag de liga/desliga.** Uma `ScalarEnabled: false` daria a *impressão* de proteger, e alguém a deixaria ligada num ambiente exposto. A proteção é a permissão, e ela vale igual em todos os ambientes.

Se a sua API for **deliberadamente pública** (como a do Stripe), troque o `.RequirePermission(...)` por `.AllowAnonymous()` em `OpenApiSetup` — mas que seja uma decisão, não um esquecimento.

> O **JSON** do OpenAPI exige a mesma permissão que a página. Proteger só a página seria teatro: bastaria pedir o JSON direto para obter exatamente a mesma informação.

### Os três esquemas de autenticação estão no Scalar

Cookie, `Bearer` e `X-Api-Key` são declarados no documento OpenAPI (`SecuritySchemesTransformer`) — então dá para **testar um endpoint protegido pela própria página**, colando o token.

> **Sem isso o Scalar é uma vitrine.** O gerador de OpenAPI do .NET **não descobre** os esquemas a partir dos handlers registrados — ele não tem como saber que existe um header `X-Api-Key`. O resultado seria uma documentação bonita em que nenhum endpoint protegido funciona: não há onde colar o token, e todo "Send Request" volta 401. É o tipo de defeito que passa despercebido porque a página *parece* certa.

### Defesa em profundidade (opcional)

Nada impede você de pôr **Cloudflare Access** (Zero Trust) na frente de `/jobs`, `/scalar` e `/admin/*` — a autenticação acontece na borda, e o request só chega na aplicação se passar. A permissão continua valendo por baixo, então nenhuma das duas camadas é o único ponto de falha.

---

## Eventos de domínio (outbox)

**O problema.** Você cria um usuário e precisa mandar um e-mail. O caminho ingênuo — salva no banco, depois chama o serviço de e-mail — tem uma janela entre as duas coisas, e nela cabe um crash. O resultado é **um usuário sem e-mail**; invertendo a ordem, **um e-mail sem usuário**. Não há ordem que resolva: são dois sistemas, e não existe transação entre eles.

**A saída** é não ter dois sistemas no momento da escrita. O evento é gravado numa tabela do **mesmo banco**, na **mesma transação** do dado:

```csharp
db.Users.Add(user);
await outbox.EnqueueAsync(new UserCreated(user.Id, user.DisplayName), ct);

await db.SaveChangesAsync(ct);   // ← o dado E o evento, num commit só
```

Ou os dois existem, ou nenhum existe — quem garante isso é o Postgres, não o seu código. Um job do Hangfire drena a tabela a cada minuto e publica de verdade.

**Declare o evento e o handler:**

```csharp
public sealed record UserCreated(Guid UserId, string DisplayName) : IDomainEvent;

public sealed class SendWelcomeEmail(IEmailSender email) : IEventHandler<UserCreated>
{
    public async Task HandleAsync(UserCreated evt, CancellationToken ct) =>
        await email.SendAsync(evt.UserId, "Bem-vindo!", ct);
}
```

```csharp
// Program.cs
builder.Services.AddEventHandler<UserCreated, SendWelcomeEmail>();
```

> ⚠️ **A entrega é *at-least-once*, não *exactly-once* — o handler PRECISA ser idempotente.** Se o processo morrer entre publicar e marcar como publicado, o evento é reentregue. Um handler que manda e-mail sem checar nada mandará **dois e-mails**. Não é um defeito do outbox: é a consequência inevitável de não haver transação entre o banco e o mundo externo, e prometer o contrário seria mentira.

**Detalhes que importam:**

- **A mensagem publicada não é apagada** — ela é a trilha de que o evento aconteceu. Sem ela, não há como responder "este e-mail chegou a ser enviado?" depois de um incidente.
- **`FOR UPDATE SKIP LOCKED`**: duas réplicas rodando o job não processam a mesma mensagem. Sem isso, cada evento seria entregue em dobro.
- **Uma mensagem envenenada para depois de 5 tentativas.** Sem o teto, ela seria retentada para sempre, enchendo o log e atrasando as mensagens saudáveis atrás dela.
- **Um evento sem handler não é erro.** É marcado como processado — ninguém (ainda) se importa com ele, e retentá-lo até morrer só encheria o log de uma falha que não é falha.

---

## Dados pessoais (LGPD)

### Como declarar um campo pessoal

Na entidade, um **par**: o valor cifrado e o índice cego.

```csharp
public sealed class Employee : BaseEntity, ITenantEntity
{
    public Guid TenantId { get; set; }

    public Pii Email { get; set; }          // AES-256-GCM na coluna bytea
    public byte[] EmailIndex { get; set; } = [];   // HMAC-SHA256 — é por aqui que se busca

    public Pii Cpf { get; set; }
    public byte[] CpfIndex { get; set; } = [];
}
```

No mapeamento, **uma linha** configura os dois:

```csharp
builder.HasPii(e => e.Email, e => e.EmailIndex, PiiScope.Email, encryptor, unique: true);
builder.HasPii(e => e.Cpf,   e => e.CpfIndex,   PiiScope.Cpf,   encryptor, unique: true);
```

Ao gravar, calcule o índice:

```csharp
employee.Email = Pii.Create(PiiScope.Email, request.Email);
employee.EmailIndex = blindIndex.Compute(PiiScope.Email, request.Email);
```

### Como buscar

Pelo índice cego — **sem decifrar nada**. É assim que o login funciona com o e-mail criptografado:

```csharp
var lookup = blindIndex.Compute(PiiScope.Email, emailDigitado);

var user = await db.Employees
    .FirstOrDefaultAsync(e => e.EmailIndex == lookup, ct);
```

A normalização vive dentro do `Compute` (e-mail vira minúsculas; CPF perde a máscara), então `"JOAO@X.COM"` e `" joao@x.com "` acham o mesmo registro. Nunca normalize por fora — divergir a normalização entre gravação e busca quebra o login.

### Por que assim

| Decisão | Motivo |
|---|---|
| **AES-GCM com nonce aleatório** | O mesmo valor gera ciphertexts diferentes. Criptografia determinística vazaria igualdade entre registros, e seria fraca para o CPF (só existem ~10¹¹). |
| **GCM (autenticado)** | Se alguém alterar um byte no banco, a decifragem **falha** em vez de devolver lixo. |
| **Índice cego (HMAC, não SHA)** | Um SHA-256 puro de CPF é força-bruta de segundos. O HMAC exige a chave secreta. |
| **Versão da chave dentro do ciphertext** | É o que torna a **rotação viável**: dados cifrados com a v1 continuam legíveis depois que a v2 assume. Sem isso, rotacionar exigiria re-criptografar a base offline — e ninguém rotacionaria nunca. |

### Rotação de chave

Adicione a nova versão e mude a ativa. **Mantenha as antigas:**

```json
"Encryption": {
  "Keys": { "1": "<antiga>", "2": "<nova>" },
  "ActiveKeyVersion": 2
}
```

Dados novos usam a v2; os antigos continuam legíveis pela v1. **Remover a v1 torna os dados cifrados com ela irrecuperáveis** — a mensagem de erro diz isso explicitamente, mas o dado já era.

> A `BlindIndexKey` **não pode ser rotacionada sozinha**: mudá-la invalida todos os índices gravados e a busca para de achar qualquer coisa. Rotacioná-la exige recalcular os índices de toda a base, num processo offline deliberado.

### Auditoria (Art. 37)

Toda leitura de PII em claro via `IPiiReader` grava em `pii_access_logs`: quem leu, de quem, qual escopo, **por quê**, de que IP, e o `traceId`.

A tabela é **append-only** — o interceptor lança se alguém tentar alterar ou apagar um registro. Uma trilha de auditoria que pode ser apagada é exatamente o que um invasor apagaria.

O `reason` é obrigatório: sem ele, o log diz "alguém leu 400 CPFs" e não distingue uma exportação legítima de um vazamento.

**O titular sempre pode ver o próprio dado** (LGPD, Art. 18) — a checagem de permissão é dispensada quando `currentUser.UserId == entityId`, mas o acesso continua sendo registrado.

---

## Deploy

Dois containers, **o mesmo binário**, em sequência:

```yaml
migrations:
  environment:
    - MIGRATE_ONLY=true     # cria/migra e MORRE (exit 0)
  restart: "no"

api:
  depends_on:
    migrations:
      condition: service_completed_successfully   # só sobe se a migration passou
```

Isso elimina a concorrência por construção: o container de migrations é único, então não há duas réplicas fazendo `CREATE DATABASE` ao mesmo tempo.

> **A constante é `MIGRATE_ONLY`.** Ela é definida em **um único lugar** (`PersistenceExtensions.MigrateOnlyKey`) de propósito: no template anterior, o compose mandava `MIGRATE_ONLY` e o código lia `MIGRATION_ONLY` — uma letra de diferença, e **o deploy falhava 100% das vezes no primeiro boot**.

### Topologia

```
app.seudominio.com  →  Cloudflare Pages (frontend)
api.seudominio.com  →  Cloudflare DNS → Traefik → container
```

Mesmo apex nos dois lados = **same-site**: o cookie de autenticação usa `SameSite=Lax` e não briga com o ITP do Safari. Se o frontend for para um domínio realmente distinto (`*.pages.dev`), o cookie vira cross-site e o modelo de autenticação precisa ser revisto.

---

## Desenvolvimento local

Requer Docker (Postgres, Redis, Loki, Grafana) e o .NET 10 SDK.

```bash
dotnet build                              # warnings são erros
dotnet test                               # sobe Postgres/Redis efêmeros via Testcontainers

MIGRATE_ONLY=true dotnet run --project src/Garius.Api    # bootstrap do banco
dotnet run --project src/Garius.Api                      # sobe a API
```

| Endpoint | Para quê |
|---|---|
| `/` | ping |
| `/health/live` | o processo está vivo (não toca em dependências) |
| `/health/ready` | pronto para tráfego (checa Postgres e Redis) |
| `/health/detail` | diagnóstico — exige `X-Health-Key`; em produção, sem chave configurada, **não existe** |

Os testes do Secret Manager rodam contra o **GCP real** e são pulados automaticamente se não houver `GOOGLE_APPLICATION_CREDENTIALS` na máquina.
