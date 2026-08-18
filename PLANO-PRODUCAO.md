# Plano: do template atual ao padrão de produção em k8s

Este documento é o plano de implementação dos 8 pontos levantados na auditoria de
prontidão para produção pública em alta escala e alta disponibilidade.

**Estado da base:** boa. O template já acerta o que a maioria erra — falha fechada em toda
configuração, permissões fora do cookie, rate limit e cache no Redis (não em memória),
outbox transacional com `FOR UPDATE SKIP LOCKED`, PII como tipo, e um pipeline cuja ordem
é justificada linha a linha.

**O que falta é de uma família só:** comportamento sob orquestrador, réplicas e carga real.
Nada aqui é reescrita. São lacunas pontuais, e várias delas hoje causam perda de request
silenciosa.

---

## Ordem e dependências

A ordem não é arbitrária. A Fase 1 conserta perda de request que acontece em todo deploy;
a Fase 2 dá os olhos para verificar as fases seguintes; a Fase 3 é maturidade incremental.

```
Fase 1 (obrigatória, nesta ordem)
  1. Graceful shutdown + probes k8s     ← maior impacto; perda de request hoje
  2. Resiliência de conexão (EF retry)  ← failover de banco é rotina em nuvem

Fase 2 (observabilidade — habilita verificar tudo que vem depois)
  3. OpenTelemetry (traces + métricas)
  4. Saúde do outbox                    ← depende de 3 para a métrica

Fase 3 (maturidade)
  5. Manifestos k8s / Helm              ← consolida 1 e 2
  6. Dockerfile hardening + supply chain
  7. Rate limit por identidade
  8. Pool de conexões: fórmula e teto
```

O item 5 depende de 1 (as probes precisam existir antes do manifesto que as usa). O item 4
depende de 3 (a métrica precisa de um exportador). Os demais são independentes.

---

## Nota de método — vale para todos os itens

O `CLAUDE.md` exige teste com dentes: **neutralize a defesa e confirme que o teste falha.**
Isso é fácil em alguns itens (retry, headers) e caro em outros.

Seja honesto sobre o custo antes de começar:

| Item | Custo do teste | Observação |
|---|---|---|
| 1 Shutdown | **Alto** | Exige carga + `SIGTERM` + verificar zero erro. Infraestrutura de teste que não existe hoje. |
| 2 EF retry | Médio | Testcontainers permite derrubar/subir o Postgres no meio. |
| 3 OTel | Baixo | Exportador em memória, asserção sobre as activities. |
| 4 Outbox | Baixo | Já há `DatabaseFixture`; inserir mensagem velha e checar o health. |
| 5 k8s | Médio | Validação de manifesto + `kind`/`k3d` no CI, opcional. |
| 6 Docker | Baixo | Build da imagem no CI já prova. |
| 7 Rate limit | Baixo | Padrão dos testes de rate limit já existentes. |
| 8 Pool | Baixo | Teste de configuração; o boot avisa. |

Toda classe de teste nova que use `ApiFactory` **precisa** de `[Collection(ApiCollection.Name)]`
— sem isso o paralelismo do xUnit quebra a suíte de formas intermitentes.

E, ao final de cada fase: `dotnet clean && dotnet build && dotnet test`. O `clean` não é
opcional.

---

# FASE 1 — Obrigatória antes de chamar de pronto

## 1. Graceful shutdown + probes de k8s

### O problema

Não há `ShutdownTimeout` nem configuração de `HostOptions` no projeto. Mais importante que a
duração é a **sequência**: quando o k8s envia `SIGTERM`, ele em paralelo remove o endpoint do
Service. A remoção propaga por kube-proxy/ingress e leva de 1 a alguns segundos. Nessa janela
o pod já parou de aceitar conexões, mas o balanceador ainda manda tráfego — **cada request
nessa janela vira erro de conexão para um cliente real.**

Isso acontece em todo deploy, todo scale-down do HPA e toda migração de nó.

O `/health/ready` atual (`HealthSetup.cs:81`) não sabe nada sobre shutdown: ele checa Postgres
e Redis e mais nada. Precisa passar a falhar **imediatamente** no `SIGTERM`, enquanto o
processo continua servindo o que já está em voo.

