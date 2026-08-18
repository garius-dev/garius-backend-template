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

**Tudo o que está aqui está pronto e testado** — 227 testes contra Postgres e Redis reais (Testcontainers), build estrito (warnings = erro) e auditoria de CVE no build.

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

### ⚠️ O que o template NÃO tem (e você vai precisar)

A **infraestrutura** de autenticação e autorização está completa e testada. As **telas de administração** dela, não — e isso é deliberado, mas você precisa saber antes de começar:

| Falta | O que isso significa na prática |
|---|---|
| **CRUD de usuários** | Não há `POST /users`. O **único** usuário que existe é o superadmin do `Bootstrap:AdminEmail`. |
| **CRUD de papéis** | Não há como criar o papel "Gerente" pela API. |
| **Conceder permissão / papel** | Não há endpoint. Hoje se faz **por SQL**, na tabela `user_claims` (ou `user_roles`). |
| **Alterar/recuperar senha** | Não há `POST /auth/change-password` nem fluxo de "esqueci a senha". |

As permissões `users.*` e `roles.*` **já estão declaradas** no catálogo, e o `.RequirePermission(...)` já as reconhece — falta só escrever os endpoints. O motivo de não virem prontos: **essas telas variam demais entre aplicações** (uma quer convite por e-mail, outra quer SSO, outra quer aprovação manual), e um CRUD genérico seria a primeira coisa que você jogaria fora.

O que já existe e ajuda: **`GET /permissions`** devolve o catálogo inteiro (valor, recurso, ação, descrição) para o front montar a tela de papéis sem manter uma segunda lista em JavaScript.

> **Enquanto isso, para conceder uma permissão a um usuário**, é um `INSERT` em `user_claims`:
> ```sql
> INSERT INTO user_claims ("Id", "UserId", "ClaimType", "ClaimValue", "TenantId", "Enabled", "CreatedAt", "UpdatedAt")
> VALUES (gen_random_uuid(), '<user-id>', 'permission', 'invoices.approve', NULL, true, now(), now());
> ```
> `TenantId = NULL` vale em todos os tenants. **Invalide o cache** depois (o `PermissionResolver` guarda no Redis) — ou espere o TTL.

---

**A infraestrutura está completa.** Não documente aqui o que ainda não existe. Documentação que mente é pior que documentação ausente.

---

## Derivando uma nova aplicação

### 1. Derive com `dotnet new`

```bash
dotnet new install .
dotnet new garius-api -n Tcm.SfcHortolandia.Api -o ../Tcm.SfcHortolandia.Api
```

**O nome que você passa é o namespace raiz da aplicação.** Os projetos são os sufixos dele:

```
src/Tcm.SfcHortolandia.Api/              namespace Tcm.SfcHortolandia.Api.Features.Auth
src/Tcm.SfcHortolandia.Core/             namespace Tcm.SfcHortolandia.Core.Entities
src/Tcm.SfcHortolandia.Infrastructure/   namespace Tcm.SfcHortolandia.Infrastructure.Database
tests/Tcm.SfcHortolandia.Tests/          namespace Tcm.SfcHortolandia.Tests.Auth
```

> O prefixo comum é o nome **menos o último segmento** (`Tcm.SfcHortolandia.Api` → `Tcm.SfcHortolandia`). Um nome sem ponto funciona igual: `-n MeuSistema` gera `MeuSistema.Api`, `MeuSistema.Core`…

E renomeia **tudo o que precisa ser renomeado**, de uma vez:

| | vira |
|---|---|
| Projetos, pastas e ~160 namespaces | `Tcm.SfcHortolandia.*` |
| `InternalsVisibleTo` | `Tcm.SfcHortolandia.Tests` |
| Filtro de log do Serilog | `"Tcm.SfcHortolandia"` |
| `Api:Title` (Scalar/OpenAPI) | `Tcm.SfcHortolandia.Api` |
| `UserSecretsId` | um GUID novo |
| **`Database:ApplicationName`** | **`Tcm.SfcHortolandia.Api`** |
| **`GcpSecrets:SecretName`** | **`tcm-sfchortolandia-api-secrets`** |

Daí saem os nomes no Postgres: `db_tcm_sfchortolandia_api`, `hangfire_tcm_sfchortolandia_api`, `tcm_sfchortolandia_api_user`.

> Verificado: a app derivada compila com **0 warnings** e a suíte nasce **verde** (167 passando; os 2 testes que leem o Secret Manager ficam *pulados* até você criar o secret — ver o passo 2).

> ⚠️ **Não copie a pasta à mão, e não peça a uma IA para renomear.** Renomear ~160 arquivos é mecânico, e uma IA acerta ~99% — o problema é o 1%: um `InternalsVisibleTo` órfão, ou o `Database:ApplicationName` esquecido. Nada disso quebra o build. **O `ApplicationName` esquecido é o pior:** a aplicação compila, sobe e funciona — apontando para o **mesmo banco e o mesmo usuário** do template. A colisão só aparece quando duas aplicações se atropelam em produção.

> `TemplateDerivationTests` **falha na app derivada** se o `Database:ApplicationName` ou o `GcpSecrets:SecretName` ainda forem os do template — transformando os dois erros mais caros (duas apps colidindo no mesmo banco, duas apps lendo o mesmo secret) em erro de build.
>
> ⚠️ **No próprio template esses testes são PULADOS** (`Assert.SkipWhen` no assembly `Garius.*`) — eles não teriam como distinguir "é o template" de "esqueci de renomear". Ou seja: **eles não protegem quem copia a pasta à mão** e mantém os nomes `Garius.*`. É mais uma razão para usar `dotnet new`, e não `cp -r`.

> ⚠️ **O `SecretName` esquecido é ainda pior que o `ApplicationName`.** A colisão de banco ao menos **quebra** visivelmente quando duas apps se atropelam. Já duas aplicações lendo o **mesmo secret** cifram dados pessoais com a **mesma chave**, e **nada nunca falha**: a rotação de uma quebra a outra, e quem tiver acesso ao secret de uma decifra a PII de todas.

> O **"Exportar Template" do Visual Studio não serve**: ele renomeia o namespace raiz, mas não sabe nada sobre o seu `appsettings.json` — e te deixa exatamente no erro silencioso do banco.

