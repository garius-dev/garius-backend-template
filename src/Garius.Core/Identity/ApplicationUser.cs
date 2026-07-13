using Garius.Core.Entities;
using Garius.Core.Security;
using Microsoft.AspNetCore.Identity;

namespace Garius.Core.Identity;

/// <summary>
/// Usuário da aplicação.
///
/// <para>
/// <b>Não herda de <c>BaseEntity</c></b>: o <see cref="IdentityUser{TKey}"/> já traz <c>Id</c>,
/// e herdar dos dois criaria duas propriedades em conflito. As colunas de auditoria vêm de
/// <see cref="IAuditable"/>, que é o contrato pelo qual o <c>AppDbContext</c> aplica soft
/// delete e timestamps a todas as tabelas.
/// </para>
///
/// <para>
/// <b>Não implementa <c>ITenantEntity</c></b>: um usuário pertence a vários tenants (N:N, via
/// <see cref="UserTenants"/>). Um <c>TenantId</c> na própria tabela seria a modelagem 1:N,
/// que foi descartada.
/// </para>
///
/// <para>
/// <b>Como o e-mail criptografado convive com o Identity:</b>
/// </para>
/// <list type="bullet">
///   <item><see cref="EmailPii"/> — o e-mail cifrado (AES-256-GCM). É o dado de verdade.</item>
///   <item><c>NormalizedEmail</c> (herdado) — guarda o <b>índice cego</b> (HMAC em base64), não
///         o e-mail. É o que o <c>UserStore</c> compara em <c>FindByEmailAsync</c>, sem saber
///         que é um HMAC. Ver <c>BlindIndexLookupNormalizer</c>.</item>
///   <item><c>Email</c> (herdado) — <b>ignorado no mapeamento</b>: gravaria o e-mail em claro.</item>
/// </list>
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>, IAuditable
{
    public ApplicationUser()
    {
        Id = Guid.CreateVersion7();
    }

    // --- Dados pessoais (LGPD) ---

    /// <summary>
    /// E-mail, cifrado no banco. É o dado de verdade.
    ///
    /// <para>
    /// Escrever aqui também preenche o <c>Email</c> (string) herdado do Identity — que existe
    /// <b>apenas em memória</b> (a coluna é ignorada no mapeamento). O <c>UserManager</c>
    /// precisa dele como <i>entrada</i>: o <c>CreateAsync</c> faz
    /// <c>NormalizeEmail(user.Email)</c> para preencher o <c>NormalizedEmail</c>. Sem isso,
    /// o índice cego nunca seria gravado e <c>FindByEmailAsync</c> não acharia ninguém.
    /// </para>
    /// </summary>
    public Pii EmailPii
    {
        get => _emailPii;
        set
        {
            _emailPii = value;

            // Alimenta o Identity. Em memória apenas — nunca chega ao banco.
            Email = value.IsEmpty ? null : value.Reveal();
        }
    }

    private Pii _emailPii;

    /// <summary>CPF, cifrado. Opcional.</summary>
    public Pii Cpf { get; set; }

    /// <summary>Índice cego do CPF (HMAC-SHA256). É por aqui que se busca por CPF.</summary>
    public byte[] CpfIndex { get; set; } = [];

    /// <summary>Nome de exibição. Não é PII sensível — vai em claro.</summary>
    public string? DisplayName { get; set; }

    // --- Auditoria e soft delete ---

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    // --- Navegações explícitas ---
    //
    // O IdentityUser padrão tem ZERO navegações: `user.Roles` não existe, e é por isso que
    // se acaba chamando _userManager.GetRolesAsync() a cada request. O grafo abaixo é
    // consultável numa query só.

    public ICollection<ApplicationUserRole> UserRoles { get; } = [];

    public ICollection<ApplicationUserClaim> Claims { get; } = [];

    public ICollection<ApplicationUserLogin> Logins { get; } = [];

    public ICollection<ApplicationUserToken> Tokens { get; } = [];

    /// <summary>
    /// Os tenants a que este usuário pertence (N:N).
    ///
    /// <para>
    /// É o que obriga o login em dois passos quando há mais de um: o usuário autentica, vê a
    /// lista, escolhe — e só então o <c>TenantId</c> entra nas claims do cookie.
    /// </para>
    /// </summary>
    public ICollection<ApplicationUserTenant> UserTenants { get; } = [];
}
