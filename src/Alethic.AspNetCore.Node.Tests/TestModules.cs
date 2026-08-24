using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Alethic.AspNetCore.Node.Tests;

/// <summary>
/// Module sources built from text, for tests.
/// </summary>
/// <remarks>
/// Node loads modules from disk, so text has to become a file before it can be one. That is test
/// convenience rather than library surface — a test reads better with its module beside its
/// assertions than in a directory of fixtures — and it lives here so the library does not carry it
/// for a use case nobody has.
/// </remarks>
static class TestModules
{

	/// <summary>
	/// Writes module text to a temporary file and returns a source over it.
	/// </summary>
	/// <remarks>
	/// Named for a hash of the text, so identical text is one file and one module, and text that
	/// differs is never mistaken for the same module however it is named.
	/// </remarks>
	/// <param name="name">Name reported in stack traces.</param>
	/// <param name="text"></param>
	public static NodeModuleSource FromText(string name, string text)
	{
		ArgumentNullException.ThrowIfNull(name);
		ArgumentNullException.ThrowIfNull(text);

		var directory = Path.Combine(Path.GetTempPath(), "alethic-aspnetcore-node-tests");
		Directory.CreateDirectory(directory);

		var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
		var file = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(name)}-{hash}.cjs");

		// Content-addressed, so a concurrent writer is writing identical bytes; a rename into place
		// keeps a half-written file from ever being loadable.
		if (File.Exists(file) == false)
		{
			var pending = $"{file}.{Environment.CurrentManagedThreadId}.tmp";
			File.WriteAllText(pending, text);

			try
			{
				File.Move(pending, file, overwrite: false);
			}
			catch (IOException) when (File.Exists(file))
			{
				File.Delete(pending);
			}
		}

		return NodeModuleSource.FromFile(file);
	}

}
