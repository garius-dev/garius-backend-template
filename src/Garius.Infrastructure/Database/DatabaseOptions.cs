namespace Garius.Infrastructure.Database;

/// <summary>Seção <c>Database</c>. As senhas vêm do Secret Manager, nunca do appsettings.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5432;

    /// <summary>
    /// Superusuário. É a <b>chave mestra</b>, a mesma em todas as aplicações: só ele cria
    /// bancos e roles. Usado <b>exclusivamente</b> na janela de bootstrap
    /// (<c>MIGRATE_ONLY</c>); o runtime normal nunca o vê.
    /// </summary>
    public string RootUsername { get; set; } = "postgres";

    /// <summary>Do Secret Manager: <c>Database:RootPassword</c>.</summary>
    public string RootPassword { get; set; } = string.Empty;

    /// <summary>
    /// Do Secret Manager: <c>Database:AppPassword</c>. <b>Única por aplicação.</b>
    /// O bootstrap cria a role do app com exatamente esta senha — ela não é gerada,
    /// então não existe o problema de "onde guardo a senha que acabei de gerar".
    /// </summary>
    public string AppPassword { get; set; } = string.Empty;

    /// <summary>
    /// Nome da aplicação, do qual derivam banco, banco do Hangfire e usuário:
    /// <c>db_{slug}</c>, <c>hangfire_{slug}</c>, <c>{slug}_user</c>.
    ///
    /// <para>
    /// <b>Cada aplicação derivada deste template deve definir isto.</b> Sem ele, o nome
    /// viria do assembly de entrada (<c>Garius.Api</c> → <c>db_garius_api</c>), que é o
    /// nome do projeto, não o da aplicação — e duas apps diferentes colidiriam.
    /// </para>
    /// </summary>
    public string? ApplicationName { get; set; }

    /// <summary>Sobrescreve o nome do banco. Vazio = derivado de <see cref="ApplicationName"/>.</summary>
    public string? DatabaseName { get; set; }

    /// <summary>Sobrescreve o nome do usuário. Vazio = derivado de <see cref="ApplicationName"/>.</summary>
    public string? AppUsername { get; set; }

    public int MaxPoolSize { get; set; } = 20;

    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Quantas vezes retentar uma operação que falhou por motivo <b>transitório</b> (failover
    /// do banco, queda de rede). Ver <c>PersistenceExtensions</c>.
    ///
    /// <para>
    /// Três é o suficiente para atravessar um failover típico sem transformar uma
    /// indisponibilidade real numa espera longa: com o backoff do Npgsql, o cliente que
    /// espera por um banco que não vai voltar desiste em segundos, não em minutos.
    /// </para>
    /// </summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>
    /// Teto do intervalo entre tentativas. O Npgsql aplica backoff exponencial até este valor.
    /// </summary>
    public int MaxRetryDelaySeconds { get; set; } = 5;
}
