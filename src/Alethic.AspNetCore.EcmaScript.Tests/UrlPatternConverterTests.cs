using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Alethic.AspNetCore.EcmaScript.Tests;

/// <summary>
/// Exercises the one place URLPattern meets ASP.NET template syntax. Pure logic; no engine.
/// </summary>
[TestClass]
public class UrlPatternConverterTests
{

	[TestMethod]
	[DataRow("/", "/")]
	[DataRow("/about", "/about")]
	[DataRow("/parks/:parkRef", "/parks/{parkRef}")]
	[DataRow("/parks/:parkRef/maps/:mapId", "/parks/{parkRef}/maps/{mapId}")]
	[DataRow("/docs/:page?", "/docs/{page?}")]
	[DataRow("/files/*", "/files/{**rest}")]
	[DataRow("/a-b/c.d", "/a-b/c.d")]
	public void Expressible_patterns_convert(string pattern, string expected)
	{
		Assert.AreEqual(expected, UrlPatternConverter.ToRouteTemplate(pattern));
	}

	[TestMethod]
	[DataRow("")]                       // not a pathname
	[DataRow("about")]                  // must be rooted
	[DataRow("/id/:num(\\d+)")]         // regex group
	[DataRow("/tags/:tag+")]            // repeat modifier
	[DataRow("/tags/:tag*")]            // repeat modifier
	[DataRow("/a/*/b")]                 // wildcard not in final position
	[DataRow("/{/books}?")]             // group syntax
	[DataRow("/a/b{c}d")]               // group syntax inside a segment
	[DataRow("/x/:")]                   // parameter without a name
	[DataRow("/x/:1bad")]               // parameter name cannot start with a digit
	public void Inexpressible_patterns_answer_null(string pattern)
	{
		// Null is a contract, not a failure: the mapper leaves such routes to the fallback, where the
		// application's own router still renders the right thing.
		Assert.IsNull(UrlPatternConverter.ToRouteTemplate(pattern));
	}

}
