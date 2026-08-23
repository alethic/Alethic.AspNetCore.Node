using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.EcmaScript.Hosting.Http;

/// <summary>
/// Presents a stream response's body as a stream whose disposal disposes the whole response.
/// </summary>
/// <remarks>
/// The engine capacity a streaming call holds is released when its response is disposed, but HTTP
/// content types dispose only the stream they were given. This ties the two together, so the
/// ordinary dispose-the-content flow is enough to return the capacity.
/// </remarks>
sealed class StreamResponseStream : Stream
{

	readonly JavaScriptStreamResponse response;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="response"></param>
	public StreamResponseStream(JavaScriptStreamResponse response)
	{
		this.response = response ?? throw new ArgumentNullException(nameof(response));
	}

	/// <inheritdoc />
	public override bool CanRead => response.Body.CanRead;

	/// <inheritdoc />
	public override bool CanSeek => false;

	/// <inheritdoc />
	public override bool CanWrite => false;

	/// <inheritdoc />
	public override long Length => throw new NotSupportedException();

	/// <inheritdoc />
	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

	/// <inheritdoc />
	public override int Read(byte[] buffer, int offset, int count) => response.Body.Read(buffer, offset, count);

	/// <inheritdoc />
	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
		response.Body.ReadAsync(buffer, offset, count, cancellationToken);

	/// <inheritdoc />
	public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
		response.Body.ReadAsync(buffer, cancellationToken);

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

	/// <inheritdoc />
	protected override void Dispose(bool disposing)
	{
		if (disposing)
			response.DisposeAsync().AsTask().GetAwaiter().GetResult();

		base.Dispose(disposing);
	}

	/// <inheritdoc />
	public override async ValueTask DisposeAsync()
	{
		await response.DisposeAsync();
		await base.DisposeAsync();
	}

}
