using System;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Alethic.EcmaScript.Hosting;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Alethic.EcmaScript.Hosting.Node.Tests;

/// <summary>
/// Exercises the general streaming invocation with no HTTP anywhere in sight — the contract the
/// fetch adapter is itself built on.
/// </summary>
[Collection("Node")]
public class NodeStreamingTests
{

	/// <summary>
	/// A module that streams a numbered sequence: head describes the run, body carries the lines,
	/// and the signal stops production mid-run.
	/// </summary>
	const string SequenceModule = """
		module.exports.sequence = function (count, delayMs, payload, signal) {
			let produced = 0;
			const encoder = new TextEncoder();
			const body = new ReadableStream({
				async pull(controller) {
					if (produced >= count || signal.aborted) {
						controller.close();
						return;
					}
					await new Promise(r => setTimeout(r, delayMs));
					controller.enqueue(encoder.encode('line ' + produced++ + '\n'));
				},
			});
			return {
				head: { count, payloadBytes: payload ? payload.length : 0 },
				body,
			};
		};
		""";

	/// <summary>
	/// Builds a provider with one default pool on the embedded Node backend.
	/// </summary>
	static ServiceProvider BuildServices()
	{
		var services = new ServiceCollection();
		services.AddJavaScriptEnginePool().UseEmbeddedNode();
		return services.BuildServiceProvider();
	}

	/// <summary>
	/// Head shape the sequence module reports.
	/// </summary>
	sealed record SequenceHead(
		[property: JsonPropertyName("count")] int Count,
		[property: JsonPropertyName("payloadBytes")] int PayloadBytes);

	[Fact]
	public async Task Streaming_invocation_delivers_head_and_body()
	{
		await using var services = BuildServices();
		var pool = services.GetRequiredService<IJavaScriptEnginePoolProvider>().Get("Default");
		var module = pool.GetModule(JavaScriptModuleSource.FromText("seq.cjs", SequenceModule));

		await using var response = await module.InvokeStreamAsync("sequence", [3, 1], new byte[] { 1, 2, 3, 4 }, CancellationToken.None);

		var head = response.GetHead<SequenceHead>();
		Assert.NotNull(head);
		Assert.Equal(3, head.Count);
		Assert.Equal(4, head.PayloadBytes);

		using var reader = new StreamReader(response.Body, Encoding.UTF8);
		Assert.Equal("line 0\nline 1\nline 2\n", await reader.ReadToEndAsync());
	}

	[Fact]
	public async Task Cancellation_reaches_the_producer_through_the_signal()
	{
		await using var services = BuildServices();
		var pool = services.GetRequiredService<IJavaScriptEnginePoolProvider>().Get("Default");
		var module = pool.GetModule(JavaScriptModuleSource.FromText("seq.cjs", SequenceModule));

		using var cts = new CancellationTokenSource();

		// A long slow run; cancel once the first line proves it started. The producer checks its
		// signal per pull, so the stream must end long before the thousand lines would have.
		await using var response = await module.InvokeStreamAsync("sequence", [1000, 10], null, cts.Token);

		var buffer = new byte[64];
		var first = await response.Body.ReadAsync(buffer, CancellationToken.None);
		Assert.True(first > 0);

		cts.Cancel();

		var total = 0;
		while (true)
		{
			var read = await response.Body.ReadAsync(buffer, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
			if (read == 0)
				break;

			total += read;
		}

		// Well short of the full run: 1000 lines at 10ms would be ten seconds and ~8KB.
		Assert.True(total < 1024, $"the producer ran on after cancellation ({total} bytes)");
	}

}
