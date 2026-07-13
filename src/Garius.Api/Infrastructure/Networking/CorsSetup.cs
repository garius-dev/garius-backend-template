namespace Garius.Api.Infrastructure.Networking;

/// <summary>
/// CORS com <b>falha fechada</b>.
///
/// <para>
/// O template anterior fazia <c>AllowAnyOrigin()</c> quando a lista de origens estava
/// vazia — e o <c>appsettings.json</c> vinha com a lista vazia. Ou seja: produção com a
/// configuração padrão abria CORS para qualquer origem. Um esquecimento de configuração
/// virava uma falha <b>aberta</b>.
/// </para>
///
/// <para>
/// Aqui, lista vazia em produção significa <b>nenhuma origem permitida</b>. Se o frontend
/// parar de funcionar, o erro é evidente e a correção é declarar a origem — o que é
/// infinitamente melhor do que uma API silenciosamente aberta ao mundo.
/// </para>
///
/// <para>
/// Nota: com o front em <c>app.dominio.com</c> e a API em <c>api.dominio.com</c> (mesmo
/// site), CORS quase não entra em jogo. Ele existe para o dev local e para eventuais
/// integrações de terceiros.
/// </para>
/// </summary>
internal static class CorsSetup
{
    internal const string PolicyName = "Default";

    internal static IServiceCollection AddConfiguredCors(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

        services.AddCors(options => options.AddPolicy(PolicyName, policy =>
        {
            if (origins.Length == 0)
            {
                // Nenhuma origem declarada -> nega tudo. Em Development isso normalmente
                // não acontece (o appsettings.Development.json declara localhost).
                // Uma API M2M pura legitimamente não precisa de CORS nenhum.
                return;
            }

            policy.WithOrigins(origins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  // Obrigatório para o cookie HttpOnly de auth viajar.
                  .AllowCredentials()
                  .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
        }));

        if (environment.IsProduction() && origins.Length == 0)
        {
            // Não derruba o boot: uma API M2M pura pode legitimamente não ter origem
            // de browser. Mas registra alto, porque na maioria das vezes é esquecimento.
            Serilog.Log.Warning(
                "CORS: nenhuma origem em Cors:AllowedOrigins. Toda requisição cross-origin de browser será negada. " +
                "Se existe um frontend, declare a origem.");
        }

        return services;
    }
}
