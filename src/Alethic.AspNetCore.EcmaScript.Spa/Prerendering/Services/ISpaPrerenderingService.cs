using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;

namespace Alethic.AspNetCore.EcmaScript.SpaServices.Prerendering.Services;

public interface ISpaPrerenderingService
{
	Task BuildRoutes(ISpaRouteBuilder routeBuilder);
	Task OnSupplyData(HttpContext httpContext, IDictionary<string, object> data);
}
