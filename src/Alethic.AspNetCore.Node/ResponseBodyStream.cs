using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.AspNetCore.Node;

/// <summary>
/// The rendered body: the pipe's reading side, owning the lease behind the render.
/// </summary>
/// <remarks>
/// Disposal releases the lease and observes the pump, so the ordinary dispose-the-response flow is
/// what returns the engine capacity — and a render abandoned mid-body neither leaks its claim nor
/// leaves its fault unobserved.
/// </remarks>
sealed class ResponseBodyStream : Stream
{

	readonly Stream inner;
	readonly NodeEngineLease lease;
	readonly Task pump;

	int disposed;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="inner"></param>
	/// <param name="lease"></param>
	/// <param name="pump"></param>
	public ResponseBodyStream(Stream inner, NodeEngineLease lease, Task pump)
	{
		this.inner = inner;
		this.lease = lease;
		this.pump = pump;
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
	public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
		inner.ReadAsync(buffer, cancellationToken);

	/// <inheritdoc />
	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
		inner.ReadAsync(buffer, offset, count, cancellationToken);

	/// <inheritdoc />
	public override int Read(byte[] buffer, int offset, int count) =>
		inner.Read(buffer, offset, count);

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
	/// Releases the lease and observes the pump, exactly once.
	/// </summary>
	async ValueTask ReleaseAsync()
	{
		if (Interlocked.Exchange(ref disposed, 1) != 0)
			return;

		await inner.DisposeAsync();

		try
		{
			// Disposing the reader is what unblocks a still-running pump; its fault, if any, was
			// already delivered through the head or the body, so here it is only observed.
			await pump;
		}
		catch
		{
		}

		await lease.DisposeAsync();
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
