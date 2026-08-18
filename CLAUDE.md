# Instruções para IAs neste repositório

Este arquivo é carregado **automaticamente** em toda sessão. Ele é curto de propósito: a
documentação de verdade está no **[README.md](README.md)**, e este arquivo existe para garantir
que você a leia *antes* de escrever a primeira linha de código — não depois.

## ⚠️ Antes de tudo: você está no TEMPLATE ou numa APLICAÇÃO DERIVADA?

A resposta muda o que você deve fazer, e confundir as duas é o atrito mais comum aqui.

**Como saber:** rode `dotnet test --filter TemplateDerivation`.

- Os testes **pularam** (`Skipped`) → você está **no TEMPLATE**. Não escreva features de negócio
  aqui: derive primeiro (ver o fim deste arquivo).
- Os testes **rodaram** → você está numa **APLICAÇÃO DERIVADA**.

> ⚠️ **Não tente descobrir isso comparando o nome dos projetos com um valor escrito aqui.** Este
> arquivo passa pelo substituidor do `dotnet new`: qualquer nome do template citado no texto vira o
> nome da aplicação derivada, e a frase se inverte sozinha — passando a afirmar que a app derivada
> "é o template". O guarda em `TemplateDerivationTests` existe justamente porque essa armadilha já
> desativou um teste em silêncio.

Numa **aplicação derivada**, então:

> **O código que veio do template é uma BASE, não um limite.** Escrever features novas — inclusive
> features que **substituem** ou **estendem** o que veio pronto — é exatamente o uso esperado. As
> regras abaixo dizem *como* construir, não *o que* é permitido construir.

Se o usuário pedir algo que o template não tem, **a resposta certa é implementar**, respeitando as
regras de contrato. Não é dizer que "está fora das regras".

**E leia o [REGRAS-DA-APLICACAO.md](REGRAS-DA-APLICACAO.md) antes deste arquivo.** É lá que moram
as regras DESTA aplicação — autenticação real, papéis, domínio, integrações. Em caso de conflito,
**ele vence** este arquivo em tudo que for regra de negócio; as 10 regras de contrato continuam
valendo em ambos.

Se ele ainda estiver com os comentários de exemplo (`<!-- ... -->`), a aplicação ainda não
registrou as próprias regras: **ajude o usuário a preenchê-lo** na primeira conversa, em vez de
tratar o template como se fosse a especificação dela.

## Duas categorias, e não confunda uma com a outra

### 1. CONTRATO — vale para sempre, em qualquer aplicação derivada

São as 10 regras da próxima seção. Elas existem porque cada uma já custou caro em produção.
Violá-las quebra segurança ou o contrato com o frontend.

### 2. ANDAIME — veio pronto para você ESTENDER ou TROCAR

Isto é ponto de partida, não lei. Mexer aqui é o uso normal do template:

| O que veio pronto | O que a app derivada normalmente faz |
|---|---|
| Login por e-mail/senha (`AuthService`) | **adiciona** SSO/Google/Entra, ou troca por eles |
| Permissões de exemplo (`Catalog.*`) | apaga e declara as do próprio domínio |
| `ApplicationUser` e o modelo de tenant | acrescenta campos, vínculos e regras próprias |
| Endpoints `/__test` | some em produção; some do repositório quando não servir mais |
| Páginas admin (`/jobs`, `/scalar`) | mantém, restringe ou remove |
| O CRUD de usuários e papéis | **não existe** — a app derivada escreve o dela |

**Exemplo concreto, porque foi um atrito real:** integrar login com o Google **não viola nenhuma**
das 10 regras. É `AddAuthentication().AddGoogle(...)`, um `ExternalLoginInfo` virando o mesmo
principal com claims de `permission`, e o resto do pipeline intacto. Os dois métodos (senha local e
Google) podem e devem coexistir. Recusar isso citando o README é ler o template como uma jaula.

O que continua valendo ao adicionar um provedor externo: o e-mail é `Pii` (regra 7); a autorização é
por permissão, não por papel do Google (regra 8); a resposta segue o contrato (regra 1); e o segredo
do cliente OAuth vem do Secret Manager (regra 5).