Some o Hangfire: jobs em execução precisam de tempo para terminar ou voltar à fila. Sem
`ShutdownTimeout` alinhado ao `terminationGracePeriodSeconds`, o pod é morto no meio de um job.

### O que fazer

**a) Um `ShutdownState` singleton**, registrado no `IHostApplicationLifetime.ApplicationStopping`,
que vira uma flag `IsShuttingDown`.

**b) Um health check `"shutdown"`** com a tag `ready`, que retorna `Unhealthy` assim que a flag
liga. Assim o `/health/ready` reprova no instante do `SIGTERM`, sem depender de dependência
externa nenhuma.

**c) `ShutdownTimeout` explícito**, configurável, default 30s:

```csharp
builder.Services.Configure<HostOptions>(o =>
{
    o.ShutdownTimeout = TimeSpan.FromSeconds(30);
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
});
```

**d) Um startup probe** (`/health/startup`), hoje inexistente. Sem ele o liveness pode matar um
pod que ainda está subindo — e o boot deste template não é instantâneo (Secret Manager,
conexão ao Redis com `AbortOnConnectFail`, EF).

**e) A relação de tempos precisa fechar**, e é isso que costuma ser errado:

```
preStop sleep (5s)  +  ShutdownTimeout (30s)  <  terminationGracePeriodSeconds (45s)
```

Se o `terminationGracePeriodSeconds` for menor que a soma, o k8s manda `SIGKILL` no meio da
drenagem e o esforço todo é perdido. Documentar isso junto ao manifesto (item 5).

### Arquivos

- `src/Garius.Api/Infrastructure/Health/ShutdownState.cs` (novo)
- `src/Garius.Api/Infrastructure/Health/HealthSetup.cs` — novo check, novo endpoint `/health/startup`
- `src/Garius.Api/Program.cs` — `HostOptions`, registro do `ShutdownState`

### Teste com dentes

O caro. O teste honesto dispara carga contínua contra a `ApiFactory`, sinaliza
`ApplicationStopping`, e afirma **zero respostas com erro** enquanto o `/health/ready` já
reprova. Neutralizar = remover o check de shutdown; o teste tem de passar a acusar request
perdido.

Se o teste completo ficar caro demais, o mínimo aceitável é: `/health/ready` reprova após
`ApplicationStopping`, e `/health/live` continua aprovando. Isso já é verificável e já protege
a regressão mais provável.

---

## 2. Resiliência de conexão no EF Core

### O problema

`PersistenceExtensions.cs:111` configura só o `MigrationsAssembly`. Não há
`EnableRetryOnFailure`.

Em VPS com Postgres local isso passa despercebido. Em nuvem gerenciada, não: failover de
Cloud SQL / RDS / Aurora derruba conexões por 5–30 segundos, e é **esperado** que a aplicação
retente. Sem isso, toda manutenção programada do provedor vira janela de 500.

### A armadilha

Ligar o retry sem mais nada **quebra o `OutboxProcessor` em runtime**. O EF proíbe transação
explícita sob execution strategy, porque não sabe reexecutar o bloco inteiro — e o outbox abre
transação explícita em `OutboxProcessor.cs:43`.

O erro é claro (`InvalidOperationException: The configured execution strategy
'NpgsqlRetryingExecutionStrategy' does not support user-initiated transactions`), mas só
aparece quando o job roda. Se essa mudança for feita sem tocar no outbox, a suíte pode passar
e o drain quebrar em produção.

### O que fazer

```csharp
builder.UseNpgsql(connectionString, npgsql =>
{
    npgsql.MigrationsAssembly(...);
    npgsql.EnableRetryOnFailure(
        maxRetryCount: 3,
        maxRetryDelay: TimeSpan.FromSeconds(5),
        errorCodesToAdd: null);
});
```

E, no `OutboxProcessor`, envolver a rodada na execution strategy:

```csharp
var strategy = db.Database.CreateExecutionStrategy();
await strategy.ExecuteAsync(async () => { /* transação + lote */ });
```

Cuidado ao reexecutar: o corpo precisa ser idempotente. Como o lote é relido a cada tentativa
(o `FOR UPDATE SKIP LOCKED` roda de novo) e o commit é atômico, isso se sustenta — mas o
`message.Attempts++` só conta se a transação commitar, o que é o comportamento correto.

