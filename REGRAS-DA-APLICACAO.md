# Regras desta aplicação

> **Este arquivo nasce vazio de propósito, e é o mais importante da sua aplicação derivada.**
>
> O `CLAUDE.md` e o `README.md` descrevem a **base** — o contrato de resposta, a criptografia de
> PII, o modelo de permissões. Eles não sabem nada sobre o **seu** negócio.
>
> É aqui que entram as regras da sua aplicação: como se autentica de verdade, quais são os papéis,
> o que o domínio significa. Enquanto isto estiver vazio, uma IA só tem o template para consultar —
> e vai responder sobre o template, que é exatamente o atrito que este arquivo existe para evitar.
>
> **Preencha na primeira conversa.** Apague as instruções entre `<!-- -->` conforme for escrevendo.

---

## O que esta aplicação é

<!--
Duas ou três frases. Quem usa, para quê, e qual é o fluxo principal.

Exemplo: "Agendamento de docas para transportadoras. O transportador reserva uma janela de
descarga; o operador do armazém confirma ou remaneja. O fluxo crítico é a reserva não permitir
sobreposição na mesma doca."
-->

## Autenticação

<!--
COMO ALGUÉM ENTRA NESTA APLICAÇÃO. Ela pode ter mais de um caminho, e o template já suporta
cookie (pessoa), Bearer M2M (máquina) e X-Api-Key (terceiro).

Responda:
- Quais provedores? (senha local, Google, Entra, SSO do cliente...)
- Eles coexistem ou um substitui o outro?
- Quem pode se cadastrar sozinho, e quem precisa de convite/aprovação?

Exemplo: "Login por Google (Workspace do cliente) para funcionários; senha local só para o
superadministrador, como saída de emergência. Não há autocadastro: o acesso é criado por convite."

⚠️ Ao adicionar um provedor externo, o que continua valendo da base: o e-mail é `Pii`, a
autorização é por PERMISSÃO (não pelos grupos do provedor), e o segredo OAuth vem do Secret
Manager.
-->

## Papéis e permissões

<!--
As permissões desta aplicação (`docas.reservar`, `docas.aprovar`, ...) e, se houver, os papéis que
as agrupam.

⚠️ Papel é AGRUPAMENTO, nunca critério de autorização. O endpoint exige a PERMISSÃO
(`.RequirePermission(...)`); o papel é só como ela foi concedida. Ver a regra 8 do CLAUDE.md.

Exemplo:
| Papel | Permissões |
|---|---|
| Transportador | `docas.reservar`, `docas.ler` |
| Operador | `docas.*`, `relatorios.ler` |
-->

## Domínio: as entidades e as regras que não podem ser quebradas

<!--
As invariantes do negócio — o que precisa ser SEMPRE verdade, e o que acontece se for violado.
São elas que viram teste.

Exemplo:
- Uma doca não aceita duas reservas sobrepostas (constraint no banco, não só validação).
- Reserva cancelada com menos de 2h de antecedência gera penalidade.
- Transportador só enxerga as próprias reservas (filtro por tenant).
-->

## Integrações externas

<!--
Sistemas de fora com que esta aplicação fala: ERP, gateway de pagamento, e-mail, webhook.

Para cada um: o que acontece quando ele está FORA DO AR? (falha fechada? fila? degrada?)
Essa resposta é decisão de negócio, e a base não tem como adivinhá-la.
-->

## Decisões desta aplicação que divergem do template

<!--
Se você trocou algo que veio pronto, registre AQUI o quê e o porquê. Sem isso, a próxima pessoa
(ou IA) vai "consertar" de volta para o padrão do template.

Exemplo: "O login local foi REMOVIDO — só Google. O AuthService de senha continua no código
porque o superadministrador o usa no bootstrap."
-->

## Como verificar

O mesmo do template, e não é negociável:

```bash
dotnet clean && dotnet build && dotnet test
```

As regras de contrato do `CLAUDE.md` (envelope, `Result`, `Pii`, permissão, falha fechada) valem
para todo código novo desta aplicação. Elas dizem **como** construir — não limitam **o que**
construir.
