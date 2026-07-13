namespace Garius.Core.Authorization;

/// <summary>
/// Uma permissão no formato <c>recurso.ação</c> — o modelo do Google Cloud IAM.
///
/// <code>
/// users.create
/// invoices.approve
/// reports.export
/// </code>
///
/// <para>
/// <b>É a permissão, não o papel, que o endpoint exige.</b> Um papel é apenas um conjunto
/// nomeado de permissões. No dia em que o "Gerente" também precisar aprovar fatura, mexe-se
/// no banco — não no código.
/// </para>
/// </summary>
/// <param name="Resource">O recurso (ex.: <c>invoices</c>).</param>
/// <param name="Action">A ação (ex.: <c>approve</c>).</param>
/// <param name="Description">Para que serve. Aparece na tela de administração de papéis.</param>
public sealed record Permission(string Resource, string Action, string Description)
{
    /// <summary>Separa recurso e ação, e recurso e curinga.</summary>
    public const char Separator = '.';

    /// <summary>Curinga: <c>invoices.*</c> (tudo em faturas) ou <c>*</c> (superadministrador).</summary>
    public const string Wildcard = "*";

    /// <summary>
    /// O tipo da claim que carrega permissões. É por ele que as permissões chegam ao usuário,
    /// vindas dos papéis ou concedidas diretamente.
    /// </summary>
    public const string ClaimType = "permission";

    /// <summary>A permissão como string: <c>invoices.approve</c>.</summary>
    public string Value => $"{Resource}{Separator}{Action}";

    public override string ToString() => Value;

    /// <summary>
    /// Uma permissão concedida (<paramref name="granted"/>, que pode ter curinga) satisfaz a
    /// permissão <paramref name="required"/>?
    ///
    /// <code>
    /// Matches("*",               "invoices.approve")  → true   (superadministrador)
    /// Matches("invoices.*",      "invoices.approve")  → true   (tudo em faturas)
    /// Matches("invoices.approve","invoices.approve")  → true
    /// Matches("invoices.read",   "invoices.approve")  → false
    /// Matches("*.approve",       "invoices.approve")  → false  (curinga só à direita)
    /// </code>
    ///
    /// <para>
    /// O curinga só existe à <b>direita</b>, de propósito. Um <c>*.delete</c> ("apagar
    /// qualquer coisa") é uma permissão que ninguém consegue auditar — e que quase sempre é
    /// concedida por engano.
    /// </para>
    /// </summary>
    public static bool Matches(string granted, string required)
    {
        ArgumentNullException.ThrowIfNull(granted);
        ArgumentNullException.ThrowIfNull(required);

        if (granted == Wildcard)
        {
            return true;
        }

        if (string.Equals(granted, required, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // invoices.* satisfaz invoices.qualquer-coisa
        if (granted.EndsWith($"{Separator}{Wildcard}", StringComparison.Ordinal))
        {
            var resource = granted[..^2];

            return required.StartsWith($"{resource}{Separator}", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
