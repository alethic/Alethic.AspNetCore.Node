using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.EcmaScript.Hosting.Http;

/// <summary>
/// Pulls a response body out of the engine, one chunk per read, through the stream's own reader.
/// </summary>
/// <remarks>
/// This is nothing but object operations: <c>getReader()</c> once, then <c>read()</c> per chunk,
/// each result awaited as the promise it is and its bytes copied out. The stream owns the session
/// and every handle the response acquired, so disposing it — which disposing the response content
/// does — returns the engine capacity and releases the pinned objects.
/// </remarks>
sealed class JavaScriptResponseBodyStream : Stream
{

	/// <summary>
	/// Opens the body's reader and wraps it.
	/// </summary>
	/// <param name="session"></param>
	/// <param name="body"></param>
	/// <param name="registration"></param>
	/// <param name="owned"></param>
	/// <param name="cancellationToken"></param>
	public static async Task<JavaScriptResponseBodyStream> OpenAsync(IJavaScriptSession session, IJavaScriptObject body, CancellationTokenRegistration registration, List<IJavaScriptObject> owned, CancellationToken cancellationToken)
	{
		owned.Add(body);
		var reader = (await body.InvokeAsync("getReader", [], cancellationToken)).AsObject();
		owned.Add(reader);
		return new JavaScriptResponseBodyStream(session, reader, registration, owned);
	}

	/// <summary>
	/// Wraps a response with no body at all, so disposal semantics hold regardless.
	/// </summary>
	/// <param name="session"></param>
	/// <param name="registration"></param>
	/// <param name="owned"></param>
	public static JavaScriptResponseBodyStream Empty(IJavaScriptSession session, CancellationTokenRegistration registration, List<IJavaScriptObject> owned) =>
		new(session, null, registration, owned);

	readonly IJavaScriptSession session;
	readonly IJavaScriptObject? reader;
	readonly CancellationTokenRegistration registration;
	readonly List<IJavaScriptObject> owned;

	byte[]? current;
	int offset;
	bool finished;
	int disposed;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="session"></param>
	/// <param name="reader"></param>
	/// <param name="registration"></param>
	/// <param name="owned"></param>
	JavaScriptResponseBodyStream(IJavaScriptSession session, IJavaScriptObject? reader, CancellationTokenRegistration registration, List<IJavaScriptObject> owned)
	{
		this.session = session;
		this.reader = reader;
		this.registration = registration;
		this.owned = owned;
		finished = reader is null;
	}

	/// <inheritdoc />
	public override bool CanRead => true;

	/// <inheritdoc />
	public override bool CanSeek => false;

	/// <inheritdoc />
	public override bool CanWrite => false;

	/// <inheritdoc />
	public override long Length => throw new NotSupportedException();

	/// <inheritdoc />
	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

	/// <inheritdoc />
	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		while (true)
		{
			// Serve out of the chunk in hand first; a JS chunk and a caller's buffer rarely agree on
			// size, and going back to the engine for bytes already here would be waste.
			if (current is not null)
			{
				var take = Math.Min(buffer.Length, current.Length - offset);
				current.AsMemory(offset, take).CopyTo(buffer);
				offset += take;

				if (offset >= current.Length)
				{
					current = null;
					offset = 0;
				}

				return take;
			}

			if (finished)
				return 0;

			// One pull: read() answers a promise of { done, value }.
			await using var pending = (await reader!.InvokeAsync("read", [], cancellationToken)).AsObject();
			var settled = await pending.AwaitAsync(cancellationToken);
			await using var result = settled.AsObject();

			if ((await result.GetAsync("done", cancellationToken)).AsBoolean())
			{
				finished = true;
				return 0;
			}

			var value = await result.GetAsync("value", cancellationToken);
			await using var chunk = value.AsObject();
			current = await chunk.ToByteArrayAsync(cancellationToken);
			offset = 0;
		}
	}

	/// <inheritdoc />
	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
		ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

	/// <inheritdoc />
	public override int Read(byte[] buffer, int offset, int count) =>
		ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

	/// <inheritdoc />
	public override void Flush()
	{
	}

	/// <inheritdoc />
	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

	/// <inheritdoc />
	public override void SetLength(long value) => throw new NotSupportedException();

	/// <inheritdoc />
	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

	/// <summary>
	/// Releases the handles, the registration, and the session, exactly once.
	/// </summary>
	async ValueTask ReleaseAsync()
	{
		if (Interlocked.Exchange(ref disposed, 1) != 0)
			return;

		await registration.DisposeAsync();

		foreach (var handle in owned)
			await handle.DisposeAsync();

		await session.DisposeAsync();
	}

	/// <inheritdoc />
	protected override void Dispose(bool disposing)
	{
		if (disposing)
			ReleaseAsync().AsTask().GetAwaiter().GetResult();

		base.Dispose(disposing);
	}

	/// <inheritdoc />
	public override async ValueTask DisposeAsync()
	{
		await ReleaseAsync();
		await base.DisposeAsync();
	}

}
