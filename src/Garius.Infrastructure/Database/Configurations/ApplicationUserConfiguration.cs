using Garius.Core.Identity;
using Garius.Core.Security;
using Garius.Infrastructure.Database.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Garius.Infrastructure.Database.Configurations;

/// <summary>
/// Mapeia o usuário — e é aqui que o Identity é adaptado ao e-mail criptografado.
///
/// <para>
/// <b>O ponto crítico:</b> o <c>IdentityUser</c> traz <c>Email</c> e <c>NormalizedEmail</c>
/// como <c>string</c>, e o <c>UserStore</c> busca por <c>NormalizedEmail</c> no login. Deixar
/// as duas gravaria o e-mail <b>em claro</b> — anulando toda a criptografia.
/// </para>
///
/// <para>
/// A solução:
/// </para>
/// <list type="bullet">
///   <item><c>Email</c> é <b>ignorado</b> (a coluna não existe no banco);</item>
///   <item><c>NormalizedEmail</c> guarda o <b>índice cego</b> (HMAC em base64) — o
///         <c>BlindIndexLookupNormalizer</c> cuida disso, e o <c>FindByEmailAsync</c>
///         continua funcionando nativamente;</item>
///   <item><c>EmailPii</c> guarda o e-mail cifrado (AES-256-GCM).</item>
/// </list>
/// </summary>
/// <remarks>
/// <b>Não implementa <c>IEntityTypeConfiguration&lt;&gt;</c> de propósito.</b> O
/// <c>ApplyConfigurationsFromAssembly</c> varre o assembly em busca dessa interface e tenta
/// instanciar cada implementação <b>por reflexão</b> — o que falha aqui (o construtor exige o
/// <see cref="IFieldEncryptor"/>) e produz um aviso a cada boot. Como um aviso tolerado vira
/// ruído, e ruído esconde o aviso que importa, esta classe fica fora do scan: o
/// <see cref="AppDbContext"/> a chama diretamente.
/// </remarks>
internal sealed class ApplicationUserConfiguration(IFieldEncryptor encryptor)
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("users");

        // A coluna de e-mail em claro do Identity NÃO existe no banco.
        // Reintroduzi-la faria o e-mail voltar a ser gravado em texto puro.
        //
        // O IdentityDbContext base já mapeou a propriedade quando este Configure roda, daí o
        // "first mapped explicitly and then ignored" — é esperado, e o Ignore é o que vale.
        builder.Ignore(u => u.Email);

        // Guarda o HMAC do e-mail (base64 de 32 bytes = 44 chars), não o e-mail.
        // É por esta coluna que o login busca — e o índice único garante um cadastro por e-mail.
        builder.Property(u => u.NormalizedEmail).HasMaxLength(64);

        builder.HasIndex(u => u.NormalizedEmail)
               .IsUnique()
               .HasFilter("\"Enabled\" = true");

        // O e-mail de verdade: cifrado.
        builder.Property(u => u.EmailPii)
               .HasConversion(new PiiConverter(encryptor, PiiScope.Email))
               .HasColumnType("bytea")
               .HasColumnName("Email")
               .IsRequired();

        // CPF: cifrado + índice cego próprio (o Identity não tem uma coluna para reaproveitar).
        builder.HasPii(u => u.Cpf, u => u.CpfIndex, PiiScope.Cpf, encryptor, unique: false);

        // UserName do Identity: o Id em texto — identificador opaco, sem revelar nada.
        // (A convenção do framework é usar o e-mail como username; aqui isso vazaria PII.)
        builder.Property(u => u.UserName).HasMaxLength(64);
        builder.Property(u => u.NormalizedUserName).HasMaxLength(64);

        builder.Property(u => u.DisplayName).HasMaxLength(200);

        // Navegações explícitas — o IdentityUser padrão não tem nenhuma.
        builder.HasMany(u => u.UserRoles)
               .WithOne(ur => ur.User)
               .HasForeignKey(ur => ur.UserId)
               .IsRequired();

        builder.HasMany(u => u.Claims)
               .WithOne(c => c.User)
               .HasForeignKey(c => c.UserId)
               .IsRequired();

        builder.HasMany(u => u.Logins)
               .WithOne(l => l.User)
               .HasForeignKey(l => l.UserId)
               .IsRequired();

        builder.HasMany(u => u.Tokens)
               .WithOne(t => t.User)
               .HasForeignKey(t => t.UserId)
               .IsRequired();

        builder.HasMany(u => u.UserTenants)
               .WithOne(ut => ut.User)
               .HasForeignKey(ut => ut.UserId)
               .IsRequired();
    }
}
