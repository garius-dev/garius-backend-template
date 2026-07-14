using System.Collections.Frozen;
using System.Reflection;
using Garius.Core.Security;

namespace Garius.Core.Authorization;

/// <summary>
/// O <b>registro único</b> de permissões da aplicação.
///
/// <para>
/// Declarar aqui, e não como string solta no endpoint, é o que permite:
/// </para>
/// <list type="bullet">
///   <item>o compilador pegar um erro de digitação;</item>
///   <item>o frontend listar as permissões (<c>GET /permissions</c>) para montar a tela de
///         administração de papéis, sem ninguém manter uma segunda lista em JavaScript;</item>
///   <item>validar, ao conceder uma permissão, que ela existe de verdade.</item>
/// </list>
///
/// <para>
/// <b>Uma aplicação derivada acrescenta as suas</b> criando uma classe estática aninhada com
/// campos <c>public static readonly Permission</c> — a descoberta é por reflexão, então basta
/// declarar.
/// </para>
/// </summary>
public static class Permissions
{
    /// <summary>
    /// Superadministrador: satisfaz <b>qualquer</b> permissão. Ver <see cref="Permission.Matches"/>.
    /// Conceder isto é conceder tudo, inclusive o que ainda não existe — use com parcimônia.
    /// </summary>
    public const string SuperAdmin = Permission.Wildcard;

    /// <summary>Administração de usuários.</summary>
    public static class Users
    {
        public static readonly Permission Read = new("users", "read", "Ver usuários");
        public static readonly Permission Create = new("users", "create", "Criar usuários");
        public static readonly Permission Update = new("users", "update", "Editar usuários");
        public static readonly Permission Delete = new("users", "delete", "Desativar usuários");
    }

    /// <summary>Administração de papéis e permissões.</summary>
    public static class Roles
    {
        public static readonly Permission Read = new("roles", "read", "Ver papéis");
        public static readonly Permission Create = new("roles", "create", "Criar papéis");
        public static readonly Permission Update = new("roles", "update", "Editar papéis e suas permissões");
        public static readonly Permission Delete = new("roles", "delete", "Excluir papéis");

        /// <summary>
        /// Conceder e revogar papéis de usuários. Separada de <see cref="Update"/> de propósito:
        /// quem edita um papel não necessariamente pode <b>dar</b> esse papel a alguém — inclusive
        /// a si mesmo, que é o caminho clássico de escalada de privilégio.
        /// </summary>
        public static readonly Permission Assign = new("roles", "assign", "Conceder e revogar papéis de usuários");
    }

    /// <summary>
    /// Administração das credenciais de máquina: clients OAuth2 e chaves de API.
    ///
    /// <para>
    /// <b>Separada de <see cref="Users"/> e <see cref="Roles"/> de propósito.</b> Quem cria um
    /// client escolhe os escopos dele — e, sem uma trava, isso seria uma <b>escalada de
    /// privilégio em dois passos</b>: criar um client com escopo <c>*</c>, autenticar-se com ele,
    /// virar superadministrador. Uma permissão de aparência inócua ("criar credenciais") daria
    /// acesso total.
    /// </para>
    ///
    /// <para>
    /// A trava existe: <c>MachineAuthService.ValidateScopesAsync</c> compara os escopos pedidos
    /// com as permissões de <b>quem está criando</b> (respeitando curinga) e recusa o que exceder
    /// — <b>ninguém delega um poder que não tem</b>. Neutralizar essa checagem faz 3 testes
    /// falharem.
    /// </para>
    ///
    /// <para>
    /// Ainda assim, <c>clients.create</c> permite delegar a uma máquina <b>tudo o que você
    /// tem</b> — numa credencial de longa duração, que vive fora do navegador. Conceda a pouca
    /// gente, e é por isso que não vem de carona em "administrar usuários".
    /// </para>
    /// </summary>
    public static class Clients
    {
        public static readonly Permission Read = new("clients", "read", "Ver clients M2M e chaves de API");
        public static readonly Permission Create = new("clients", "create", "Criar clients M2M e chaves de API");
        public static readonly Permission Revoke = new("clients", "revoke", "Revogar clients M2M e chaves de API");
    }

