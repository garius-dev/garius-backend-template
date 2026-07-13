namespace Garius.Core.Security;

/// <summary>
/// Criptografia de campo para dados pessoais (LGPD).
///
/// <para>
/// <b>AES-256-GCM com nonce aleatório</b>: o mesmo valor gera ciphertexts diferentes a cada
/// gravação. Isso é deliberado — criptografia determinística vazaria igualdade entre
/// registros (dois usuários com o mesmo CPF teriam o mesmo ciphertext) e seria fraca
/// justamente para o CPF, cujo espaço de busca é pequeno (~10^11).
/// </para>
///
/// <para>
/// O preço do nonce aleatório é que <c>WHERE Cpf = @x</c> deixa de funcionar. É para isso
/// que existe o <see cref="IBlindIndex"/>.
/// </para>
/// </summary>
public interface IFieldEncryptor
{
    /// <summary>
    /// Cifra um valor. O resultado embute a versão da chave, o nonce e a tag de
    /// autenticação — então é autossuficiente para ser decifrado depois, inclusive após
    /// uma rotação de chave.
    /// </summary>
    byte[] Encrypt(string plaintext);

    /// <summary>
    /// Decifra. Lança se o dado foi adulterado (o GCM autentica) ou se a chave que o cifrou
    /// não está mais disponível.
    /// </summary>
    string Decrypt(byte[] ciphertext);
}
