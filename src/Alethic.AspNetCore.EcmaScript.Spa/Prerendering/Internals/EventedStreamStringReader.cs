using System;
using System.Text;

namespace Alethic.AspNetCore.EcmaScript.SpaServices.Prerendering.Internals;

/// <summary>
/// Captures the completed-line notifications from a <see cref="EventedStreamReader"/>,
/// combining the data into a single <see cref="string"/>.
/// </summary>
internal sealed class EventedStreamStringReader : IDisposable
{

	readonly EventedStreamReader _eventedStreamReader;
	bool _isDisposed;
	readonly StringBuilder _stringBuilder = new StringBuilder();

	public EventedStreamStringReader(EventedStreamReader eventedStreamReader)
	{
		_eventedStreamReader = eventedStreamReader ?? throw new ArgumentNullException(nameof(eventedStreamReader));
		_eventedStreamReader.OnReceivedLine += OnReceivedLine;
	}

	public string ReadAsString() => _stringBuilder.ToString();

	void OnReceivedLine(string line) => _stringBuilder.AppendLine(line);

	public void Dispose()
	{
		if (!_isDisposed)
		{
			_eventedStreamReader.OnReceivedLine -= OnReceivedLine;
			_isDisposed = true;
		}
	}

}
