using Alethic.AspNetCore.EcmaScript;

using Xunit;

namespace Alethic.AspNetCore.EcmaScript.Tests;

/// <summary>
/// Exercises the one place URLPattern meets ASP.NET template syntax. Pure logic; no engine.
/// </summary>
public class UrlPatternConverterTests
{

	[Theory]
	[InlineData("/", "/")]
	[InlineData("/about", "/about")]
	[InlineData("/parks/:parkRef", "/parks/{parkRef}")]
	[InlineData("/parks/:parkRef/maps/:mapId", "/parks/{parkRef}/maps/{mapId}")]
	[InlineData("/docs/:page?", "/docs/{page?}")]
	[InlineData("/files/*", "/files/{**rest}")]
	[InlineData("/a-b/c.d", "/a-b/c.d")]
	public void Expressible_patterns_convert(string pattern, string expected)
	{
		Assert.Equal(expected, UrlPatternConverter.ToRouteTemplate(pattern));
	}

	[Theory]
	[InlineData("")]                       // not a pathname
	[InlineData("about")]                  // must be rooted
	[InlineData("/id/:num(\\d+)")]         // regex group
	[InlineData("/tags/:tag+")]            // repeat modifier
	[InlineData("/tags/:tag*")]            // repeat modifier
	[InlineData("/a/*/b")]                 // wildcard not in final position
	[InlineData("/{/books}?")]             // group syntax
	[InlineData("/a/b{c}d")]               // group syntax inside a segment
	[InlineData("/x/:")]                   // parameter without a name
	[InlineData("/x/:1bad")]               // parameter name cannot start with a digit
	public void Inexpressible_patterns_answer_null(string pattern)
	{
		// Null is a contract, not a failure: the mapper leaves such routes to the fallback, where the
		// application's own router still renders the right thing.
		Assert.Null(UrlPatternConverter.ToRouteTemplate(pattern));
	}

}