### Sobre papéis (roles) — a regra 8 é mais estreita do que parece

Ela proíbe **autorizar** por papel (`[Authorize(Roles = "Admin")]`, `RequireRole(...)`). Ela **não**
proíbe a app derivada ter papéis como **agrupamento** de permissões: um papel "Gestor" que concede
`invoices.approve` e `invoices.read` é desenho normal e bem-vindo. O endpoint continua exigindo a
**permissão**; o papel é só como ela foi concedida.

## Leia isto primeiro

**Antes da primeira edição**, leia o README.md inteiro. Não é opcional e não é retórica: ele tem
~1000 linhas, e cada decisão vem com o *porquê*. Se algo no código parecer redundante, ineficiente
ou paranoico, **a explicação provavelmente está lá** — e quase sempre é uma armadilha que já custou
caro em produção. Remover uma proteção que você não entendeu é o erro mais caro que você pode
cometer aqui.

> ⚠️ **O README descreve o que EXISTE, não o que é PERMITIDO existir.** Ele é um mapa da base, não
> uma lista fechada de funcionalidades. Se o usuário pede algo que não está lá, isso não é violação
> — é a app derivada crescendo, que é para o que ela serve. Use o README para saber *como* encaixar
> a coisa nova (qual contrato de resposta, onde entra no pipeline, o que precisa ser `Pii`), não
> para decidir *se* ela pode existir.

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

## O que SEMPRE pode (numa aplicação derivada)

Esta lista existe porque a de baixo, sozinha, faz o template parecer uma jaula. Nada aqui precisa
de permissão, discussão ou aviso — é o trabalho normal:

- **Adicionar provedores de autenticação** (Google, Microsoft Entra, SSO corporativo), convivendo
  com o login local ou substituindo-o.
- **Criar entidades, endpoints, services, migrations e permissões** do domínio da aplicação.
- **Estender `ApplicationUser`, tenants e o modelo de dados** com o que o negócio pedir.
- **Apagar o que veio de exemplo** — as permissões `Catalog.*`, os endpoints `/__test`, o catálogo
  de demonstração.
- **Escrever as telas de administração** de usuários e papéis, que o template deliberadamente não
  traz.
- **Trocar decisões de infraestrutura** (outro provedor de secrets, outro sink de log, outro
  agendador) quando houver motivo — desde que a substituição mantenha a garantia original.

A régua é simples: **as 10 regras dizem COMO construir; elas não limitam O QUE construir.**

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

> ⚠️ **Esta seção só vale se você estiver NO TEMPLATE** — veja no topo do arquivo como confirmar
> (`dotnet test --filter TemplateDerivation`). Numa aplicação derivada, ignore-a: você já derivou,
> e escrever features é exatamente o que se espera.
>
> (O texto que ficava aqui comparava o nome dos projetos com o nome do template. Não funcionava:
> o `dotnet new` substituía esse nome, e a frase passava a dizer à aplicação derivada que ela
> "é o template" e não devia escrever features — o oposto do pretendido. Foi encontrado derivando
> uma app de verdade e lendo o resultado.)

Para derivar, use o `dotnet new` (não copie a pasta à mão, e não peça para uma IA renomear tudo):

```bash
dotnet new install .
dotnet new garius-api -n Tcm.SfcHortolandia.Api -o ../Tcm.SfcHortolandia.Api
```

**O nome que você passa é o namespace raiz.** Os projetos viram `Tcm.SfcHortolandia.Api`, `.Core`,
`.Infrastructure` e `.Tests`. Isso renomeia também o `InternalsVisibleTo`, o filtro de log do
Serilog, o `UserSecretsId` **e** o `Database:ApplicationName` — que é o que impede duas aplicações
de colidirem no mesmo banco e no mesmo usuário do Postgres.

> Há um teste (`TemplateDerivationTests`) que **falha** se o `ApplicationName` ainda for o do
> template. Ele existe porque essa colisão é silenciosa: a app sobe, funciona, e só atropela a
> outra em produção.
