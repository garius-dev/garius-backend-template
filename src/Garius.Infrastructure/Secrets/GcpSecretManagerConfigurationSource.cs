using Microsoft.Extensions.Configuration;

namespace Garius.Infrastructure.Secrets;

internal sealed class GcpSecretManagerConfigurationSource(GcpSecretManagerOptions options)
    : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new GcpSecretManagerConfigurationProvider(options);
}