### 2. Crie o secret no Google Secret Manager

O `dotnet new` já apontou a sua app para o secret **dela** (`tcm-sfchortolandia-api-secrets`) — falta criá-lo. Um secret por aplicação, com um **JSON flat** (chaves no formato `Section:Key`):

```json
{
  "Database:RootPassword": "<a mesma em todas as apps — é a chave mestra que cria bancos>",
  "Database:AppPassword": "<única desta aplicação>",
  "Redis:Password": "<a senha do seu Redis>",

  "Encryption:Keys:1": "<32 bytes em base64>",
  "Encryption:ActiveKeyVersion": "1",
  "Encryption:BlindIndexKey": "<32 bytes em base64>",

  "Jwt:SigningKey": "<32 bytes em base64>",

  "Bootstrap:AdminEmail": "voce@empresa.com",
  "Bootstrap:AdminPassword": "<uma senha forte, mínimo 12 caracteres>"
}
```

E, **só em produção**, os endereços — no mesmo secret:

```json
{
  "Database:Host": "postgres-prod",
  "Redis:ConnectionString": "redis-prod:6379",
  "Security:TrustedProxies:0": "172.18.0.0/16",
  "Cors:AllowedOrigins:0": "https://app.suaapp.com"
}
```

> **Em desenvolvimento você não configura nada disso.** A infra local é sempre a mesma (`localhost`), e já está no `appsettings.Development.json` — é `dotnet run` e pronto.
>
> Em produção eles mudam por **servidor**, e por isso vêm de fora. `Database:Host` e `Redis:ConnectionString` são o **nome do container** na rede Docker — nunca `localhost`: dentro de um container, `localhost` é o *próprio* container.
>
> A aplicação **não sobe** sem eles — falha fechada, e o log diz qual falta.


> **`Bootstrap:*` é o que resolve o ovo e a galinha.** Sem elas, a aplicação sobe **fechada** — a `FallbackPolicy` exige autenticação em tudo, o `/scalar` exige `docs.read`, o `/jobs` exige `jobs.read` — e não existe **nenhum usuário** para conceder permissão a ninguém. Nem para entrar e criar o primeiro.
>
> Com elas, o bootstrap cria esse usuário com a permissão **curinga** (`*`), que abre tudo — inclusive as permissões que você criar depois.
>
> **Não há senha padrão, e isso é deliberado.** Um `admin/admin` embutido é como um deploy vaza: alguém deriva, sobe, esquece de trocar, e uma conta com poder total fica aberta na internet — num template que *parece* seguro. Se as duas chaves não estiverem no secret, **nenhum usuário é criado** (e o bootstrap avisa no log). A ausência de configuração falha **fechada**.
>
> O seed é **idempotente e sem efeito colateral**: se o usuário já existe, o bootstrap **não toca nele** — não redefine a senha nem reconcede a permissão. Se fizesse, seria uma porta dos fundos que reabre a cada deploy: você revoga um acesso, faz um deploy qualquer, e o acesso volta em silêncio.

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

**Uma vez por aplicação**, no `appsettings.Production.json`. Nada aqui é segredo — são endereços, e é por isso que ficam no arquivo e não no Secret Manager:

```json
{
  "Database": { "Host": "postgres-prod" },
  "Redis":    { "ConnectionString": "redis-prod:6379" },

  "Security": { "TrustedProxies": [ "172.18.0.0/16" ] },
  "Cors":     { "AllowedOrigins": [ "https://app.seudominio.com" ] },

  "Serilog":  { "Loki": { "Enabled": true, "Url": "http://loki:3100" } }
}
```

- **`Database:Host` e `Redis:ConnectionString` são o nome do CONTAINER** na rede Docker — nunca `localhost`: dentro de um container, `localhost` é o *próprio* container.
- **`TrustedProxies` é obrigatório em produção** — é a rede Docker do Traefik. Sem ele **a aplicação não sobe** (falha explícita e proposital — veja o porquê logo abaixo).
- **`AllowedOrigins` vazio nega tudo.** Se houver frontend, declare a origem.

> Depois disso, o deploy é **um comando**: `./deploy.ps1 v1.2.0` — testa, builda, publica e sobe no servidor. O `.env` tem **oito linhas** e não cresce.

#### `Security:TrustedProxies` — o que é, e por que não pode ficar vazio

**Pegue o valor assim** (uma vez por servidor — é o mesmo para todas as suas apps):

```bash
docker network inspect garius_network --format '{{(index .IPAM.Config 0).Subnet}}'
# → 172.18.0.0/16
```

É a **rede Docker do Traefik** — o endereço de quem entrega o request no seu container. **Não** é da Cloudflare (essa é outra camada; veja abaixo).

**O problema que ele resolve.** O `X-Forwarded-For` é só um *header HTTP*: quem faz a requisição escreve o que quiser nele.

```bash
curl https://api.suaapp.com/auth/login -H "X-Forwarded-For: 1.2.3.4" -d '...'
```

Se a aplicação aceita esse header de **qualquer origem**, ela acredita que o request veio de `1.2.3.4` — porque o atacante disse que veio. E o template usa o IP para três coisas que então **deixam de funcionar**:

| Usa o IP para | O que quebra se o header for falsificável |
|---|---|
| **Rate limit por IP** | o atacante manda um IP diferente a cada tentativa e o limite **nunca dispara** — o brute force de senha passa livre |
| **Auditoria LGPD** | o log de acesso a dado pessoal registra o IP que o atacante escolheu |
| **Lockout / anomalia** | idem: cada tentativa parece vir de alguém novo |

**O que a lista faz.** Ela diz: *"só aceite o `X-Forwarded-For` se o request chegou **deste** endereço"*. Como o único que fala com o container é o Traefik (pela rede Docker), a lista é a rede do Traefik. Um request que chegue de outro lugar tem o header **ignorado**, e vale o IP real da conexão TCP.

> ⚠️ **Não "resolva" isso deixando a lista vazia.** No ASP.NET, `KnownProxies` e `KnownNetworks` vazios significam **confiar em qualquer um** — e é por isso que a aplicação **se recusa a subir** nesse estado, em vez de aceitar em silêncio. Um rate limit que não limita é pior que nenhum: ele passa a impressão de que existe uma defesa.

