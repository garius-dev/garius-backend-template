using System.Reflection;
using System.Text.RegularExpressions;
using Npgsql;

namespace Garius.Infrastructure.Database;

/// <summary>
/// <b>Fonte única</b> dos nomes de banco/role e das connection strings.
///
/// <para>
/// No template anterior, o nome do usuário era montado por interpolação em 4 lugares, com
/// <b>duas fórmulas diferentes</b>: uma usava o último segmento do assembly
/// (<c>GariusTech.Template.Api</c> → <c>api_user</c>) e outra o assembly inteiro. O
/// resultado é que o health check do Postgres tentava autenticar com um usuário que não
/// existia — e nunca funcionou, sem ninguém perceber.
/// </para>
///
/// <para>
/// Aqui os nomes derivam do <b>nome completo</b> do assembly, e as connection strings são
/// montadas por <see cref="NpgsqlConnectionStringBuilder"/> — que escapa corretamente
/// senhas contendo <c>;</c> ou <c>'</c> (a interpolação de string quebrava nesses casos).
/// Tudo é calculado uma única vez, no construtor.
/// </para>
/// </summary>
public sealed partial class DatabaseNaming
{
    private readonly DatabaseOptions _options;

    public DatabaseNaming(DatabaseOptions options, Assembly applicationAssembly)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(applicationAssembly);

        _options = options;

        // Database:ApplicationName é a fonte preferida — o nome do assembly de entrada é
        // "Garius.Api" em toda app derivada deste template, e derivar dele faria duas apps
        // diferentes colidirem no mesmo banco e no mesmo usuário.
        var source = options.ApplicationName is { Length: > 0 } name
            ? name
            : applicationAssembly.GetName().Name!;

        var slug = Slugify(source);

        DatabaseName = options.DatabaseName is { Length: > 0 } db ? db : $"db_{slug}";
        HangfireDatabaseName = $"hangfire_{slug}";
        AppUsername = options.AppUsername is { Length: > 0 } user ? user : $"{slug}_user";

        RootConnectionString = Build(options.RootUsername, options.RootPassword, "postgres");
        RootConnectionStringToAppDatabase = Build(options.RootUsername, options.RootPassword, DatabaseName);
        AppConnectionString = Build(AppUsername, options.AppPassword, DatabaseName);
        HangfireConnectionString = Build(AppUsername, options.AppPassword, HangfireDatabaseName);
    }

    /// <summary>Banco de negócio. Ex.: <c>db_gariustech_backend_template</c>.</summary>
    public string DatabaseName { get; }

    /// <summary>
    /// Banco do Hangfire — um <b>banco</b> separado, não um schema. É a convenção que o
    /// usuário já usa em produção, e mantém as tabelas de job fora do banco de negócio.
    /// </summary>
    public string HangfireDatabaseName { get; }

    /// <summary>Usuário de runtime. Único por aplicação. Só CRUD, sem DDL.</summary>
    public string AppUsername { get; }

    /// <summary>
    /// Conexão como superusuário, no banco <c>postgres</c>. Só existe para o bootstrap
    /// criar banco e roles. <b>Nunca</b> deve ser usada em runtime.
    /// </summary>
    public string RootConnectionString { get; }

    /// <summary>Conexão do bootstrap já dentro do banco da aplicação (para GRANTs e migrations).</summary>
    public string RootConnectionStringToAppDatabase { get; }

    /// <summary>Conexão de runtime. É a única que a aplicação usa depois do bootstrap.</summary>
    public string AppConnectionString { get; }

    /// <summary>Conexão do Hangfire (mesmo usuário de runtime, banco separado).</summary>
    public string HangfireConnectionString { get; }

    /// <summary>Conexão como superusuário em um banco específico. Só para o bootstrap.</summary>
    public string RootConnectionStringTo(string database) =>
        Build(_options.RootUsername, _options.RootPassword, database);

    private string Build(string username, string password, string database) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = _options.Host,
            Port = _options.Port,
            Username = username,
            Password = password,
            Database = database,
            MaxPoolSize = _options.MaxPoolSize,
            CommandTimeout = _options.CommandTimeoutSeconds,
            // Sem SSL entre containers na mesma rede Docker; a borda TLS é o Traefik.
            SslMode = SslMode.Prefer
        }.ConnectionString;

    /// <summary><c>GariusTech.Backend.Template</c> → <c>gariustech_backend_template</c>.</summary>
    private static string Slugify(string assemblyName) =>
        NonAlphanumeric().Replace(assemblyName.ToLowerInvariant(), "_").Trim('_');

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
