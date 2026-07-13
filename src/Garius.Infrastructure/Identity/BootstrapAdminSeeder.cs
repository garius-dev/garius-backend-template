using System.Security.Claims;
using Garius.Core.Authorization;
using Garius.Core.Identity;
using Garius.Core.Security;
using Garius.Core.Tenancy;
using Garius.Infrastructure.Database;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Garius.Infrastructure.Identity;

/// <summary>
/// Cria o primeiro usuário — um superadministrador — durante o <c>MIGRATE_ONLY</c>.
///
/// <para>
/// Resolve o ovo e a galinha: sem ele, a aplicação sobe fechada (a <c>FallbackPolicy</c> exige
/// autenticação em tudo, o <c>/scalar</c> exige <c>docs.read</c>) e não há ninguém para conceder
/// permissão a ninguém.
/// </para>
///
/// <para>
/// <b>Roda no bootstrap, não no runtime.</b> O container de migrations é ÚNICO — a API pode ter
/// N réplicas, e N réplicas semeando o mesmo usuário ao mesmo tempo é uma corrida.
/// </para>
///
/// <para>
/// <b>É idempotente.</b> Se o usuário já existe, ele NÃO é tocado: nem a senha, nem as
/// permissões. Um deploy não pode ressuscitar uma conta que você revogou, nem resetar a senha
/// que você trocou — seria uma porta dos fundos que reabre a cada deploy.
/// </para>
/// </summary>
public sealed class BootstrapAdminSeeder(
    UserManager<ApplicationUser> userManager,
    AppDbContext dbContext,
    IOptions<BootstrapAdminOptions> options,
    IOptions<TenancyOptions> tenancy,
    ILogger<BootstrapAdminSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        if (!settings.IsConfigured)
        {
            // FALHA FECHADA: sem configuração, NENHUM usuário é criado. A alternativa (criar um
            // admin com senha padrão) transformaria a ausência de configuração numa conta com
            // permissão total aberta na internet.
            logger.LogWarning(
                "Bootstrap: nenhum administrador foi criado — Bootstrap:AdminEmail e " +
                "Bootstrap:AdminPassword não estão configurados. A aplicação sobe FECHADA: " +
                "/scalar e /jobs exigem permissão, e não há usuário para concedê-la. " +
                "Configure os dois no Secret Manager e rode o bootstrap de novo.");

            return;
        }

        // FindByEmailAsync compara o ÍNDICE CEGO (o LookupNormalizer converte o e-mail em HMAC),
        // não o e-mail em claro — que nem sequer existe no banco.
        var existing = await userManager.FindByEmailAsync(settings.AdminEmail);

        if (existing is not null)
        {
            // Idempotente e SEM efeito colateral. NÃO redefine a senha, NÃO reconcede a
            // permissão.
            //
            // A tentação é "consertar" o admin a cada deploy — e seria uma PORTA DOS FUNDOS que
            // reabre sozinha: você revoga o acesso de alguém, faz um deploy qualquer, e o acesso
            // volta em silêncio. O bootstrap CRIA o primeiro usuário; ele não administra
            // usuários.
            logger.LogInformation(
                "Bootstrap: o administrador já existe (Id {UserId}). Nada a fazer.",
                existing.Id);

            return;
        }

        var admin = new ApplicationUser
        {
            UserName = settings.AdminEmail,
            EmailPii = Pii.Create(PiiScope.Email, settings.AdminEmail),
            EmailConfirmed = true,
            DisplayName = "Administrador",
        };

        var created = await userManager.CreateAsync(admin, settings.AdminPassword);

        if (!created.Succeeded)
        {
            // A senha não passou na política do Identity, quase sempre. Falhar aqui derruba o
            // bootstrap (exit != 0) e SEGURA a API — melhor do que subir sem administrador.
            var errors = string.Join("; ", created.Errors.Select(e => e.Description));

            throw new InvalidOperationException(
                $"Bootstrap: não foi possível criar o administrador: {errors}");
        }

        // A permissão vai como CLAIM AVULSA, com TenantId = null: vale em TODOS os tenants.
        //
        // Curinga (`*`), não uma lista de permissões: o superadministrador não pode ficar
        // desatualizado quando você criar uma permissão nova numa feature futura. Ver
        // Permission.Matches.
        dbContext.UserClaims.Add(new ApplicationUserClaim
        {
            UserId = admin.Id,
            ClaimType = Permission.ClaimType,
            ClaimValue = Permissions.SuperAdmin,
            TenantId = null,
        });

        // Vincula ao tenant padrão (o mesmo que o DatabaseBootstrapper acabou de semear). Sem
        // isso, o login funciona mas o usuário não pertence a lugar nenhum.
        var defaultTenantId = tenancy.Value.DefaultTenantId;

        var alreadyLinked = await dbContext.UserTenants
            .IgnoreQueryFilters()
            .AnyAsync(
                ut => ut.UserId == admin.Id && ut.TenantId == defaultTenantId,
                cancellationToken);

        if (!alreadyLinked)
        {
            dbContext.UserTenants.Add(new ApplicationUserTenant
            {
                UserId = admin.Id,
                TenantId = defaultTenantId,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // O e-mail NÃO vai para o log: é PII, e o log vai para o Loki.
        logger.LogInformation(
            "Bootstrap: administrador criado (Id {UserId}) com permissão total ({Permission}).",
            admin.Id,
            Permissions.SuperAdmin);
    }
}
