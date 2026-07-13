using Garius.Infrastructure.Database;
using Garius.Infrastructure.Identity;
using Serilog;

namespace Garius.Api.Infrastructure.Database;

/// <summary>
/// Executa o bootstrap (banco, roles, grants, migrations, tenant padrão) e encerra.
///
/// <para>
/// <b>Saída limpa é o ponto.</b> O template anterior chamava <c>Environment.Exit(0)</c> de
/// dentro de um serviço scoped: isso matava o processo sem executar o <c>Dispose</c> do
/// scope e <b>sem o flush do Serilog</b> — perdendo justamente os logs de erro do deploy
/// que estava falhando.
/// </para>
///
/// <para>
/// Aqui, o resultado sobe como código de retorno do <c>Program</c> e o
/// <c>Log.CloseAndFlushAsync()</c> sempre roda. O container morre com 0 (o compose libera
/// a API) ou com 1 (o <c>depends_on: service_completed_successfully</c> segura a API, que
/// é exatamente o que se quer quando a migration falha).
/// </para>
/// </summary>
internal static class MigrateRunner
{
    internal static async Task<int> RunAsync(WebApplicationBuilder builder)
    {
        // Sobe só o host mínimo: nada de pipeline HTTP, nada de Kestrel.
        using var host = builder.Build();

        try
        {
            Log.Information("Modo {Mode}: iniciando o bootstrap do banco", PersistenceExtensions.MigrateOnlyKey);

            using var scope = host.Services.CreateScope();

            var bootstrapper = scope.ServiceProvider.GetRequiredService<DatabaseBootstrapper>();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await bootstrapper.RunAsync(dbContext);

            // O primeiro usuário — depois do banco existir e das migrations rodarem, porque ele
            // é uma LINHA em tabelas que as migrations acabaram de criar.
            //
            // Aqui, e não no runtime: o container de migrations é ÚNICO, e N réplicas da API
            // semeando o mesmo usuário ao mesmo tempo é uma corrida.
            var adminSeeder = scope.ServiceProvider.GetRequiredService<BootstrapAdminSeeder>();

            await adminSeeder.SeedAsync();

            Log.Information("Bootstrap concluído com sucesso. Encerrando.");

            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "O bootstrap do banco FALHOU. A API não deve subir.");

            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}