Verificar também se há outras transações explícitas no código (auth/refresh) que precisem do
mesmo tratamento.

### Arquivos

- `src/Garius.Infrastructure/Database/PersistenceExtensions.cs`
- `src/Garius.Infrastructure/Messaging/OutboxProcessor.cs`
- `src/Garius.Infrastructure/Database/DatabaseOptions.cs` — expor `MaxRetryCount`

### Teste com dentes

Com Testcontainers: pausar o container do Postgres no meio de uma operação, despausar, e
afirmar que a operação concluiu. Neutralizar o retry = a operação falha.

Mais barato e igualmente valioso: um teste que roda o `OutboxProcessor` **com o retry ligado** e
prova que ele não lança a exceção da execution strategy. Esse teste é o que impede a regressão
mais provável.

---

# FASE 2 — Observabilidade

## 3. OpenTelemetry

### O problema

Há Serilog com logs limpos, o que é bom. Mas **log não é traço nem métrica.**

Em produção distribuída a pergunta é "por que este request demorou 3 segundos?", e a resposta
está na decomposição: 40ms na API, 2.9s numa query, 60ms no Redis. Sem tracing, isso é
adivinhação. Não há métricas RED (rate / errors / duration) por endpoint.

### O que fazer

OTel é o padrão de fato e é vendor-neutral — o mesmo código exporta para Tempo, Jaeger,
Datadog. Com Grafana já no stack, o Tempo entra natural.

Pacotes (`Directory.Packages.props`, no grupo Observabilidade):

```
OpenTelemetry.Extensions.Hosting
OpenTelemetry.Instrumentation.AspNetCore
OpenTelemetry.Instrumentation.Http
OpenTelemetry.Instrumentation.Runtime
Npgsql.OpenTelemetry
OpenTelemetry.Exporter.OpenTelemetryProtocol
```

Um `AddObservability(configuration)` novo em `Infrastructure/Observability/`, com:

- **Traces**: ASP.NET Core + HttpClient + Npgsql + StackExchange.Redis.
- **Métricas**: as do runtime (GC, thread pool) e as do Kestrel/ASP.NET.
- **Sampling**: `ParentBased(TraceIdRatioBased)`, com taxa configurável. Em alta escala,
  amostrar 100% é caro; default 10% em produção, 100% em dev.
- **Exportador OTLP**, endpoint por configuração. **Se não configurado, não exporta** — e não
  falha (diferente do resto do template, aqui degradar é correto: telemetria ausente não
  justifica derrubar a API).

**Correlação com o Serilog:** o `traceId` do contrato de resposta precisa ser o mesmo
`TraceId` do OTel. Hoje o `traceId` vem do `context.TraceIdentifier`. Ligar os dois é o que
faz o cliente conseguir reportar um id que o operador encontra no Tempo — e é a parte que
mais rende dessa fase. Verificar o enricher do Serilog para incluir `TraceId`/`SpanId`.

### Cuidado

Não instrumentar cegamente: os endpoints de health seriam ~90% dos traços. Filtrar
`/health/*` do tracing.

### Arquivos

- `src/Garius.Infrastructure/Observability/ObservabilitySetup.cs` (novo)
- `Directory.Packages.props`
- `src/Garius.Api/Program.cs`
- `src/Garius.Api/Infrastructure/Logging/` — correlação traceId

### Teste com dentes

Exportador em memória; disparar um request e afirmar que existe uma activity para o endpoint,
uma filha para a query do Npgsql, e que `/health/live` **não** gera traço.

---

## 4. Saúde do outbox

### O problema

O `OutboxProcessor` engole exceções por design, e isso está certo — uma mensagem envenenada
não pode derrubar o lote (`OutboxProcessor.cs:113-132`). Mas quando uma mensagem atinge
`MaxAttempts`, ela **sai do `WHERE` da query** (`OutboxProcessor.cs:54`) e desaparece
silenciosamente. Fica um `LogLevel.Error` no Loki e nada mais.

Ninguém é notificado. A mensagem morreu e o sistema segue como se nada fosse.

Há também um teto de throughput não documentado: lote de 100 a cada minuto = **6.000
mensagens/hora**. Sob carga de alta escala a fila cresce sem limite, e nada avisa.