    /// <summary>
    /// Jobs de background (o dashboard do Hangfire).
    ///
    /// <para>
    /// ⚠️ <b>O dashboard não é só leitura.</b> Quem o acessa consegue <b>disparar</b> e
    /// <b>apagar</b> jobs — ou seja, executar código da aplicação sob demanda. Por isso ele
    /// exige permissão, e por isso ela é sua (não vem de carona em nenhuma outra).
    /// </para>
    /// </summary>
    public static class Jobs
    {
        public static readonly Permission Read = new("jobs", "read", "Acessar o painel de jobs");
    }

    /// <summary>
    /// A documentação da API (Scalar).
    ///
    /// <para>
    /// <b>Por que a documentação exige permissão.</b> Ela é o <b>mapa completo</b> da aplicação:
    /// todos os endpoints, todos os parâmetros, todos os esquemas de autenticação, todos os
    /// códigos de erro. Servida a anônimos, é reconhecimento pronto — um atacante não precisa
    /// descobrir nada, basta ler.
    /// </para>
    ///
    /// <para>
    /// Isso não é paranoia com uma API interna: é o mesmo raciocínio pelo qual
    /// <c>/health/detail</c> exige uma chave. Se a sua API for <b>deliberadamente pública</b>
    /// (como a do Stripe ou a do GitHub), troque para <c>.AllowAnonymous()</c> — mas que seja
    /// uma decisão, não um esquecimento.
    /// </para>
    /// </summary>
    public static class Docs
    {
        public static readonly Permission Read = new("docs", "read", "Ver a documentação da API");
    }

    /// <summary>
    /// Leitura de dados pessoais em claro (LGPD). Uma por categoria: quem vê o e-mail não
    /// necessariamente vê o CPF.
    /// </summary>
    public static class Pii
    {
        public static readonly Permission ReadEmail = new("pii", "email.read", "Ver e-mails de outros usuários");
        public static readonly Permission ReadCpf = new("pii", "cpf.read", "Ver CPFs de outros usuários");
        public static readonly Permission ReadPhone = new("pii", "phone.read", "Ver telefones de outros usuários");
        public static readonly Permission ReadAddress = new("pii", "address.read", "Ver endereços de outros usuários");
        public static readonly Permission ReadBirthDate = new("pii", "birthdate.read", "Ver datas de nascimento");

        /// <summary>Auditoria: quem leu qual dado pessoal, de quem, quando e por quê.</summary>
        public static readonly Permission ReadAuditLog = new("pii", "audit.read", "Ver a trilha de auditoria de dados pessoais");
    }

    /// <summary>
    /// Todas as permissões declaradas, descobertas por reflexão. É o que o
    /// <c>GET /permissions</c> devolve ao frontend.
    /// </summary>
    public static FrozenSet<Permission> Catalog { get; } = Discover();

    private static readonly FrozenDictionary<string, Permission> ByValue =
        Catalog.ToFrozenDictionary(p => p.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>A permissão existe? Usado ao conceder, para não gravar um nome inventado.</summary>
    public static bool Exists(string value) => ByValue.ContainsKey(value);

    public static Permission? Find(string value) => ByValue.GetValueOrDefault(value);

    /// <summary>Traduz um <see cref="PiiScope"/> na permissão que o cobre.</summary>
    public static Permission ForPiiScope(PiiScope scope) => scope switch
    {
        PiiScope.Email => Pii.ReadEmail,
        PiiScope.Cpf => Pii.ReadCpf,
        PiiScope.Phone => Pii.ReadPhone,
        PiiScope.Address => Pii.ReadAddress,
        PiiScope.BirthDate => Pii.ReadBirthDate,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "PiiScope desconhecido.")
    };

    /// <summary>
    /// Varre esta classe (e as aninhadas) em busca de campos <c>static readonly Permission</c>.
    /// É o que faz uma aplicação derivada só precisar <b>declarar</b> a permissão — sem também
    /// registrá-la numa lista, que é onde as duas coisas divergem com o tempo.
    /// </summary>
    private static FrozenSet<Permission> Discover()
    {
        var found = new List<Permission>();

        Collect(typeof(Permissions), found);

        return found.ToFrozenSet();
    }

    private static void Collect(Type type, List<Permission> into)
    {
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType == typeof(Permission) && field.GetValue(null) is Permission permission)
            {
                into.Add(permission);
            }
        }

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public))
        {
            Collect(nested, into);
        }
    }
}
