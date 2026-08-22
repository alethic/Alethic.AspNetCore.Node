// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using Alethic.AspNetCore.EcmaScript.Node;
using Alethic.AspNetCore.EcmaScript.SpaServices.Abstractions;

namespace Alethic.AspNetCore.EcmaScript.SpaServices.Prerendering;

/// <summary>
/// Extension methods for configuring prerendering of a Single Page Application.
/// </summary>
public static class SpaPrerenderingExtensions
{

	/// <summary>
	/// Enables server-side prerendering middleware for a Single Page Application.
	/// </summary>
	/// <param name="spaBuilder">The <see cref="ISpaBuilder"/>.</param>
	/// <param name="configuration">Supplies configuration for the prerendering middleware.</param>
	public static IApplicationBuilder UseSpaPrerendering(this ISpaBuilder spaBuilder, Action<SpaPrerenderingOptions> configuration)
	{
		// This is not an extension method on ISpaBuilder, but our own ISpaBuilder
		// This way applications won't take the wrong extension method, but always use this one instead
		if (spaBuilder == null)
			throw new ArgumentNullException(nameof(spaBuilder));
		if (configuration == null)
			throw new ArgumentNullException(nameof(configuration));

		var options = new SpaPrerenderingOptions();
		configuration.Invoke(options);

		var capturedBootModulePath = options.BootModulePath;
		if (string.IsNullOrEmpty(capturedBootModulePath))
			throw new InvalidOperationException($"To use {nameof(UseSpaPrerendering)}, you " +
				$"must set a nonempty value on the ${nameof(SpaPrerenderingOptions.BootModulePath)} " +
				$"property on the ${nameof(SpaPrerenderingOptions)}.");

		//// If we're building on demand, start that process in the background now
		//var buildOnDemandTask = options.BootModuleBuilder?.Build(spaBuilder);
		var isBuildStarted = false;

		// Get all the necessary context info that will be used for each prerendering call
		var applicationBuilder = spaBuilder.ApplicationBuilder;
		var serviceProvider = applicationBuilder.ApplicationServices;
		var nodeServices = GetNodeServices(serviceProvider, opts => opts.NodePath = options.NodePath);
		var applicationStoppingToken = serviceProvider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
		var applicationBasePath = serviceProvider.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
		var moduleExport = new JavaScriptModuleExport(capturedBootModulePath);
		var excludePathStrings = (options.ExcludeUrls ?? []).Select(url => new PathString(url)).ToArray();
		var buildTimeout = spaBuilder.Options.StartupTimeout;

		applicationBuilder.Use(async (context, next) =>
		{
			context.Response.OnStarting(async () =>
			{
				if (options.OnPrepareResponse != null)
					await options.OnPrepareResponse(context);
			});

			// If this URL is excluded, skip prerendering.
			// This is typically used to ensure that static client-side resources
			// (e.g., /dist/*.css) are served normally or through SPA development
			// middleware, and don't return the prerendered index.html page.
			foreach (var excludePathString in excludePathStrings)
			{
				if (context.Request.Path.StartsWithSegments(excludePathString))
				{
					await next();
					return;
				}
			}

			if ((options.BootModuleBuilder != null) && !isBuildStarted)
			{
				isBuildStarted = true;
				Console.WriteLine("Building server BootModule");
				await options.BootModuleBuilder.Build(spaBuilder);
				Console.WriteLine("Finished building server BootModule");
			}

			// It's no good if we try to return a 304. We need to capture the actual
			// HTML content so it can be passed as a template to the prerenderer.
			RemoveConditionalRequestHeaders(context.Request);

			// Make sure we're not capturing compressed content, because then we'd have
			// to decompress it. Since this sub-request isn't leaving the machine, there's
			// little to no benefit in having compression on it.
			var originalAcceptEncodingValue = GetAndRemoveAcceptEncodingHeader(context.Request);

			// Capture the non-prerendered responses, which in production will typically only
			// be returning the default SPA index.html page (because other resources will be
			// served statically from disk). We will use this as a template in which to inject
			// the prerendered output.
			using (var outputBuffer = new MemoryStream())
			{
				var originalResponseStream = context.Response.Body;
				context.Response.Body = outputBuffer;

				try
				{
					await next();
					outputBuffer.Seek(0, SeekOrigin.Begin);
				}
				finally
				{
					context.Response.Body = originalResponseStream;

					if (!string.IsNullOrEmpty(originalAcceptEncodingValue))
					{
						context.Request.Headers[HeaderNames.AcceptEncoding] = originalAcceptEncodingValue;
					}
				}

				// If it isn't an HTML page that we can use as the template for prerendering,
				//  - ... because it's not text/html
				//  - ... or because it's an error
				// then prerendering doesn't apply to this request, so just pass through the
				// response as-is. Note that the non-text/html case is not an error: this is
				// typically how the SPA dev server responses for static content are returned
				// in development mode.
				var canPrerender = IsSuccessStatusCode(context.Response.StatusCode) && IsHtmlContentType(context.Response.ContentType);
				if (canPrerender == false)
				{
					await outputBuffer.CopyToAsync(context.Response.Body);
					return;
				}

				// Most prerendering logic will want to know about the original, unprerendered
				// HTML that the client would be getting otherwise. Typically this is used as
				// a template from which the fully prerendered page can be generated.
				var customData = new Dictionary<string, object>
				{
					{ "originalHtml", Encoding.UTF8.GetString(outputBuffer.GetBuffer()) }
				};

				// If the developer wants to use custom logic to pass arbitrary data to the
				// prerendering JS code (e.g., to pass through cookie data), now's their chance
				var spaPrerenderingService = context.RequestServices.GetService<Services.ISpaPrerenderingService>();
				if (spaPrerenderingService != null)
					await spaPrerenderingService.OnSupplyData(context, customData);

				// Don't do SSR when we have a redirect
				if (IsSuccessStatusCode(context.Response.StatusCode) == false)
				{
					await outputBuffer.CopyToAsync(context.Response.Body);
					return;
				}

				var (unencodedAbsoluteUrl, unencodedPathAndQuery) = GetUnencodedUrlAndPathQuery(context);
				var renderResult = await Prerenderer.RenderToString(
					applicationBasePath,
					nodeServices,
					applicationStoppingToken,
					moduleExport,
					unencodedAbsoluteUrl,
					unencodedPathAndQuery,
					customDataParameter: customData,
					timeoutMilliseconds: options.TimeoutMilliseconds,
					requestPathBase: context.Request.PathBase.ToString());

				await ServePrerenderResult(context, renderResult);
			}
		});
		return applicationBuilder;
	}

