namespace Garius.Core.Security;

/// <summary>
/// Categorias de dado pessoal. Cada uma é uma permissão separada: quem pode ver o e-mail
/// de um usuário não necessariamente pode ver o CPF dele.
///
/// <para>
/// Ler PII em claro exige a permissão correspondente <b>e gera um registro de auditoria</b>
/// (LGPD, Art. 37: o controlador deve manter registro das operações de tratamento).
/// Criptografar sem auditar quem lê é fazer metade do trabalho.
/// </para>
/// </summary>
public enum PiiScope
{
    /// <summary>E-mail.</summary>
    Email,

    /// <summary>CPF. Dado sensível: identifica unicamente e é reutilizado em todo lugar.</summary>
    Cpf,

    /// <summary>Telefone.</summary>
    Phone,

    /// <summary>Endereço.</summary>
    Address,

    /// <summary>Data de nascimento.</summary>
    BirthDate
}
