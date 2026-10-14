using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Reader;

public static class ServiceCollectionExtension
{
    public static IServiceCollection AddServices(this IServiceCollection services)
    {
        services.AddSingleton(_ =>
        {
            var readerSettings = new OpenApiReaderSettings();
            readerSettings.AddYamlReader();
            return readerSettings;
        });
        
        return services;
        
    }
}
