using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FikenHjelper.Core.Fiken;

public static class FikenServiceCollectionExtensions
{
    /// <summary>
    /// Registrerer <see cref="IFikenClient"/> med Fikens egen sekvensielle rate-limiting
    /// (maks 1 samtidig kall, minst 260 ms mellomrom, begrensede 429-retries).
    /// Ikke legg på <c>AddStandardResilienceHandler</c> eller andre retry-/hedging-handlere her.
    /// </summary>
    public static IHttpClientBuilder AddFikenApiClient(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<FikenRateLimiter>();
        services.TryAddTransient<FikenRateLimitHandler>();

        return services.AddHttpClient<IFikenClient, FikenClient>(client =>
        {
            client.BaseAddress = new Uri(FikenClient.DefaultBaseAddress);
            client.Timeout = TimeSpan.FromSeconds(30);
        }).AddHttpMessageHandler<FikenRateLimitHandler>();
    }
}
