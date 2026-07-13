using System.Text.Json.Serialization;

namespace Garius.Core.Security;

/// <summary>
/// Um dado pessoal em claro. É um <b>tipo</b>, não uma <c>string</c>, e isso é o ponto:
/// o compilador impede que ele acabe num log, num DTO ou numa resposta por acidente.
///
/// <para>
/// Para obter o valor é preciso chamar <see cref="Reveal"/> — um verbo deliberadamente
/// explícito, fácil de encontrar num code review e num grep. <see cref="ToString"/> devolve
/// a versão <b>mascarada</b>, então mesmo uma interpolação descuidada
/// (<c>$"usuário {user.Email}"</c>) não vaza o dado.
/// </para>
///
/// <code>
/// var email = Pii.Create(PiiScope.Email, "joao@empresa.com");
///
/// logger.LogInformation("Usuário {Email}", email);   // → "j***@empresa.com"
/// email.Masked;                                       // → "j***@empresa.com"
/// email.Reveal();                                     // → "joao@empresa.com"  (explícito)
/// </code>
/// </summary>
[JsonConverter(typeof(PiiJsonConverter))]
public readonly struct Pii : IEquatable<Pii>
{
    private readonly string? _value;

    private Pii(PiiScope scope, string? value)
    {
        Scope = scope;
        _value = value;
    }

    public PiiScope Scope { get; }

    /// <summary>Nenhum valor (campo opcional não preenchido).</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    public static Pii Create(PiiScope scope, string? value) => new(scope, value);

    public static Pii Empty(PiiScope scope) => new(scope, null);

    /// <summary>
    /// O valor em claro. <b>Chamar isto é uma decisão consciente</b> — é o ponto onde a
    /// autorização deve ter sido checada e a auditoria registrada.
    /// Use <c>IPiiReader</c> em vez de chamar diretamente, sempre que houver um usuário
    /// por trás da leitura.
    /// </summary>
    public string Reveal() => _value ?? string.Empty;

    /// <summary>
    /// Versão mascarada, segura para log, resposta e tela de quem não tem permissão.
    /// O formato preserva o suficiente para o dado ser reconhecível pelo próprio titular
    /// sem ser identificável por terceiros.
    /// </summary>
    public string Masked => Mask(Scope, _value);

    /// <summary>Devolve a versão MASCARADA — para que uma interpolação acidental não vaze.</summary>
    public override string ToString() => Masked;

    internal static string Mask(PiiScope scope, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return scope switch
        {
            // joao@empresa.com → j***@empresa.com
            PiiScope.Email => MaskEmail(value),

            // 12345678901 → ***.***.789-**   (mantém os 3 dígitos que a pessoa reconhece)
            PiiScope.Cpf => MaskCpf(value),

            // 11987654321 → (11) *****-4321
            PiiScope.Phone => MaskPhone(value),

            // 1990-05-20 → ****-05-20
            PiiScope.BirthDate => value.Length >= 4 ? $"****{value[4..]}" : "****",

            _ => "***"
        };
    }

    private static string MaskEmail(string value)
    {
        var at = value.IndexOf('@', StringComparison.Ordinal);

        if (at <= 0)
        {
            return "***";
        }

        var local = value[..at];
        var domain = value[at..];

        return local.Length == 1
            ? $"*{domain}"
            : $"{local[0]}***{domain}";
    }

    private static string MaskCpf(string value)
    {
        var digits = new string([.. value.Where(char.IsDigit)]);

        return digits.Length == 11
            ? $"***.***.{digits[6..9]}-**"
            : "***.***.***-**";
    }

    private static string MaskPhone(string value)
    {
        var digits = new string([.. value.Where(char.IsDigit)]);

        return digits.Length >= 8
            ? $"*****-{digits[^4..]}"
            : "*****";
    }

    public bool Equals(Pii other) =>
        Scope == other.Scope && string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Pii other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Scope, _value);

    public static bool operator ==(Pii left, Pii right) => left.Equals(right);

    public static bool operator !=(Pii left, Pii right) => !left.Equals(right);
}