**E a Cloudflare?** É a camada de **fora** (internet → Traefik), e tem configuração própria — `Security:TrustCloudflareIps`, que já vem `true`. Ali o template usa o `CF-Connecting-IP`, validado contra os ranges publicados pela Cloudflare. O `TrustedProxies` cuida do salto **de dentro** (Traefik → container). São dois saltos, duas configurações.

### 4. Registre as regras DA SUA aplicação

Preencha o **`REGRAS-DA-APLICACAO.md`** — autenticação real, papéis, domínio, integrações.

Isso não é burocracia, e o atrito que ele evita é concreto: sem esse arquivo, o único material que
descreve o sistema é o README **do template**. Uma IA (ou uma pessoa nova) lê isso e conclui que o
template é a especificação — e passa a recusar features perfeitamente legítimas por estarem "fora
das regras".

> **Caso real:** alguém derivou o template, pediu integração de login com o Google, e a IA recusou
> citando o README. Google OAuth não viola **nenhuma** das 10 regras — o problema era que não havia
> onde estivesse escrito que aquela aplicação usa Google.

Um teste (`ApplicationRulesTests`) **falha** enquanto o arquivo estiver com os comentários de
exemplo. Ele é ignorado no próprio template, onde o formulário em branco é o estado correto.

> **A base é um ponto de partida, não um teto.** Adicionar provedores de autenticação, criar
> entidades e permissões, apagar o que veio de exemplo e escrever as telas de administração é o uso
> **esperado**. As 10 regras dizem *como* construir; elas não limitam *o que* construir.

### 5. Decida a tenancy


```json
"Tenancy": { "Mode": "SingleTenant" }   // ou "MultiTenant"
```

**Alternar não muda o schema** — a coluna `TenantId` e o query filter existem nos dois modos. Muda apenas qual `ITenantResolver` é registrado. Você pode migrar de single para SaaS depois sem migration destrutiva.

### 6. Rode

```bash
# cria banco, roles, grants e aplica migrations — e MORRE (exit 0)
MIGRATE_ONLY=true dotnet run --project src/Tcm.SfcHortolandia.Api

# sobe a API
dotnet run --project src/Tcm.SfcHortolandia.Api
```

Os projetos já se chamam `Tcm.SfcHortolandia.*` — o `dotnet new` cuidou disso.

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
    user.EmailPii, PiiScope.Email, nameof(ApplicationUser), user.Id,
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
using Garius.Api.Infrastructure.Authorization;   // RequirePermission
using Garius.Api.Infrastructure.Errors;          // ToHttpResult
using Garius.Api.Infrastructure.Validation;      // ValidateRequests
using Garius.Core.Authorization;                 // Permissions

namespace Garius.Api.Features.Products;

