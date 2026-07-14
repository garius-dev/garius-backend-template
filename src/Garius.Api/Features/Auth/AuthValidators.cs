using FluentValidation;

namespace Garius.Api.Features.Auth;

/// <summary>
/// Validação de <c>POST /auth/login</c>.
///
/// <para>
/// <b>Isto corrige um 500.</b> Sem validator, um <c>POST /auth/login</c> com o corpo <c>{}</c>
/// entregava <c>Email = null</c> ao <c>AuthService</c>, que chamava
/// <c>FindByEmailAsync(null)</c> — e a API respondia <b>500 server.unexpected</b>. Um request
/// malformado (culpa do cliente) virava erro do servidor: alarme falso no Grafana, ruído no
/// Loki, e uma mensagem que mentia sobre a causa.
/// </para>
///
/// <para>
/// Havia um segundo custo, menos óbvio: um e-mail vazio <b>chegava a percorrer o fluxo de
/// login</b> — incluindo o <c>FakePasswordCheckAsync()</c>, que queima ~100ms de CPU de
/// propósito (é a defesa contra enumeração por tempo). Um atacante conseguia gastar CPU da API
/// mandando corpos vazios. Agora o request morre <b>antes</b> disso, em 400.
/// </para>
/// </summary>
public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("O e-mail é obrigatório.")
            .EmailAddress().WithMessage("Informe um e-mail válido.")
            .MaximumLength(256);

        // NÃO se valida FORMATO de senha aqui (tamanho mínimo, maiúscula, dígito).
        //
        // No LOGIN isso seria um oráculo: recusar "senha muito curta" antes de checar as
        // credenciais conta ao atacante que a política existe e qual é — e, pior, diferencia
        // uma tentativa "malformada" de uma "errada", que é exatamente a distinção que o
        // InvalidCredentials() genérico existe para apagar.
        //
        // A política de senha vale na CRIAÇÃO da conta, e quem a aplica é o Identity.
        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("A senha é obrigatória.")
            .MaximumLength(256);
    }
}

/// <summary>Validação de <c>POST /auth/select-tenant</c>.</summary>
public sealed class SelectTenantRequestValidator : AbstractValidator<SelectTenantRequest>
{
    public SelectTenantRequestValidator()
    {
        // Guid.Empty é o "nulo" de um Guid não-nullable: o corpo `{}` o produz em silêncio.
        //
        // NÃO se valida aqui se o tenant EXISTE — nem se o usuário pertence a ele. Isso é
        // AUTORIZAÇÃO, e vive no AuthService.SelectTenantAsync, que sabe QUEM está pedindo. Um
        // validator que checasse a existência do tenant viraria um oráculo: qualquer um
        // descobriria quais tenants existem, variando o palpite.
        RuleFor(x => x.TenantId)
            .NotEmpty().WithMessage("O tenant é obrigatório.");
    }
}
