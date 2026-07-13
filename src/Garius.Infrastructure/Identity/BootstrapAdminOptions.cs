namespace Garius.Infrastructure.Identity;

/// <summary>
/// O primeiro usuário — o que resolve o problema do ovo e da galinha.
///
/// <para>
/// Sem ele, uma aplicação recém-implantada sobe <b>fechada</b>: a <c>FallbackPolicy</c> exige
/// autenticação em tudo, o <c>/scalar</c> exige <c>docs.read</c>, o <c>/jobs</c> exige
/// <c>jobs.read</c> — e não há nenhum usuário para conceder permissão a ninguém. Não dá nem
/// para entrar e criar o primeiro.
/// </para>
///
/// <para>
/// <b>Sem senha padrão. Nunca.</b> Um <c>admin/admin</c> embutido no template é como um deploy
/// vaza: alguém deriva, sobe, esquece de trocar, e uma conta com <b>permissão total</b> (<c>*</c>)
/// fica aberta na internet — com o agravante de que o template PARECE seguro. Estas duas chaves
/// vêm do Secret Manager, como todo segredo.
/// </para>
///
/// <para>
/// <b>Se não estiverem configuradas, nenhum usuário é criado</b> — e o bootstrap diz isso no
/// log. É a falha FECHADA: a ausência de configuração não pode virar uma conta aberta.
/// </para>
///
/// <code>
/// "Bootstrap:AdminEmail":    "voce@empresa.com",
/// "Bootstrap:AdminPassword": "&lt;uma senha forte&gt;"
/// </code>
/// </summary>
public sealed class BootstrapAdminOptions
{
    public const string SectionName = "Bootstrap";

    /// <summary>Do Secret Manager: <c>Bootstrap:AdminEmail</c>.</summary>
    public string AdminEmail { get; set; } = string.Empty;

    /// <summary>Do Secret Manager: <c>Bootstrap:AdminPassword</c>.</summary>
    public string AdminPassword { get; set; } = string.Empty;

    /// <summary>Os dois preenchidos? Só então o admin é semeado.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AdminEmail) && !string.IsNullOrWhiteSpace(AdminPassword);
}
