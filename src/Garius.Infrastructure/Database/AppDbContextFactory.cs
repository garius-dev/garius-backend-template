using Garius.Infrastructure.Security;
using Garius.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;

namespace Garius.Infrastructure.Database;

/// <summary>
/// Usado apenas pelo <c>dotnet ef</c> em design-time (gerar/aplicar migrations pela CLI).
///
/// <para>
/// Não precisa de connection string nem de chaves reais: o EF só usa este contexto para
/// <b>comparar o modelo</b> e emitir o SQL da migration — nunca abre conexão, nunca cifra
/// nada. Os valores abaixo são placeholders válidos apenas em forma.
/// </para>
/// </summary>
internal sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <summary>32 bytes de zeros. Nunca cifra dado real — existe só para o modelo compilar.</summary>
    private static readonly string DesignTimeKey = Convert.ToBase64String(new byte[32]);

    public AppDbContext CreateDbContext(string[] args)
    {
        // Connection string de forma válida, mas que não precisa apontar para um banco real:
        // gerar uma migration só compara modelos. O `dotnet ef migrations remove`, porém,
        // TENTA conectar (para checar se a migration já foi aplicada) — então a senha pode
        // vir de DESIGN_TIME_DB_PASSWORD quando for preciso rodá-lo.
        var password = Environment.GetEnvironmentVariable("DESIGN_TIME_DB_PASSWORD") ?? "design_time";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql($"Host=localhost;Database=design_time;Username=postgres;Password={password}")
            .Options;

        var encryption = Options.Create(new EncryptionOptions
        {
            Keys = { [1] = DesignTimeKey },
            ActiveKeyVersion = 1,
            BlindIndexKey = DesignTimeKey
        });

        return new AppDbContext(
            options,
            new SystemTenantResolver(),
            new AesGcmFieldEncryptor(encryption));
    }
}