	static bool IsHtmlContentType(string contentType)
	{
		if (string.Equals(contentType, "text/html", StringComparison.Ordinal))
		{
			return true;
		}

		return contentType != null
			&& contentType.StartsWith("text/html;", StringComparison.Ordinal);
	}

	static bool IsSuccessStatusCode(int statusCode)
	   => statusCode >= 200 && statusCode < 300;

	static void RemoveConditionalRequestHeaders(HttpRequest request)
	{
		request.Headers.Remove(HeaderNames.IfMatch);
		request.Headers.Remove(HeaderNames.IfModifiedSince);
		request.Headers.Remove(HeaderNames.IfNoneMatch);
		request.Headers.Remove(HeaderNames.IfUnmodifiedSince);
		request.Headers.Remove(HeaderNames.IfRange);
	}

	static string? GetAndRemoveAcceptEncodingHeader(HttpRequest request)
	{
		var headers = request.Headers;
		var value = (string?)null;

		if (headers.ContainsKey(HeaderNames.AcceptEncoding))
		{
			value = headers[HeaderNames.AcceptEncoding];
			headers.Remove(HeaderNames.AcceptEncoding);
		}

		return value;
	}

	static (string, string) GetUnencodedUrlAndPathQuery(HttpContext httpContext)
	{
		// This is a duplicate of code from Prerenderer.cs in the SpaServices package.
		// Once the SpaServices.Extension package implementation gets merged back into
		// SpaServices, this duplicate can be removed. To remove this, change the code
		// above that calls Prerenderer.RenderToString to use the internal overload
		// that takes an HttpContext instead of a url/path+query pair.
		var requestFeature = httpContext.Features.Get<IHttpRequestFeature>();
		var unencodedPathAndQuery = requestFeature.RawTarget;
		var request = httpContext.Request;
		var unencodedAbsoluteUrl = $"{request.Scheme}://{request.Host}{unencodedPathAndQuery}";
		return (unencodedAbsoluteUrl, unencodedPathAndQuery);
	}

	static async Task ServePrerenderResult(HttpContext context, IPrerendererResult renderResult)
	{
		context.Response.Clear();

		if (!string.IsNullOrEmpty(renderResult.RedirectUrl))
		{
			var permanentRedirect = renderResult.StatusCode.GetValueOrDefault() == 301;
			context.Response.Redirect(renderResult.RedirectUrl, permanentRedirect);
		}
		else
		{
			if (renderResult.StatusCode.HasValue)
				context.Response.StatusCode = renderResult.StatusCode.Value;

			context.Response.ContentType = "text/html";
			await context.Response.WriteAsync(renderResult.Html);
		}
	}

	/// <summary>
	/// Gets the configured <see cref="INodeService"/>.
	/// </summary>
	/// <param name="serviceProvider"></param>
	/// <param name="optionAction"></param>
	/// <returns></returns>
	static INodeService GetNodeServices(IServiceProvider serviceProvider, Action<NodeServicesOptions> optionAction)
	{
		// Use the registered instance, or create a new private instance if none is registered
		var instance = serviceProvider.GetService<INodeService>();
		if (instance == null)
		{
			// initialize options
			var opts = new NodeServicesOptions(serviceProvider);
			optionAction(opts);

			// initialize node services
			var result = NodeServicesFactory.CreateNodeServices(opts);
			return result;
		}
		else
		{
			return instance;
		}

	}

}
