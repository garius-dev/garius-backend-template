namespace Garius.Core.Machine;

/// <summary>
/// O que um request traz como credencial de máquina. Ver <see cref="MachineAuth.ExtractCredential"/>.
/// </summary>
public enum MachineCredentialKind
{
    /// <summary>Nenhuma credencial de máquina — é o cookie do usuário, ou um anônimo.</summary>
    None = 0,

    /// <summary>JWT de client credentials, em <c>Authorization: Bearer ey...</c>.</summary>
    Jwt = 1,

    /// <summary>
    /// Chave de API, em <c>Authorization: Bearer gk_...</c> ou no header <c>X-Api-Key</c>.
    /// </summary>
    ApiKey = 2,
}