### O que fazer

**a) Health check `outbox`**, com a tag `ready` **desligada** — ele não deve tirar o pod do
balanceamento (o outbox atrasado não impede servir HTTP). Tag própria, exposta em
`/health/detail`:

- `Healthy`: mensagem pendente mais antiga com menos de N minutos.
- `Degraded`: acima do limiar.
- `Unhealthy`: há mensagens mortas (`Attempts >= MaxAttempts`).

**b) Métricas** (via OTel, item 3):

- `outbox.pending` — gauge.
- `outbox.oldest_age_seconds` — **a métrica que importa**; é o indicador antecedente de
  travamento.
- `outbox.dead` — counter.
- `outbox.processed` / `outbox.failed`.

**c) Consultar o teto de throughput.** Tornar `BatchSize` e o intervalo configuráveis, e
documentar a conta (`BatchSize × 60/intervalo = mensagens/hora`) no README. Considerar que o
job se reagende imediatamente quando drenou um lote cheio — sinal de que há fila.

### Arquivos

- `src/Garius.Infrastructure/Messaging/OutboxHealthCheck.cs` (novo)
- `src/Garius.Infrastructure/Messaging/OutboxProcessor.cs` — métricas, `BatchSize` configurável
- `src/Garius.Api/Infrastructure/Health/HealthSetup.cs`

### Teste com dentes

Inserir mensagem pendente com `CreatedAt` antigo → health `Degraded`. Inserir com
`Attempts = MaxAttempts` → `Unhealthy`. Neutralizar o check = ambos passam como `Healthy`.

---

# FASE 3 — Maturidade

## 5. Manifestos k8s / Helm

### O problema

O template assume Traefik + compose; os comentários do `HealthSetup` dizem isso
explicitamente. Não há `k8s/`, `helm/` nem `charts/`. Como k8s é alvo declarado, isso é uma
lacuna — e é onde metade dos problemas da Fase 1 efetivamente se resolve.

### O que fazer

Um chart Helm em `deploy/helm/`, com:

- **`Deployment`** — as três probes (`startup`, `liveness`, `readiness`) com timings coerentes,
  `preStop` sleep, `terminationGracePeriodSeconds` casado com o `ShutdownTimeout` do item 1.
- **`PodDisruptionBudget`** — `minAvailable`. Sem ele, drenar um nó pode derrubar todas as
  réplicas de uma vez. É barato e quase sempre esquecido.
- **`HorizontalPodAutoscaler`** — CPU + memória, com `behavior` de scale-down conservador
  (evita flapping).
- **`resources`** requests/limits — sem requests, o scheduler não tem o que respeitar e o HPA
  não funciona.
- **`Job` de migração como Helm hook** `pre-install,pre-upgrade` com
  `hook-delete-policy: before-hook-creation`. O `MIGRATE_ONLY` já está pronto para isso — só
  falta o manifesto. O hook **precisa** ter `backoffLimit` e falhar o deploy se a migração
  falhar.
- **`securityContext`** — `runAsNonRoot`, `readOnlyRootFilesystem`, `allowPrivilegeEscalation:
  false`, `capabilities.drop: [ALL]`, `seccompProfile: RuntimeDefault`.
- **`NetworkPolicy`** — default deny, liberando só Postgres, Redis e o ingress.
- **`topologySpreadConstraints`** — espalhar réplicas entre nós/zonas. Sem isso, o scheduler
  pode empilhar tudo num nó e a "alta disponibilidade" é ilusória.

**Atenção ao `readOnlyRootFilesystem`:** o .NET escreve em `/tmp`. Precisa de um `emptyDir`
montado ali, senão o pod não sobe. É a causa mais comum de falha ao ligar essa flag.

### Arquivos

- `deploy/helm/` (novo) — `Chart.yaml`, `values.yaml`, `templates/`
- `README.md` — seção de deploy em k8s

### Teste

`helm template` + `kubeconform` no CI, validando contra o schema da versão alvo. Se quiser
dentes de verdade: subir um `kind` no CI e rodar um smoke. Opcional — o valor/custo aqui é
discutível, e o `helm template` já pega a maioria dos erros.

---

## 6. Dockerfile: hardening e supply chain