public static class ProductEndpoints
{
    public static void MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        // ValidateRequests: todo endpoint deste grupo cujo request tenha um
        // AbstractValidator<T> é validado ANTES do handler. Ver "Validação", abaixo.
        var group = app.MapGroup("/products").WithTags("Products").ValidateRequests();

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

### Validação

> ⚠️ **Em Minimal API, `[Required]` e `[MaxLength]` NÃO fazem nada.** Não existe o `[ApiController]` do MVC, que era quem ligava a validação automática das Data Annotations. Decorar o record com elas é **decoração**: *parece* proteger, e não protege. Esta seção é o que ocupa esse lugar — e é **obrigatória**, não opcional.

O contrato é **uma coisa só**: crie um `AbstractValidator<SeuRequest>` ao lado do endpoint. Ele é descoberto, registrado e aplicado sozinho — **não há passo dois, e não há chamada para esquecer**.

```csharp
// src/Garius.Api/Features/Products/ProductValidators.cs
public sealed class CreateProductRequestValidator : AbstractValidator<CreateProductRequest>
{
    public CreateProductRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Price).GreaterThan(0);
    }
}
```

Um corpo inválido nem chega ao handler — a resposta é **400 no contrato da API**, com os erros **por campo** e **todos de uma vez** (é o que o front usa para pintar de vermelho os dois campos, em vez de um por vez):

```json
{
  "code": "validation.failed",
  "traceId": "86e0f207...",
  "errors": {
    "name":  ["'Name' não pode ser vazio."],
    "price": ["'Price' deve ser maior que 0."]
  }
}
```

#### Validar um FK: a regra vive UMA vez

Vários endpoints recebem o mesmo `ProductId` — criar pedido, adicionar item, aplicar desconto. Todos precisam da mesma pergunta: *existe? está ativo? é do meu tenant?* Se cada validator escrever isso à mão, é questão de tempo até um endpoint novo — escrito seis meses depois, com pressa — **esquecer**. E o esquecimento não quebra nada na hora: deixa entrar um FK órfão, ou pior, um id de **outro tenant**.

Por isso a regra é **uma extensão reutilizável**:

```csharp
public sealed class CreateOrderValidator : AbstractValidator<CreateOrderRequest>
{
    public CreateOrderValidator(AppDbContext db)          // o DbContext é injetado
    {
        RuleFor(x => x.ProductId).MustExist<CreateOrderRequest, Product>(db, "Produto");
        RuleFor(x => x.Quantity).GreaterThan(0);
    }
}
```

`MustExist` parece checar só existência — mas checa **as três coisas**, e é o `AppDbContext` que faz as outras duas de graça: o **query filter global** já aplica `Enabled = true` e o `TenantId` corrente em toda consulta. Um registro apagado, ou de outro tenant, **simplesmente não existe** para essa query. É seguro por construção — não há como escrever a regra e esquecer do tenant.

> A mensagem é a **mesma** para "não existe", "foi apagado" e "é de outro tenant". Distinguir os três daria um **oráculo**: o atacante descobriria quais ids existem em outros tenants variando o palpite.

#### O que **não** vai no validator

- **Autorização** (*"esse usuário pode?"*) — é `.RequirePermission(...)`.
- **Regra de negócio com estado** (*"esse cliente tem crédito?"*) — é o service, devolvendo `Result.Failure`.
- **Qualquer coisa que vire oráculo** — validar que "o e-mail existe" no login destruiria, pela porta dos fundos, a defesa contra enumeração de contas.

#### Onde o filtro roda (e por que importa)

**Depois da autorização.** Um anônimo leva **401 antes** de qualquer validator rodar. Não é preciosismo de status: validators podem **ir ao banco** (`MustExist`), e validar antes de autorizar transformaria a validação num vetor de DoS — consultas gratuitas, sem autenticação.

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

**E as permissões do banco? Não precisa fazer nada.** A tabela nova já nasce acessível ao usuário de runtime — que continua sem poder fazer DDL.

Quem garante isso é o `ALTER DEFAULT PRIVILEGES` do bootstrap: ele concede `SELECT/INSERT/UPDATE/DELETE` sobre as tabelas que **ainda não existem**, criadas dali em diante pela role que o executou (a root — que é exatamente a do container de migrations). Um `GRANT ON ALL TABLES` comum não serviria: ele só alcança o que já existe.

> Sem isso, o sintoma seria tardio e brutal: a migration aplica, o deploy passa, e a API morre com `permission denied for table` **na primeira query da feature nova, em produção**. Há um teste (`Uma_tabela_criada_por_uma_migration_FUTURA_ja_nasce_acessivel`) que roda uma migration *depois* do bootstrap e exercita a tabela **com o usuário de runtime real** — remover o `ALTER DEFAULT PRIVILEGES` o faz falhar.

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

**De onde vem o token.** Duas fontes, e você precisa das duas:

| Origem | Quando |
|---|---|
| `POST /auth/login` | emite o par de CSRF junto com a sessão |
| `GET /auth/csrf` | **recupera** o token a qualquer momento, sem relogar |

O `GET /auth/csrf` existe porque o cookie de sessão dura **8 horas** e o de CSRF é de **sessão do navegador**. Sem ele, quem fecha a aba e volta fica num beco sem saída: **sessão válida e nenhuma forma de obter o token** — todo POST responde 403, e a única saída seria deslogar e logar de novo. **Chame-o no boot do app**, antes da primeira requisição que altera estado.

> Ele é **anônimo** de propósito: o token de CSRF **não é uma credencial**. Ele não autentica ninguém — só prova que quem o envia consegue *ler* cookies da nossa origem, coisa que um site de terceiro não consegue (same-origin policy). Sem o cookie de **sessão**, o token sozinho não abre porta nenhuma.

O cookie **`garius.csrf-token`** é legível pelo JavaScript. Leia-o e reenvie no header em **toda requisição que altera estado** (POST/PUT/PATCH/DELETE):

```js
// no boot do app: garante que o token existe, mesmo com a sessão vinda de antes
await fetch('/auth/csrf', { credentials: 'include' });

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

Sem o header, a API responde **403** — com o código `auth.csrf_token_invalid`, que diz **qual** é o problema. (Já disse `auth.insufficient_permission`, e mandava o desenvolvedor caçar o bug na autorização, que estava perfeita. Um erro que mente sobre a própria causa custa mais caro que o erro.)

> Requisições autenticadas por `Authorization: Bearer` ou `X-Api-Key` (ver [Autenticação de máquina](#autenticação-de-máquina-m2m-e-terceiros)) dispensam CSRF: não há cookie ambiente que o navegador envie sozinho, então não há o que explorar.

**Três endpoints dispensam o token**, cada um por sua razão:

| Endpoint | Por que dispensa |
|---|---|
| `POST /auth/login` | **prova a senha** |
| `POST /auth/token` | **prova o `client_secret`** |
| `POST /auth/refresh` | o `SameSite=Lax` já barra o ataque |

Nos dois primeiros, quem já tivesse a credencial não precisaria de CSRF nenhum — o pior que um ataque conseguiria é logar a vítima na conta do próprio atacante. E exigir o token no **login** o torna **impossível de chamar** para quem já tem sessão aberta: o POST leva o cookie da sessão anterior, o antiforgery cobra um header que o cliente ainda não tem, e **relogar responde 403**.

O **refresh** é diferente: quem barra o ataque ali é o **`SameSite=Lax`**, não o token. `Lax` impede o navegador de enviar o cookie num **POST cross-site** — e o refresh é POST, então o site malicioso não consegue nem fazer o cookie viajar. O token de CSRF ali não acrescentava defesa, só **custava**: o front precisava tê-lo em mãos *antes* de conseguir renovar a sessão, justamente na situação em que ele pode não tê-lo. **Uma redundância que cria um impasse não é defesa em profundidade — é só o impasse.**

> O **`/auth/logout` continua exigindo** o token, de propósito: forçar o logout de alguém é um ataque real (irritante, não crítico), ele não prova credencial nenhuma, e — ao contrário do refresh — o front sempre tem o token quando chega lá.

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
| JWT (OAuth2 *client credentials*) | um **sistema seu** | `POST /auth/token` → `Authorization: Bearer eyJ...` |
| Chave de API | um **terceiro** | `Authorization: Bearer gk_...` |

As três chegam à autorização como um principal com claims de permissão. **Um endpoint declara a permissão e serve as três** — não existe um segundo vocabulário de escopos para máquina:

```csharp
group.MapGet("/", ...).RequirePermission(Permissions.Invoices.Read);
// vale para cookie, JWT e chave de API. O mesmo código.
```

> **As duas credenciais de máquina viajam no mesmo header `Authorization: Bearer`** — que é o que o mercado faz (Stripe, OpenAI, GitHub) e o que todo integrador já sabe mandar sem ler documentação. O que as distingue **não é heurística**: uma chave de API sempre começa com `gk_`, e um JWT nunca pode (ele é base64url de um JSON, e sempre sai como `ey...`). A decisão vive num lugar só, em `MachineAuth.ExtractCredential` — o forwarder de esquema, o handler da chave e o bypass de CSRF **têm** de concordar sobre o que é o quê.
>
> O header `X-Api-Key` **continua aceito**, mas deixou de ser o caminho recomendado.

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

# O terceiro a usa como usaria a de qualquer SaaS:
curl -H "Authorization: Bearer gk_a1b2c3d4..." https://api.suaapp.com/invoices

# (o header X-Api-Key também funciona, mas não é o recomendado)
```

O **`callLimit` é uma quota total**, não um rate limit. É o que transforma um vazamento silencioso e ilimitado num vazamento que **trava e aparece**. Uma chave estourada para de funcionar, e alguém precisa olhar para ela.

Consumiu a quota? `GET /machine/api-keys` mostra o `callCount` de cada chave. Para liberar mais chamadas, o caminho é criar uma chave nova (o segredo não é recuperável) ou subir o `callLimit` da existente direto no banco — **um endpoint de "aumentar a quota" ainda não existe**, e é o próximo passo natural se você for vender acesso.

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
  "Enabled": true,
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

### O CSP: três políticas, e a regra que não se quebra

O `SecurityHeadersMiddleware` emite **três** CSPs diferentes, e a distinção não é preciosismo:

| Resposta | CSP | Por quê |
|---|---|---|
| **API** (todo o resto) | `default-src 'none'` | uma resposta JSON não carrega nada, nunca |
| **`/admin/login`** (nossa) | **nonce**, sem `'unsafe-inline'` | nós escrevemos o HTML, então o nonce funciona — e é a página que recebe **senha** e guarda o cookie de sessão |
| **`/scalar`, `/jobs`** (de terceiros) | **`'unsafe-inline'`**, sem nonce | elas injetam CSS e JS **em runtime, por JavaScript**, e um nó criado assim **não carrega nonce** |

> ⚠️ **Nonce e `'unsafe-inline'` NUNCA na mesma diretiva.** Pela spec, o navegador **ignora** o `'unsafe-inline'` quando há um nonce presente. Não é um fallback — é uma **anulação**. Mandar os dois juntos deixou a página do Scalar **em branco**, com o console repetindo *"Applying inline style violates the following CSP directive"*. Se você criar uma página nova, decida qual dos dois usar: **um ou outro**.

O `'unsafe-inline'` do Scalar e do Hangfire é uma concessão consciente, e o que a limita é que essas páginas **não refletem entrada de usuário** (renderizam o *nosso* OpenAPI e a *nossa* fila de jobs) e **exigem permissão** — nunca são servidas a um anônimo. A página de login, que é a que recebe texto de fora, fica com o nonce.

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

### Uma imagem, não duas

O `Dockerfile` na raiz produz **uma** imagem, e ela serve os dois containers acima — quem decide o papel é a env var, não a tag. Não existe uma imagem `-migration` separada: ela seria bit a bit idêntica à da API, e manter duas dobra o build, o push e a chance de subir a API numa versão e a migration em outra.

A imagem roda como **não-root** (usuário `app`, uid 1654) e escuta na **8080**. Nada é publicado no host: quem fala com a internet é o Traefik.

> ⚠️ O `Dockerfile` copia o **`.editorconfig`** antes de compilar, e isso não é decoração. É ele que calibra os analisadores, e aqui *warning é erro*. Sem ele o build passa liso na sua máquina e **quebra dentro do container** (CA1000, CA1711, CA1716…) — o pior tipo de erro, o que só aparece no deploy.

### O deploy — um comando, do código ao ar

```powershell
./deploy.ps1 v1.2.0
```

Isso faz **tudo**, e para no primeiro erro:

1. grava `APP_VER=v1.2.0` no `.env`;
2. `dotnet clean && build && test` — a suíte inteira, com Postgres e Redis reais;
3. `docker build` e `docker push`;
4. envia o `docker-compose.app.yml`, o `.env` e a service account para o servidor (SFTP, conferindo o hash de cada arquivo);
5. `docker compose pull && up -d` lá — as migrations rodam primeiro, e a API só sobe se elas passarem.

Variações:

```powershell
./deploy.ps1 v1.2.0 -NoDeploy   # publica no Docker Hub e para (não toca no servidor)
./deploy.ps1 -Local             # só a imagem local, para conferir
```

**Nenhum arquivo novo para manter.** O destino vai no `.env` que você já tem:

```bash
DEPLOY_PATH=/opt/garius/customers/tcm/sfchortolandia   # a pasta no servidor; o nome é seu
DEPLOY_HOST=1.2.3.4
DEPLOY_USER=root
```

O `DEPLOY_PATH` é **você** quem escolhe: não precisa ser igual ao `PROJECT_NAME`. O deploy cria a pasta se ela não existir.

> **A senha não fica no `.env`, de propósito.** Esse arquivo é *enviado para o próprio servidor* (o compose o lê lá) — e uma senha de SSH viajando junto com o que ela protege, e ficando em disco em claro numa máquina de produção, é uma péssima ideia.
>
> O script a pede **uma vez** e a guarda cifrada em `%USERPROFILE%\.garius\`, pela DPAPI do Windows — atrelada ao seu usuário, de modo que nem outro usuário da mesma máquina consegue decifrá-la. Nos deploys seguintes ele não pergunta mais.

> **Passar a versão como argumento não é conveniência: é o que evita o erro mais caro do deploy.** Subir o `APP_VER` é o passo mais fácil de esquecer, e o esquecimento é *silencioso* — o servidor faz `pull` de uma tag que já tem em cache e sobe o binário **antigo**, sem erro nenhum. Aqui o `.env` local é o **mesmo** que vai para o servidor, então os dois nunca divergem.

> Republicar uma tag que já existe é **permitido** (o script avisa, em amarelo). É rotina enquanto uma release está sendo trabalhada. O aviso existe porque quem já tiver puxado aquela tag fica com um binário **diferente** do que está sendo publicado agora — e, na hora de investigar um bug, a versão deixa de identificar o binário. Em produção, prefira uma tag nova.

> A service account vai com permissão **600** (e a pasta `secrets/`, **700**). É uma credencial: com `777`, qualquer usuário do servidor lê a chave que abre a senha do banco, as chaves de criptografia e a do JWT.

O compose **não sobe infraestrutura**: ele assume Traefik, Postgres, Redis e Loki já rodando na rede `garius_network` (externa). O único segredo que vive no servidor é o JSON da service account — é ele que destrava o Google Secret Manager, de onde vêm senha do banco, chaves de criptografia e a do JWT.

> **`.env` e `secrets/` nunca vão para o git.** O `.gitignore` já bloqueia os dois. Essa regra existe porque no template **anterior** havia uma exceção (`!docker/**/secrets/gcp-service-account.json`) que vazou a chave para dentro do repositório.

### Topologia

```
app.seudominio.com  →  Cloudflare Pages (frontend)
api.seudominio.com  →  Cloudflare DNS → Traefik → container
```

Mesmo apex nos dois lados = **same-site**: o cookie de autenticação usa `SameSite=Lax` e não briga com o ITP do Safari. Se o frontend for para um domínio realmente distinto (`*.pages.dev`), o cookie vira cross-site e o modelo de autenticação precisa ser revisto.

### Kubernetes: o chart

O chart está em [`deploy/helm`](deploy/helm). Ele **não substitui** o compose — os dois modos de deploy convivem, e o compose continua sendo o caminho para VPS.

```bash
kubectl create secret generic garius-gcp-sa --from-file=key.json=<sua-service-account>

helm upgrade --install garius deploy/helm   --set image.tag=v1.2.0   --set ingress.host=api.seudominio.com
```

> **A tag da imagem é obrigatória.** Sem ela o chart **recusa renderizar**. Sem esse guarda, o Helm montaria `repositorio:` e o Kubernetes leria como `:latest` — subindo uma versão que ninguém escolheu, sem erro nenhum.

O que o chart traz, e por quê:

| Recurso | Por que existe |
|---|---|
| `Job` de migração | Hook `pre-install,pre-upgrade`: roda **antes** da API, e se falhar o deploy para. Mesmo contrato do `service_completed_successfully` no compose |
| `PodDisruptionBudget` | Sem ele, `kubectl drain` pode derrubar **todas** as réplicas de uma vez |
| `HPA` | Scale-down com janela de 5 min: sem estabilização o HPA oscila, e cada descida derruba pods que estavam servindo |
| `topologySpreadConstraints` | Sem isso o scheduler empilha as réplicas num nó só — e perder esse nó derruba tudo, com N réplicas "no ar" |
| `NetworkPolicy` | Default deny. **Desligada por padrão**: uma policy errada quebra a app de um jeito confuso (o sintoma é timeout de DNS) |
| `securityContext` | `readOnlyRootFilesystem`, `drop: ALL`, não-root |

Validar sem cluster:

```powershell
./deploy/helm/validate.ps1
```

Ele roda `helm lint`, renderiza com tudo ligado, confirma que a tag é obrigatória, e passa o YAML pelo **schema real** do Kubernetes (`kubeconform`). O último passo é o que vale: um `maxUnavailble` (typo) passa pelos dois primeiros e só falha no `kubectl apply` — no meio do deploy, com a migração já rodada.

> ### ⚠️ O chart exige Kubernetes 1.29+
>
> O `preStop` usa a action `sleep` **nativa**, e não `exec: [/bin/sleep]`. O motivo é o item seguinte: a imagem é chiseled e **não tem `/bin/sleep`**. Um preStop com `exec` falharia em silêncio (o Kubernetes só registra um evento) e a corrida do encerramento voltaria — com o manifesto parecendo correto.
>
> Em cluster mais antigo: volte a imagem base para a variante não-chiseled e use `exec`.

### A imagem: chiseled e pinada por digest

| Antes | Agora |
|---|---|
| `aspnet:10.0` (tag mutável) | `aspnet:10.0-noble-chiseled@sha256:…` |
| ~91 MB só de runtime | **62 MB** com a aplicação dentro |
| shell, apt, curl disponíveis | nada disso |

**Chiseled** remove shell, gerenciador de pacotes e coreutils. Quem conseguir execução de comando dentro do container não acha `sh`, `curl` nem `cat` para escalar.

**Digest** torna o build reproduzível: o mesmo commit dá a mesma imagem, sempre. O custo é real — digest fixo não recebe patch sozinho — e por isso o [`renovate.json`](renovate.json) existe. **Pinar sem automação de atualização é trocar um problema por outro.**

> **Duas consequências que mordem:**
> 1. `docker exec <container> sh` não funciona. Para depurar, use `kubectl debug` ou troque temporariamente para a variante não-chiseled.
> 2. O healthcheck do compose **foi removido** — não havia mais com o que fazer a requisição. Quem checa a saúde é quem está de fora: a `httpGet` probe no Kubernetes, o Traefik no compose.

O `deploy.ps1` gera **SBOM** (syft) e **assina** a imagem (cosign) quando as ferramentas estão instaladas. Os dois são opcionais de propósito: travar a publicação porque o `syft` não está instalado transformaria uma melhoria de supply chain num bloqueio de release — e a reação previsível seria alguém arrancar o passo do script.

### Rate limit: duas dimensões

| Camada | Onde no pipeline | Particiona por |
|---|---|---|
| Volume | **antes** da autenticação | IP real |
| Cota | **depois** da autorização | usuário / client M2M / chave de API |

Limite só por IP erra dos dois lados: pune o cliente legítimo atrás de CGNAT (milhares de pessoas dividindo um endereço, e portanto uma cota) e não contém o atacante com um `/64` de IPv6, que tem endereços de sobra para diluir o volume.

A ordem de cada uma é obrigatória. A de IP fica na frente porque é a defesa contra volume e precisa ser **barata** — depois da autenticação, cada tentativa de brute force pagaria um PBKDF2 antes de ser recusada, e o rate limit viraria o vetor de DoS. A de identidade **precisa** saber quem chama, o que só existe depois da autorização.

Toda resposta carrega os headers da **RFC 9331**:

```
RateLimit-Limit: 300
RateLimit-Remaining: 42
RateLimit-Reset: 30
```

Sem eles o integrador só descobre o limite ao bater nele, e a única estratégia que lhe resta é tentar de novo.

| Configuração | Default | Para quê |
|---|---|---|
| `RateLimit:Identity:Enabled` | `true` | desliga só esta camada, sem derrubar a de IP |
| `RateLimit:Identity:PermitLimit` | `300` | por credencial, por janela |

### ⚠️ Conexões: a conta que ninguém faz

`Database:MaxPoolSize` é **por réplica**. A conta que importa é:

```
réplicas × (MaxPoolSize + 8 do Hangfire) + 5 (migração e folga)
```

Contra um Postgres de fábrica (`max_connections=100`):

| Réplicas | Pool 5 | Pool 10 | Pool 20 |
|---|---|---|---|
| 3 | 44 ✅ | 59 ✅ | 89 ❌ |
| 5 | 70 ✅ | 95 ❌ | 145 ❌ |
| 10 | 135 ❌ | 185 ❌ | 285 ❌ |

**Acima de poucas réplicas, nenhum `MaxPoolSize` resolve.** Diminuir o pool até caber não é a saída: um pool pequeno demais põe as requisições na fila esperando conexão, trocando um erro visível por uma lentidão difícil de diagnosticar. A saída é **pgBouncer/PgCat** ou subir o `max_connections`.

> **Isto não falha em teste nem em staging com uma réplica.** Falha quando o autoscaler escala no pico — e o `too many clients already` chega junto com o incidente que causou o pico, parecendo consequência dele.

Preencha `Database:ExpectedReplicas` com o `maxReplicas` do seu HPA e a aplicação **avisa no boot** quando a conta passa de 70% do teto. É `Warning`, não falha fechada: é um alerta de capacidade sobre uma estimativa que a aplicação não tem como verificar.

> **pgBouncer em modo `transaction` quebra prepared statements.** Com Npgsql, exige `Max Auto Prepare=0` ou `No Reset On Close`. É pegadinha conhecida e cara.

| Configuração | Default | Para quê |
|---|---|---|
| `Database:MaxPoolSize` | `10` | conexões **por réplica** |
| `Database:ExpectedReplicas` | `0` (desliga o aviso) | o `maxReplicas` do seu HPA |
| `Database:PostgresMaxConnections` | `100` | o `max_connections` do **seu** Postgres |

### Encerramento gracioso (e as três probes)

Isto importa em **qualquer** orquestrador — Kubernetes, Swarm, ou um compose com `--force-recreate`.

Quando o orquestrador remove uma instância, ele manda `SIGTERM` **e** tira o endereço do balanceamento **ao mesmo tempo**. A segunda parte não é instantânea: ela leva de um a alguns segundos para se propagar. Nessa janela a instância já está encerrando e o balanceador **ainda manda tráfego** — e cada requisição que cai aí é um erro para um cliente real. Acontece em *todo* deploy.

O template resolve isso assim: no `SIGTERM`, o `/health/ready` passa a **reprovar imediatamente** (sai do balanceamento), enquanto a aplicação **continua servindo** o que já está em andamento. Ver `ShutdownState`.

As três probes têm efeitos diferentes, e **trocá-las tem consequência severa**:

| Probe | Pergunta | Efeito se reprovar |
|---|---|---|
| `/health/startup` | já subiu? | o orquestrador **espera** (não mata durante um boot lento) |
| `/health/live` | o processo está vivo? | **reinicia** o container |
| `/health/ready` | pronto para tráfego? | **tira do balanceamento** (não reinicia) |

> **Por que o liveness não checa o banco.** Se ele checasse, uma queda do Postgres viraria um *restart loop* de toda a frota — e reiniciar container não conserta banco de dados. Pior: o `CrashLoopBackOff` atrasa a recuperação mesmo depois de o banco voltar. O liveness é um predicado vazio de propósito.

> **Por que o Postgres é `Degraded` e o Redis é `Unhealthy` no readiness.** Sem Redis a aplicação não lê o cookie (o keyring do DataProtection vive nele), então ela não serve praticamente nada. Sem Postgres ela ainda serve o que não toca o banco. Se um Postgres que pisca por 10s reprovasse o readiness, **todas** as réplicas sairiam do balanceamento juntas e o ingress responderia 503 para 100% do tráfego.

O readiness é **cacheado por 3 segundos** (`ReadinessCache`). Com N réplicas e um período de probe de poucos segundos, o health check sozinho faria dezenas de consultas por minuto ao Postgres e ao Redis — e quando o banco está sofrendo, que é quando o readiness importa, esse tráfego extra **piora** o problema que ele deveria só observar.

> **Isto é testado de ponta a ponta.** `ShutdownE2ETests` sobe a aplicação **num container**,
> dispara carga, manda um `SIGTERM` de verdade (`docker stop`) e afirma que **nenhuma
> requisição se perde** — e que o `/health/ready` reprova *durante* a drenagem enquanto o que
> já estava em voo é atendido.
>
> O teste é lento (~50s) e usa a **mesma imagem** do Dockerfile de produção. Está em collection
> própria, com `Trait("Category", "EndToEnd")`, para poder ser isolado no CI:
>
> ```bash
> dotnet test --filter "Category!=EndToEnd"     # a suíte rápida
> dotnet test --filter "Category=EndToEnd"      # só os de ponta a ponta
> ```

#### Os tempos precisam fechar

```
preStop (5s) + Host:ShutdownTimeoutSeconds (30s) < terminationGracePeriodSeconds (45s)
```

Se a soma passar do grace period, o orquestrador manda `SIGKILL` no meio da drenagem e **todo o mecanismo é perdido** — requisição em andamento morre, job do Hangfire morre no meio. O `preStop` existe para cobrir a propagação da remoção do endpoint.

| Configuração | Default | Para quê |
|---|---|---|
| `Host:ShutdownTimeoutSeconds` | `30` | quanto a aplicação tem para drenar depois do `SIGTERM` |

### Observabilidade: três sinais, não um

O Serilog cobre **log**. Ele responde *o que aconteceu*. Não responde *onde foi o tempo* — e num
incidente de latência é essa a pergunta.

O template exporta os três sinais por **OpenTelemetry**, que é vendor-neutral: o mesmo código vai
para Grafana Tempo, Jaeger ou Datadog trocando uma variável.

| Sinal | Quem produz | Responde |
|---|---|---|
| Log | Serilog → Loki | o que aconteceu |
| Traço | OTel (ASP.NET, HttpClient, Npgsql) | **onde foi o tempo** |
| Métrica | OTel (RED por endpoint, runtime, pool do Npgsql) | como está agora |

> **O `traceId` da resposta é o do traço.** Todo envelope e todo ProblemDetails carrega um
> `traceId` no formato W3C (32 hex) — o **mesmo** que vai para o Tempo. O cliente reporta esse
> valor e o operador acha exatamente aquele request. É o que fecha o ciclo de um incidente.

> **As probes não são instrumentadas.** O kubelet chama o readiness de cada réplica a cada poucos
> segundos: instrumentá-las faria delas a maioria esmagadora dos traços, afogando os requests
> reais e inflando a conta do backend.

| Configuração | Default | Para quê |
|---|---|---|
| `Observability:Enabled` | `true` | liga a instrumentação |
| `Observability:OtlpEndpoint` | `""` | para onde exportar. **Vazio = não exporta** |
| `Observability:SamplingRatio` | `1.0` | fração amostrada. Em produção sob volume, baixe para `0.1` |

> **Aqui a falha é ABERTA — a única exceção à regra 9.** Sem `OtlpEndpoint`, a aplicação sobe e
> não exporta. Derrubar a API porque o collector saiu do ar seria trocar um problema de
> observabilidade por uma indisponibilidade. (O Loki é diferente e continua falhando fechado: lá
> alguém pediu log com `Enabled: true`, e o modo de falha era pior — a aplicação subia *parecendo*
> ter observabilidade, e o Loki ficava vazio.)

### O outbox precisa ser vigiado

O drenador engole exceções de propósito — uma mensagem envenenada não pode derrubar o lote. O
preço é que uma mensagem que **esgota as tentativas** sai do `WHERE` do drenador e **some**: fica
um `Error` no Loki e nada mais. O evento nunca vai acontecer, e nada avisa.

O health check `outbox` (visível em `/health/detail`) fecha esse buraco:

| Estado | Quando | O que significa |
|---|---|---|
| `Healthy` | fila andando | normal |
| `Degraded` | pendente há mais de `StaleAfterMinutes` | a fila parou de andar — investigue |
| `Unhealthy` | há mensagem que esgotou as tentativas | **evento perdido** |

> **Ele não entra no readiness, de propósito.** Um outbox atrasado não impede servir HTTP —
> e como todas as réplicas compartilham a mesma fila, elas sairiam do balanceamento *todas
> juntas*. O alerta sai do `/health/detail`, não do balanceador.

> ### ⚠️ `BatchSize` é um teto de throughput
>
> Com o job de minuto em minuto, o máximo é `BatchSize × 60` mensagens por hora — **6.000/h** no
> default. Acima disso a fila cresce sem limite. O sintoma é a idade da mensagem mais antiga
> subindo, que é justamente o que o health check observa.

| Configuração | Default | Para quê |
|---|---|---|
| `Outbox:BatchSize` | `100` | mensagens por rodada (ver o teto acima) |
| `Outbox:StaleAfterMinutes` | `15` | a partir de quando uma pendente indica fila travada |

### Resiliência a failover do banco

O `DbContext` liga `EnableRetryOnFailure`. Em Postgres local isso parece supérfluo; em nuvem gerenciada (Cloud SQL, RDS, Aurora) **não é**: um failover derruba as conexões por 5 a 30 segundos, e isso é rotina — acontece em manutenção programada do provedor. Sem retry, toda manutenção vira uma janela de 500.

> ### ⚠️ Retry ligado **proíbe** transação explícita
>
> Com `EnableRetryOnFailure`, o EF recusa `BeginTransaction` — ele não sabe reexecutar um bloco aberto à mão. Quem precisa de transação tem de pedir a *execution strategy*:
>
> ```csharp
> var strategy = db.Database.CreateExecutionStrategy();
> await strategy.ExecuteAsync(() => /* transação aqui */);
> ```
>
> O erro, se você esquecer, é claro — mas só aparece em **runtime**, quando aquele caminho executa. O `OutboxProcessor` é o único lugar do template que abre transação, e ele já faz isso certo.
>
> E há um efeito colateral não óbvio: numa reexecução, as entidades da tentativa anterior continuam no `ChangeTracker`. Sem um `ChangeTracker.Clear()` no início do bloco, um contador incrementado na tentativa que falhou seria somado de novo. Ver `OutboxProcessor.ProcessBatchAsync`.

| Configuração | Default | Para quê |
|---|---|---|
| `Database:MaxRetryCount` | `3` | tentativas em falha transitória |
| `Database:MaxRetryDelaySeconds` | `5` | teto do backoff exponencial |

---

## Desenvolvimento local

Requer Docker (Postgres, Redis, Loki, Grafana) e o .NET 10 SDK.

```bash
dotnet build                              # warnings são erros
dotnet test                               # sobe Postgres/Redis efêmeros via Testcontainers

MIGRATE_ONLY=true dotnet run --project src/Garius.Api    # bootstrap do banco
dotnet run --project src/Garius.Api                      # sobe a API
```

Um boot saudável termina assim — **duas** linhas:

```
[15:34:50 INF] Iniciando Garius.Api em Development
[15:34:50 INF] Escutando em http://localhost:5226
```

> **A segunda linha existe por um motivo.** O ASP.NET Core já loga `Now listening on: ...`, mas
> sob `Microsoft.Hosting.Lifetime` — e o filtro do Serilog põe `Microsoft` em `Warning` (é a
> regra dos *logs limpos*, e ela é deliberada). Sem esta linha, o boot termina em `Iniciando` e
> **mais nada**: a aplicação está no ar, escutando, e *parece travada*. A porta é a do
> `launchSettings.json` (**5226**), não a 5000.

| Endpoint | Para quê |
|---|---|
| `/` | ping |
| `/health/startup` | já terminou de subir? (startup probe) |
| `/health/live` | o processo está vivo (não toca em dependências) |
| `/health/ready` | pronto para tráfego (checa Postgres e Redis, e sabe se está encerrando) |
| `/health/detail` | diagnóstico — exige `X-Health-Key`; em produção, sem chave configurada, **não existe** |

Os testes do Secret Manager rodam contra o **GCP real** e são pulados automaticamente se não houver `GOOGLE_APPLICATION_CREDENTIALS` na máquina.
