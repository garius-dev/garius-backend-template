using Garius.Core.Entities;
using Garius.Core.Tenancy;

namespace Garius.Core.Security;

/// <summary>
/// Registro de <b>toda leitura de dado pessoal em claro</b>.
///
/// <para>
/// LGPD, Art. 37: o controlador deve manter registro das operações de tratamento de dados
/// pessoais. Criptografar o banco e não registrar quem descriptografou é fazer metade do
/// trabalho — quando houver um incidente, é este registro que responde "quem viu o CPF de
/// quem, e quando".
/// </para>
///
/// <para>
/// Note o que <b>não</b> está aqui: o valor lido. O log registra que o acesso ocorreu,
/// nunca o dado — do contrário a tabela de auditoria viraria a maior fonte de vazamento de
/// PII do sistema.
/// </para>
/// </summary>
public sealed class PiiAccessLog : BaseEntity, ITenantEntity
{
    public Guid TenantId { get; set; }

    /// <summary>Quem leu. <c>null</c> quando a leitura partiu do sistema (um job, uma migração).</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Nome da entidade lida (ex.: <c>ApplicationUser</c>).</summary>
    public required string EntityType { get; set; }

    /// <summary>Id do registro lido — de quem é o dado pessoal.</summary>
    public required Guid EntityId { get; set; }

    /// <summary>Que categoria de dado foi lida.</summary>
    public required PiiScope Scope { get; set; }

    /// <summary>
    /// Por que foi lido. É o campo que dá sentido ao registro numa auditoria: sem ele, o
    /// log diz que alguém leu 400 CPFs e não diz se foi uma exportação legítima ou um vazamento.
    /// </summary>
    public required string Reason { get; set; }

    /// <summary>IP real de quem leu (via <c>CF-Connecting-IP</c> validado).</summary>
    public string? ClientIp { get; set; }

    /// <summary>Correlaciona com o log da aplicação no Grafana.</summary>
    public string? TraceId { get; set; }
}
