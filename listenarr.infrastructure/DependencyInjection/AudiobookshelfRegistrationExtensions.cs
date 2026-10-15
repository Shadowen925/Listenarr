using Listenarr.Application.Audiobookshelf;
using Listenarr.Application.Audiobookshelf.Contracts;
using Listenarr.Infrastructure.Audiobookshelf;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.DependencyInjection;

internal static class AudiobookshelfRegistrationExtensions
{
    public static IServiceCollection AddAudiobookshelfHttpClient(this IServiceCollection services)
    {
        services.AddHttpClient<IAudiobookshelfClient, AudiobookshelfApiClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false
            });
        return services;
    }

    public static IServiceCollection AddAudiobookshelfIntegration(this IServiceCollection services)
    {
        services.AddScoped<IAudiobookshelfIntegrationService, AudiobookshelfIntegrationService>();
        return services;
    }
}
