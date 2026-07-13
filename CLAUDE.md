# Instruções para IAs neste repositório

Este arquivo é carregado **automaticamente** em toda sessão. Ele é curto de propósito: a
documentação de verdade está no **[README.md](README.md)**, e este arquivo existe para garantir
que você a leia *antes* de escrever a primeira linha de código — não depois.

## Leia isto primeiro

**Antes da primeira edição**, leia o README.md inteiro. Não é opcional e não é retórica: ele tem
~1000 linhas, e cada decisão vem com o *porquê*. Se algo no código parecer redundante, ineficiente
ou paranoico, **a explicação provavelmente está lá** — e quase sempre é uma armadilha que já custou
caro em produção. Remover uma proteção que você não entendeu é o erro mais caro que você pode
cometer aqui.

As duas seções que você **não pode** pular:
- **Regras invioláveis** — violar qualquer uma quebra segurança ou o contrato com o frontend.
- **Como escrever código aqui** — o padrão de endpoint, service, entidade e teste.

## As regras, em uma linha cada

Estão detalhadas no README (com o porquê). Aqui só para você reconhecer quando estiver prestes a
violar uma:

1. **Toda resposta segue o contrato** — envelope no sucesso, ProblemDetails no erro. (Exceção única:
   `POST /auth/token`, que segue a RFC 6749.)
2. **Erro de negócio é `Result`, não exceção.**
3. **Nada interno vaza para o cliente** — nem stack trace, nem nome de tabela, nem connection string.
4. **IP real sempre por `GetClientIp()`** — nunca `RemoteIpAddress` cru.
5. **Segredo nunca no appsettings** — vem do Secret Manager ou de env var.
6. **Índice único é sempre parcial** (`WHERE "Enabled" = true`) — por causa do soft delete.
7. **Dado pessoal é `Pii`, nunca `string`.**
8. **Autorize por PERMISSÃO, nunca por papel** — `.RequirePermission(...)`, jamais `Roles = "Admin"`.
9. **Falha de configuração falha FECHADA** — a aplicação não sobe; nunca "degrada".
10. **A ordem do pipeline não se mexe** sem entender o efeito.

## O que NUNCA fazer aqui

- **Não adicione MediatR, AutoMapper, repositório genérico ou CQRS.** Este template é um modular
  monolith com vertical slices, deliberadamente. O alvo é um dev solo mantê-lo por anos.
- **Não exija papel** (`Roles = "..."`). Papel é configuração; permissão é código.
- **Não coloque permissões dentro do cookie.** Medido: 1000 permissões = cookie de 50 KB = 12× o
  limite do navegador, que o **descarta em silêncio** e o login para de funcionar sem nada no log.
- **Não use `IMemoryCache` para nada que precise ser invalidado.** Com N réplicas, a invalidação
  numa não alcança as outras. Use o Redis. (Foi um bug real: acesso revogado que continuava
  funcionando *de forma intermitente*.)
- **Não abra o dashboard do Hangfire** (`/jobs`) nem a documentação (`/scalar`). O primeiro é
  execução remota de código; o segundo é o mapa completo da API.
- **Não "conserte" o build removendo um warning-as-error.** O build é estrito de propósito.

## Como verificar o que você fez

```bash
dotnet clean && dotnet build && dotnet test
```

**O `clean` não é opcional.** Um build incremental **esconde** falhas de estado global — foi assim
que uma corrida entre containers de teste ficou latente por uma fase inteira. Se você não rodou
`clean`, você não verificou nada.

Os testes rodam contra **Postgres e Redis reais** (Testcontainers). Se algum falhar, **descubra o
porquê antes de escrever código novo** — a suíte estava verde.

### Teste de segurança tem de ter dentes

Se você escreveu um teste que protege uma defesa, **neutralize a defesa e confirme que o teste
falha**. Um teste que passa com e sem a defesa é decoração — e já aconteceu aqui: um teste de cache
passava mesmo com o bug presente, porque dava o resultado certo *pela razão errada*.

## Derivando uma nova aplicação a partir deste template

Se este repositório ainda se chama `Garius.*` e o `Database:ApplicationName` ainda é
`GariusTech.Backend.Template`, **você está no template, não numa aplicação derivada**. Não comece a
escrever features aqui.

Para derivar, use o `dotnet new` (não copie a pasta à mão, e não peça para uma IA renomear tudo):

```bash
dotnet new install .
dotnet new garius-api -n MinhaApp.Backend -o ../MinhaApp.Backend
```

Isso renomeia os projetos, os namespaces, o `InternalsVisibleTo`, o `MigrationsAssembly` **e** o
`Database:ApplicationName` — que é o que impede duas aplicações de colidirem no mesmo banco e no
mesmo usuário do Postgres.

> Há um teste (`TemplateDerivationTests`) que **falha** se o `ApplicationName` ainda for o do
> template. Ele existe porque essa colisão é silenciosa: a app sobe, funciona, e só atropela a
> outra em produção.
