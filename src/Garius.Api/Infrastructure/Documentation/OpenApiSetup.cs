using Garius.Api.Infrastructure.Authorization;
using Garius.Core.Machine;
using Microsoft.AspNetCore.OpenApi;

// Microsoft.OpenApi 2.x: os tipos vivem DIRETO neste namespace — o `.Models` da 1.x deixou de
// existir. (Ver o pin em Directory.Packages.props: a 2.x é obrigatória por causa de uma CVE, e
// a 3.x quebra o source generator do ASP.NET.)
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace Garius.Api.Infrastructure.Documentation;

/// <summary>
/// A documentação da API (OpenAPI + Scalar).
///
/// <para>
/// ⚠️ <b>Protegida por permissão (<c>docs.read</c>), inclusive em produção.</b> A documentação é
/// o <b>mapa completo</b> da aplicação: todos os endpoints, parâmetros, esquemas de autenticação
/// e códigos de erro. Servida a anônimos, é reconhecimento pronto — um atacante não precisa
/// descobrir nada, basta ler. Se a sua API for <b>deliberadamente pública</b>, troque por
/// <c>.AllowAnonymous()</c>; mas que seja uma decisão, não um esquecimento.
/// </para>
/// </summary>
internal static class OpenApiSetup
{
    private const string DocumentName = "v1";

    internal static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer<SecuritySchemesTransformer>();
        });

        return services;
    }

    internal static void MapApiDocumentation(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // O documento OpenAPI (o JSON) e a página do Scalar são a MESMA informação — proteger
        // só a página seria teatro: bastaria pedir o JSON direto. As duas exigem docs.read.
        app.MapOpenApi("/openapi/{documentName}.json")
           .RequirePermission(Core.Authorization.Permissions.Docs.Read);

        app.MapScalarApiReference("/scalar", options => options
            .WithTitle("Garius Backend Template")
            .WithTheme(ScalarTheme.BluePlanet)
            .AddDocument(DocumentName, routePattern: "/openapi/{documentName}.json"))
        .RequirePermission(Core.Authorization.Permissions.Docs.Read);
    }
}

/// <summary>
/// Declara os <b>três</b> esquemas de autenticação da API no documento OpenAPI.
///
/// <para>
/// <b>Sem isto o Scalar é uma vitrine.</b> O gerador de OpenAPI do .NET <b>não descobre</b> os
/// esquemas a partir dos handlers de autenticação registrados — ele não tem como saber que
/// existe um header <c>X-Api-Key</c>. O resultado é uma documentação bonita em que <b>nenhum
/// endpoint protegido pode ser testado</b>: não há onde colar o token, e todo "Send Request"
/// volta 401. É o tipo de defeito que passa despercebido porque a página <i>parece</i> certa.
/// </para>
/// </summary>
internal sealed class SecuritySchemesTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        // 1. Cookie — uma PESSOA, no navegador.
        //
        // Não há campo para preencher: o cookie é HttpOnly e o navegador o envia sozinho.
        // Declará-lo mesmo assim é o que faz a documentação dizer a verdade sobre COMO um
        // usuário se autentica — e o Scalar, aberto por quem já logou em /admin/login, envia
        // o cookie junto de qualquer forma.
        document.Components.SecuritySchemes["cookie"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = "__Host-garius.auth",
            Description =
                "Sessão de usuário (cookie HttpOnly). Emitido por POST /auth/login. " +
                "O navegador o envia automaticamente — não há nada a preencher aqui. " +
                "Requisições que alteram estado também exigem o header X-CSRF-Token."
        };

        // 2. Bearer — uma MÁQUINA (OAuth2 client credentials).
        document.Components.SecuritySchemes[MachineAuth.BearerScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description =
                "JWT de máquina. Obtenha-o em POST /auth/token, trocando client_id + " +
                "client_secret (OAuth2 client credentials). Vida curta (1h por padrão)."
        };

        // 3. X-Api-Key — um TERCEIRO.
        document.Components.SecuritySchemes[MachineAuth.ApiKeyScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = MachineAuth.ApiKeyHeader,
            Description =
                "Chave de API de longa duração, para integrações de terceiros. " +
                "Criada em POST /machine/api-keys e mostrada UMA ÚNICA VEZ."
        };

        // Os três valem para o documento inteiro, e são ALTERNATIVAS (não cumulativos).
        //
        // Cada item da lista é uma opção independente: "cookie OU Bearer OU X-Api-Key". Um
        // único item com as três chaves dentro significaria "cookie E Bearer E X-Api-Key,
        // todos juntos" — e aí o Scalar exigiria as três credenciais de uma vez para deixar
        // qualquer requisição sair.
        document.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("cookie", document)] = []
            },
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(MachineAuth.BearerScheme, document)] = []
            },
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(MachineAuth.ApiKeyScheme, document)] = []
            }
        ];

        document.Info = new OpenApiInfo
        {
            Title = "Garius Backend Template",
            Version = "v1",
            Description =
                "Três formas de autenticar, um só modelo de autorizar: cookie (pessoa), " +
                "Bearer (máquina) e X-Api-Key (terceiro). Um endpoint declara a PERMISSÃO que " +
                "exige, e serve as três.\n\n" +
                "**Contrato de resposta.** Sucesso vem num envelope (`{ success, data, traceId }`); " +
                "erro vem em ProblemDetails (RFC 9457). A ÚNICA exceção é `POST /auth/token`, " +
                "que segue a RFC 6749 (`access_token` na raiz) — é o que toda biblioteca OAuth " +
                "do mundo espera encontrar."
        };

        return Task.CompletedTask;
    }
}
