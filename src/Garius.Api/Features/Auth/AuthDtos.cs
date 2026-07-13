namespace Garius.Api.Features.Auth;

public sealed record LoginRequest(string Email, string Password);

public sealed record SelectTenantRequest(Guid TenantId);

/// <param name="Id">Id do tenant.</param>
/// <param name="Name">Nome de exibição.</param>
/// <param name="IsDefault">O tenant que o usuário assume por padrão ao entrar.</param>
public sealed record TenantOptionDto(Guid Id, string Name, bool IsDefault);

/// <summary>
/// Resposta do login.
///
/// <para>
/// Quando o usuário pertence a <b>vários</b> tenants (o vínculo é N:N), o login <b>não</b>
/// emite o cookie: devolve a lista para o front apresentar a escolha, e a sessão só nasce no
/// <c>/auth/select-tenant</c>. Com um único tenant, o cookie sai direto — o segundo passo é um
/// atalho, não uma obrigação.
/// </para>
/// </summary>
/// <param name="Authenticated">
/// <c>true</c> = a sessão está criada (o cookie foi emitido).
/// <c>false</c> = falta selecionar o tenant.
/// </param>
/// <param name="Tenants">Os tenants disponíveis, quando é preciso escolher.</param>
/// <param name="User">Os dados do usuário, quando a sessão foi criada.</param>
public sealed record LoginResponse(
    bool Authenticated,
    IReadOnlyList<TenantOptionDto>? Tenants,
    CurrentUserDto? User);

/// <param name="Id">Id do usuário.</param>
/// <param name="Email">
/// <b>Mascarado</b> (<c>j***@empresa.com</c>). O e-mail em claro exige uma leitura auditada
/// via <c>IPiiReader</c> — não é derramado numa resposta de login.
/// </param>
/// <param name="DisplayName">Nome de exibição.</param>
/// <param name="TenantId">O tenant da sessão.</param>
/// <param name="Permissions">
/// As permissões efetivas. Vão na <b>resposta</b> (para o front decidir o que renderizar) —
/// mas <b>nunca no cookie</b>, que teria seu tamanho estourado. Ver PermissionScaleTests.
/// </param>
public sealed record CurrentUserDto(
    Guid Id,
    string Email,
    string? DisplayName,
    Guid? TenantId,
    IReadOnlyList<string> Permissions);