### O problema

Três pontos no `Dockerfile` atual:

1. **Tags mutáveis** (`sdk:10.0`, `aspnet:10.0`). Build não reproduzível: dois builds do mesmo
   commit podem gerar imagens diferentes.
2. **Sem chiseled/distroless.** A imagem `aspnet` tem shell e gerenciador de pacotes — superfície
   desnecessária numa API pública.
3. **Sem SBOM nem assinatura.** Para produção séria hoje, é expectativa.

O `USER app` já está correto.

### O que fazer

**a) Pinar por digest:**

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:<digest> AS build
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:<digest> AS final
```

Digest fixo exige um processo de atualização — Renovate/Dependabot resolvem, e sem isso o pin
vira dívida (imagem velha sem patch de segurança). **Não pinar sem configurar a atualização
automática.**

**b) Chiseled.** Sem shell nem apt. Impacto a verificar antes: o `Serilog.Sinks.Grafana.Loki`
e o `Google.Cloud.SecretManager` precisam de certificados raiz — a imagem chiseled os tem, mas
convém confirmar no primeiro build. A variante `-extra` existe se faltar ICU/tzdata.

**c) SBOM + assinatura** no pipeline de release: `syft` para gerar, `cosign` para assinar.
Ligado ao `deploy.ps1`, que já centraliza a versão.

**d) Revisar o `.dockerignore`** — garantir que `bin/`, `obj/`, `.git/` e qualquer secret local
não entrem no contexto de build.

### Arquivos

- `Dockerfile`
- `.dockerignore`
- `deploy.ps1` — passos de SBOM e assinatura
- `renovate.json` ou `.github/dependabot.yml` (novo)

---

## 7. Rate limit por identidade

### O problema

O rate limit particiona só por IP (`RateLimitMiddleware.cs:57-69`), e está bem posicionado no
pipeline (antes da autenticação, para não pagar PBKDF2 num brute force — decisão correta).

Mas para API pública falta a segunda dimensão. Um cliente legítimo atrás de CGNAT compartilha
IP com milhares de pessoas; um atacante com um /64 de IPv6 tem endereços de sobra. Limite só
por IP pune o primeiro e não contém o segundo.

**Correção ao levantamento inicial:** o `Retry-After` **já existe** e está correto — o script Lua
calcula `retryAfterMs` (`RedisRateLimiter.cs:61-67`) e o middleware o emite
(`RateLimitMiddleware.cs:102`). O que falta são os headers `RateLimit-*` da RFC 9331.

### O que fazer

**a) Partição por identidade quando houver uma.** A dificuldade é de ordem no pipeline: o rate
limit roda **antes** da autenticação, de propósito. A solução é **duas camadas**:

- A atual, por IP, cedo e barata (mantida como está).
- Uma segunda, **depois do `UseAuthorization`**, particionada por `sub`/`client_id`/api-key,
  com limites por identidade e cota por plano. Essa pode custar mais, porque o request já
  passou pela autenticação.

Isso encaixa naturalmente com as três formas de credencial que o template já tem (cookie,
Bearer M2M, `X-Api-Key`) — e a API key, em particular, é onde a cota por cliente é mais
necessária.

**b) Headers `RateLimit-*` (RFC 9331)** em **toda** resposta, não só no 429:

```
RateLimit-Limit: 100
RateLimit-Remaining: 42
RateLimit-Reset: 30
```

Sem eles, o cliente só descobre o limite batendo nele.

**c) Considerar isenção para tráfego interno** (health checks, probes) — hoje o `/health/*`
consome cota do IP do kubelet.

### Arquivos

- `src/Garius.Api/Infrastructure/RateLimiting/RateLimitMiddleware.cs`
- `src/Garius.Api/Infrastructure/RateLimiting/IdentityRateLimitMiddleware.cs` (novo)
- `src/Garius.Infrastructure/RateLimiting/RateLimitOptions.cs`
- `src/Garius.Api/Program.cs` — segunda camada após o `UseAuthorization`

### Teste com dentes

Dois IPs diferentes com a mesma identidade batem no mesmo limite. Duas identidades no mesmo IP
não se afetam na camada de identidade. Headers presentes em resposta de sucesso.

---

## 8. Pool de conexões: fórmula e teto

### O problema

`MaxPoolSize = 20` (`DatabaseOptions.cs:47`). O número por pod é razoável; o problema é que
**ninguém multiplica**.

Com 10 réplicas: 200 conexões, mais o Hangfire (até 8 workers com pool próprio), mais os
health checks, mais o container de migração. O default do `max_connections` do Postgres é 100.

Isso não falha em teste nem em staging com uma réplica. Falha quando o HPA escala sob pico —
exatamente no pior momento, e o sintoma (`too many clients already`) chega junto com o
incidente que causou o pico.

### O que fazer

**a) Documentar a fórmula** no README e no `DatabaseOptions`:

```
conexões = réplicas × (MaxPoolSize + pool do Hangfire) + migração + folga
```

**b) Aviso no boot.** Duas configurações novas — `ExpectedReplicas` e `PostgresMaxConnections`
— e um log `Warning` (não falha: o template falha fechado em *configuração inválida*, e isto é
um alerta de capacidade, não um erro) quando o produto passar de ~70% do teto.

**c) Documentar pgBouncer/PgCat.** Em nuvem, pooler externo é o padrão acima de poucas
réplicas. Vale uma seção no README com a ressalva importante: **em modo `transaction` o pooler
quebra prepared statements** — com Npgsql isso exige `Max Auto Prepare=0` ou
`No Reset On Close`. É pegadinha conhecida e cara.

**d) Reavaliar o `MaxPoolSize` default.** Para um pod pequeno, 20 pode ser generoso demais;
menos conexões e mais pods costuma ser melhor. Sugerir 10 como default e documentar o porquê.

### Arquivos

- `src/Garius.Infrastructure/Database/DatabaseOptions.cs`
- `src/Garius.Infrastructure/Database/PersistenceExtensions.cs` — aviso no boot
- `README.md`

### Teste

Configuração que estoura o teto → log de `Warning` presente. Barato e suficiente.

---

## O que este plano deliberadamente NÃO muda

Registrado para que não vire discussão de novo:

- **Modular monolith com vertical slices.** Correto para o alvo; escala bem. Sem MediatR, sem
  AutoMapper, sem repositório genérico.
- **Hangfire.** Analisado e mantido. O uso é um job recorrente; o acoplamento com o dashboard,
  o banco separado e a ponte de permissões já está testado. TickerQ não resolve problema que
  exista aqui. Se o volume um dia justificar sair, o destino é uma fila de verdade
  (Pub/Sub, SQS), não outro agendador em banco.
- **Permissões fora do cookie, resolvidas no Redis.** Certo, com teste de invalidação entre
  réplicas.
- **Ordem do pipeline.** Rate limit antes da autenticação é uma decisão que muita gente erra;
  aqui está certa e justificada. O item 7 **acrescenta** uma camada, não move a existente.
- **Falha fechada em toda configuração.** É o que separa template sério de scaffold.
- **Uma imagem, dois modos (`MIGRATE_ONLY`).** Elegante e correto; o item 5 o aproveita.
- **Fail-open do rate limit quando o Redis cai.** Está justificado no código e a justificativa
  se sustenta.

---

## Resumo

| # | Item | Fase | Impacto | Custo |
|---|---|---|---|---|
| 1 | Graceful shutdown + probes | 1 | **Alto** — perda de request em todo deploy | Alto (teste) |
| 2 | EF retry + execution strategy | 1 | **Alto** — failover é rotina em nuvem | Médio |
| 3 | OpenTelemetry | 2 | **Alto** — sem isso, o resto é diagnosticado no escuro | Médio |
| 4 | Saúde do outbox | 2 | Médio — falha silenciosa hoje | Baixo |
| 5 | Manifestos k8s / Helm | 3 | Médio — consolida 1 e 2 | Médio |
| 6 | Docker hardening + SBOM | 3 | Médio — supply chain | Baixo |
| 7 | Rate limit por identidade | 3 | Médio — completa a defesa | Baixo |
| 8 | Pool: fórmula e teto | 3 | Baixo — mas o sintoma chega no pico | Baixo |

**Itens 1 a 4 são o corte para "pronto para produção pública em k8s".** Do 5 em diante é
maturidade incremental, entregável em qualquer ordem.
