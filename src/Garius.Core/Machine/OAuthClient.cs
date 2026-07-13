using Garius.Core.Entities;
using Garius.Core.Tenancy;

namespace Garius.Core.Machine;

/// <summary>
/// Um cliente <b>máquina a máquina</b> (OAuth2 <i>client credentials</i>): um sistema que se
/// autentica com <c>client_id</c> + <c>client_secret</c> e recebe um JWT de curta duração.
///
/// <para>
/// <b>Não é um usuário.</b> Não tem senha, não tem e-mail, não tem lockout, não faz login em
/// dois passos. É por isso que mora numa tabela própria e não como um <c>ApplicationUser</c>
/// disfarçado — o caminho que parece economizar código e depois obriga metade das colunas do
/// Identity a serem nulas, e o lockout a ser desligado com um <c>if</c>.
/// </para>
///
/// <para>
/// <b>Por que não Duende/IdentityServer.</b> A licença é paga e o produto é uma peça a mais
/// para manter e atualizar. Client credentials é o fluxo <b>mais simples</b> do OAuth2 — não
/// há redirect, não há consentimento, não há PKCE: é uma troca de segredo por token. Emiti-lo
/// aqui são ~50 linhas; adotar um servidor de identidade inteiro para isso é desproporcional.
/// </para>
///
/// <para>
/// <b>Escopos são permissões.</b> Um client não tem papéis: ele tem uma lista fixa de
/// permissões do mesmo catálogo (<c>Permissions</c>) que os usuários usam. Um endpoint declara
/// <c>.RequirePermission(Permissions.Invoices.Read)</c> e passa a servir usuário e máquina com
/// o mesmo código — sem um segundo vocabulário de escopos para manter em sincronia.
/// </para>
/// </summary>
public sealed class OAuthClient : BaseEntity, ITenantEntity
{
    /// <summary>
    /// O <c>client_id</c> público. Não é segredo: vai em logs, em configuração e na requisição.
    /// É o <see cref="SecretHash"/> que autentica.
    /// </summary>
    public required string ClientId { get; set; }

    /// <summary>
    /// Hash do <c>client_secret</c>.
    ///
    /// <para>
    /// O segredo em claro <b>só existe uma vez</b>: na resposta que cria o client. Não há como
    /// recuperá-lo depois — perdeu, gera outro. É a mesma disciplina de uma senha, e a razão
    /// pela qual um vazamento do banco não entrega credenciais utilizáveis.
    /// </para>
    ///
    /// <para>
    /// Hash rápido (SHA-256), não Argon2. O segredo é gerado por nós com 256 bits de entropia
    /// criptográfica — não é uma senha escolhida por humano, então não há dicionário nem força
    /// bruta viável. Um KDF lento seria pago a cada emissão de token sem ganho nenhum.
    /// </para>
    /// </summary>
    public required string SecretHash { get; set; }

    /// <summary>Para que serve este client. Aparece na tela de administração.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// As permissões deste client, do <b>mesmo catálogo</b> dos usuários
    /// (<c>invoices.read</c>). Validadas contra <c>Permissions.Exists</c> na criação — um
    /// escopo com erro de digitação nunca chega ao banco.
    ///
    /// <para>
    /// Vão para dentro do JWT, como claims de permissão. Diferente do cookie de usuário (onde
    /// as permissões estourariam o limite de tamanho), aqui cabem: um client tem uma lista
    /// curta e fixa de escopos, não os papéis inteiros de uma organização. É o que torna a
    /// autorização de máquina <b>stateless</b> — nenhuma ida ao banco por request.
    /// </para>
    /// </summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>
    /// O tenant em nome do qual este client opera. Vai como claim <c>tenant_id</c> no JWT, e a
    /// partir daí o global query filter trata a máquina exatamente como trata um usuário.
    ///
    /// <para>
    /// Em modo single-tenant é o tenant padrão e ninguém pensa nisso. Em SaaS, é o que impede
    /// um client de um cliente ler os dados de outro — <b>por construção</b>, não por uma
    /// checagem que alguém pode esquecer de escrever no endpoint.
    /// </para>
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Quando o client deixa de valer. <c>null</c> = não expira.
    ///
    /// <para>
    /// Uma credencial de máquina costuma ser criada uma vez e esquecida por anos. Ter a coluna
    /// desde o começo é o que permite uma política de rotação existir depois sem migration.
    /// </para>
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Última emissão de token bem-sucedida. Para achar clients que ninguém mais usa.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }
}
