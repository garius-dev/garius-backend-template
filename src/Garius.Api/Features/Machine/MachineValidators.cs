using FluentValidation;
using Garius.Api.Infrastructure.Validation;

namespace Garius.Api.Features.Machine;

/// <summary>
/// Validação de <c>POST /machine/clients</c>.
///
/// <para>
/// ⚠️ <b>Não existe validator para o <c>TokenRequest</c> (<c>POST /auth/token</c>), e é
/// deliberado.</b> Aquele endpoint segue a RFC 6749: os erros dele saem como
/// <c>{ "error": "invalid_client" }</c>, não como o ProblemDetails da API. Um validator o faria
/// responder no formato errado, e toda biblioteca OAuth padrão quebraria — que é justamente o
/// que a exceção ao envelope existe para evitar. Lá a validação é feita à mão, no
/// <c>MachineAuthService</c>.
/// </para>
/// </summary>
public sealed class CreateOAuthClientRequestValidator : AbstractValidator<CreateOAuthClientRequest>
{
    public CreateOAuthClientRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("O nome do client é obrigatório.")
            .MaximumLength(200);

        RuleFor(x => x.Scopes)
            .NotEmpty().WithMessage("Um client sem escopo nenhum não conseguiria fazer nada.");

        // Cada escopo, individualmente, tem de EXISTIR no catálogo.
        //
        // Um typo (`users.raed`) não é um erro qualquer: ele cria uma credencial que AUTENTICA
        // mas não AUTORIZA nada. O integrador recebe um token válido, toma 403 em tudo, e vai
        // procurar o defeito na integração dele — que está certa. Barrar na criação é a única
        // hora em que o erro ainda é barato.
        //
        // (A checagem de ESCALADA — "você não pode delegar um poder que não tem" — é outra, e
        // vive no MachineAuthService.ValidateScopesAsync: ela precisa saber QUEM está criando.)
        RuleForEach(x => x.Scopes).MustBeAKnownPermission();

        RuleFor(x => x.ExpiresAt)
            .Must(date => date is null || date > DateTimeOffset.UtcNow)
            .WithMessage("A data de expiração precisa estar no futuro.");
    }
}

/// <summary>Validação de <c>POST /machine/api-keys</c>.</summary>
public sealed class CreateApiKeyRequestValidator : AbstractValidator<CreateApiKeyRequest>
{
    public CreateApiKeyRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Diga de quem é a chave — é como você vai reconhecê-la depois.")
            .MaximumLength(200);

        RuleFor(x => x.Scopes)
            .NotEmpty().WithMessage("Uma chave sem escopo nenhum não conseguiria fazer nada.");

        RuleForEach(x => x.Scopes).MustBeAKnownPermission();

        // A quota é um TETO de chamadas. Zero ou negativo criaria uma chave nascida morta —
        // ela autenticaria e seria recusada na primeira chamada, sem explicação óbvia.
        // (null = sem teto, e é válido.)
        RuleFor(x => x.CallLimit)
            .GreaterThan(0)
            .When(x => x.CallLimit is not null)
            .WithMessage("A quota precisa ser maior que zero (ou nula, para não ter teto).");

        RuleFor(x => x.ExpiresAt)
            .Must(date => date is null || date > DateTimeOffset.UtcNow)
            .WithMessage("A data de expiração precisa estar no futuro.");
    }
}
