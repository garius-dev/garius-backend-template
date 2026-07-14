using System.Reflection;
using Garius.Api.Infrastructure.Security;
using Garius.Core.Security;
using Garius.Infrastructure.Database;
using Garius.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Garius.Tests.Database;

/// <summary>
/// Prova que o grafo de serviços do modo <c>MIGRATE_ONLY</c> — o modo com que o container de
/// migrations roda em TODO deploy — <b>valida</b>.
///
/// <para>
/// <b>Por que este teste existe.</b> O <see cref="BootstrapTests"/> chama o
/// <c>DatabaseBootstrapper</c> <b>direto</b>, sem passar pelo <c>Program.cs</c>. E a suíte
/// inteira sobe a app no modo NORMAL. Resultado: o caminho real do deploy não era exercitado
/// por nenhum teste — e estava <b>quebrado</b>.
/// </para>
///
/// <para>
/// O <c>ICurrentUser</c> era sempre registrado como <c>HttpCurrentUser</c>, que depende do
/// <c>IPermissionResolver</c> — registrado só DEPOIS do <c>return</c> do modo migração. O
/// <c>builder.Build()</c> estourava na validação do container, o bootstrap não rodava, e
/// <b>nenhuma app derivada conseguia criar o próprio banco</b>. A suíte, verde.
/// </para>
///
/// <para>
/// O teste monta o MESMO grafo mínimo que o <c>Program</c> monta antes do <c>return</c>, e o
/// valida com <c>ValidateOnBuild</c> — que é exatamente a checagem que falhava. Não usa a
/// <c>ApiFactory</c> de propósito: a <c>WebApplicationFactory</c> não sabe lidar com um
/// <c>Program</c> que retorna sem construir a <c>WebApplication</c> (ela morre com
/// <c>ObjectDisposedException</c>, um artefato do harness, não o bug real).
/// </para>
/// </summary>
public class MigrateOnlyBootTests
{
    [Fact]
    public void O_grafo_do_MIGRATE_ONLY_valida()
    {
        var services = BuildMigrateOnlyGraph();

        // ValidateOnBuild é a checagem que falhava: ela percorre TODOS os descritores e tenta
        // construir o call site de cada um. Sem ela, o container só quebraria ao RESOLVER o
        // serviço — e um teste que não a ligasse passaria com o bug presente.
        var exception = Record.Exception(() =>
            services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            }));

        Assert.Null(exception);
    }

    /// <summary>
    /// O <c>ICurrentUser</c> do bootstrap não pode ser o <c>HttpCurrentUser</c>: no bootstrap
    /// não existe HTTP, nem requisição, nem usuário — e ele arrasta o <c>IPermissionResolver</c>
    /// (e, atrás dele, o Redis e a pilha de autorização) para dentro de um processo que só
    /// deveria criar banco e aplicar migrations.
    /// </summary>
    [Fact]
    public void O_ICurrentUser_do_bootstrap_e_o_SystemCurrentUser()
    {
        var services = BuildMigrateOnlyGraph();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(ICurrentUser));

        Assert.Equal(typeof(SystemCurrentUser), descriptor.ImplementationType);
    }

    /// <summary>
    /// O usuário do bootstrap NEGA toda permissão — falha fechada.
    ///
    /// <para>
    /// A alternativa (devolver <c>true</c>, "é o sistema, pode tudo") criaria um superusuário
    /// implícito que ninguém escreveu e ninguém revisou. O bootstrap cria schema e roles; ele
    /// não tem por que perguntar "posso?".
    /// </para>
    /// </summary>
    [Fact]
    public async Task O_usuario_do_bootstrap_nao_tem_usuario_nem_permissao()
    {
        var system = new SystemCurrentUser();

        Assert.Null(system.UserId);
        Assert.Null(system.ClientIp);

        Assert.False(await system.HasPermissionAsync("*", TestContext.Current.CancellationToken));
        Assert.False(await system.HasPermissionAsync("users.read", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Reproduz o grafo que o <c>Program</c> monta em MIGRATE_ONLY: tudo o que vem ANTES do
    /// <c>return await MigrateRunner.RunAsync(builder)</c>, e nada do que vem depois.
    /// </summary>
    private static ServiceCollection BuildMigrateOnlyGraph()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // O bootstrap não conecta aqui — o grafo só precisa ser CONSTRUÍVEL.
                ["Database:Host"] = "localhost",
                ["Database:RootPassword"] = "irrelevante-para-a-validacao",
                ["Database:AppPassword"] = "irrelevante-para-a-validacao",
                ["Database:ApplicationName"] = "Tests.MigrateOnly",

                // Chaves reais (32 bytes): o AddFieldEncryption falha no boot sem elas — e é
                // ele que exige o ICurrentUser, que é onde o bug morava.
                ["Encryption:Keys:1"] = "ZFbLDHAltmKIu1ANyNd7XyLre4jRiwYwKWjL8Lrn7nU=",
                ["Encryption:ActiveKeyVersion"] = "1",
                ["Encryption:BlindIndexKey"] = "ywIgmu+JbmkZ2HMcpLnWgheAF0CxDQlVZrRjT3VpaO4=",
            })
            .Build();

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddHttpContextAccessor();

        // A LINHA QUE O BUG TROUXE À TONA. Em MIGRATE_ONLY é o SystemCurrentUser — o
        // HttpCurrentUser exigiria o IPermissionResolver, que não existe neste grafo.
        services.AddScoped<ICurrentUser, SystemCurrentUser>();

        services.AddFieldEncryption(configuration);

        // AddPersistence é quem registra o Identity (para o BootstrapAdminSeeder criar o
        // primeiro usuário) e o DatabaseBootstrapper. É ele que precisa validar SOZINHO — sem
        // Redis, sem DataProtection, sem a pilha de autorização, nada disso existe no bootstrap.
        services.AddPersistence(
            configuration,
            Assembly.GetAssembly(typeof(Program))!,
            migrateOnly: true);

        return services;
    }
}
