using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using RunbookAI.SystemApi.Configuration;

namespace RunbookAI.SystemApi.Infrastructure;

public static class BlobSetup
{
    public static IServiceCollection AddBlobStorage(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<BlobOptions>>().Value;
            var service = !string.IsNullOrEmpty(options.ConnectionString)
                ? new BlobServiceClient(options.ConnectionString)
                // Real Azure: no keys, the identity of the pod / developer (az login).
                : new BlobServiceClient(options.ServiceUri, new DefaultAzureCredential());
            return service.GetBlobContainerClient(options.ContainerName);
        });
        return services;
    }
}
