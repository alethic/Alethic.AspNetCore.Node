using Alethic.AspNetCore.EcmaScript.Node.Hosting;
using Alethic.AspNetCore.EcmaScript.SpaServices.Extensions;
using Alethic.AspNetCore.EcmaScript.SpaServices.Prerendering;

namespace Sample.Vite.Server
{

	public static class Program
	{

		public static async Task Main(string[] args)
		{
			var builder = WebApplication.CreateBuilder(args);
			builder.Services.AddControllers();
			builder.Services.AddSpaStaticFilesImproved(o => o.RootPath = "../Sample.Vite.Client");

			var app = builder.Build();
			app.UseForwardedHeaders();
			app.UseSpaStaticFilesImproved();
			app.UseSpaImproved(spa =>
			{
				if (app.Environment.IsDevelopment())
					spa.Options.SourcePath = "../Sample.Vite.Client";

				spa.UseSpaPrerendering(ssr =>
				{
					ssr.BootModulePath = $"{spa.Options.SourcePath}/dist/server/entry-server.js";
				});
			});

			app.MapGroup("/").MapRazorPages

			app.MapSpa<IRouteProvider, IPrerenderer>("/prefix");
			app.MapViteReactRoutes("/reactapp", "routes.js", "entry-server.js");
			app.MapViteAngularRoutes("/angularapp", "idunno.js", "angular-server.js");

			app.MapStaticAssets();
			await app.RunAsync();
		}

	}

}
