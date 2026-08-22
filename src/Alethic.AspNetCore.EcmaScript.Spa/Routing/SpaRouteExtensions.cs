using Alethic.AspNetCore.EcmaScript.SpaServices.Prerendering.Services;

using Microsoft.Extensions.DependencyInjection;

namespace Alethic.AspNetCore.EcmaScript.SpaServices.Routing;

public static class SpaRouteExtensions
{

	public static IServiceCollection AddSpaPrerenderingService<TService>(this IServiceCollection services)
		where TService : class, ISpaPrerenderingService
	{
		return services
			.AddHttpContextAccessor()
			.AddSingleton<ISpaRouteService, SpaRouteService>()
			.AddScoped<ISpaPrerenderingService, TService>();
	}

}
