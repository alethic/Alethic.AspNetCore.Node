using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using Alethic.EcmaScript.Hosting.Http;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;

namespace Alethic.AspNetCore.EcmaScript;

/// <summary>
/// Translates between the ASP.NET request and the application's fetch contract.
/// </summary>
static class JavaScriptRequestDispatcher
{

	/// <summary>
	/// Serves one HTTP request from the JavaScript application.
	/// </summary>
	/// <remarks>
	/// The response is copied as it is produced, so a page that emits its shell ahead of suspended
	/// content reaches the client that way rather than all at once. The client going away aborts the
	/// render through the request's cancellation, not merely the copy.
	/// </remarks>
	/// <param name="context"></param>
	/// <param name="application"></param>
	public static async Task DispatchAsync(HttpContext context, IJavaScriptHttpApplication application)
	{
		using var request = BuildRequest(context);
		using var response = await application.SendAsync(request, context.RequestAborted);

		context.Response.StatusCode = (int)response.StatusCode;

		foreach (var header in response.Headers)
			context.Response.Headers[header.Key] = header.Value.ToArray();

		foreach (var header in response.Content.Headers)
		{
			// The body arrives as a stream of unknown length, and the server frames it itself; a
			// length or transfer coding copied from the module would claim otherwise.
			if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
				continue;
			if (string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
				continue;

			context.Response.Headers[header.Key] = header.Value.ToArray();
		}

		await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);

		// Copied chunk by chunk with a flush per chunk, so progress made by the render is progress
		// seen by the client. A plain copy would batch on the server's own buffer instead.
		var buffer = new byte[16 * 1024];
		while (true)
		{
			var read = await body.ReadAsync(buffer, context.RequestAborted);
			if (read == 0)
				break;

			await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
			await context.Response.Body.FlushAsync(context.RequestAborted);
		}
	}

	/// <summary>
	/// Rebuilds the incoming request in the shape the module's fetch expects.
	/// </summary>
	/// <param name="context"></param>
	static HttpRequestMessage BuildRequest(HttpContext context)
	{
		var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), context.Request.GetEncodedUrl());

		if (context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0)
			request.Content = new StreamContent(context.Request.Body);

		foreach (var header in context.Request.Headers)
		{
			// The authority travels in the URL; a Host header besides it is at best redundant and at
			// worst contradicts a PathBase-adjusted address.
			if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
				continue;

			if (request.Headers.TryAddWithoutValidation(header.Key, (string?[])header.Value) == false)
				request.Content?.Headers.TryAddWithoutValidation(header.Key, (string?[])header.Value);
		}

		return request;
	}

}
