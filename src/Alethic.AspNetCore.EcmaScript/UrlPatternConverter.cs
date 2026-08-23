using System;

namespace Alethic.AspNetCore.EcmaScript;

/// <summary>
/// Converts URLPattern pathname syntax to ASP.NET route templates.
/// </summary>
/// <remarks>
/// The manifest speaks URLPattern — the WHATWG standard every framework's route syntax lowers to
/// mechanically — precisely so applications never learn ASP.NET's. This is the one place the
/// host-specific conversion happens. Only the subset ASP.NET templates can express converts:
/// literal segments, <c>:name</c> parameters, <c>:name?</c> optional parameters, and a trailing
/// <c>*</c> wildcard. A pattern using more — regex groups, modifiers, mid-path wildcards — answers
/// null and is served by the fallback endpoint instead, losing only its per-route policy.
/// </remarks>
static class UrlPatternConverter
{

	/// <summary>
	/// Converts a URLPattern pathname to an ASP.NET route template, or null where it does not
	/// translate.
	/// </summary>
	/// <param name="pattern"></param>
	public static string? ToRouteTemplate(string pattern)
	{
		ArgumentNullException.ThrowIfNull(pattern);

		if (pattern.Length == 0 || pattern[0] != '/')
			return null;

		if (pattern == "/")
			return "/";

		var segments = pattern.Substring(1).Split('/');
		var converted = new string[segments.Length];

		for (var i = 0; i < segments.Length; i++)
		{
			var segment = segments[i];

			// A bare wildcard swallows the rest of the path, so it only translates in final position.
			if (segment == "*")
			{
				if (i != segments.Length - 1)
					return null;

				converted[i] = "{**rest}";
				continue;
			}

			if (segment.Length > 1 && segment[0] == ':')
			{
				var name = segment.Substring(1);
				var optional = name.EndsWith('?');
				if (optional)
					name = name.Substring(0, name.Length - 1);

				if (IsName(name) == false)
					return null;

				converted[i] = optional ? "{" + name + "?}" : "{" + name + "}";
				continue;
			}

			// A literal segment, if it really is one: anything URLPattern treats as syntax means the
			// pattern uses a construct the template cannot carry.
			if (segment.AsSpan().IndexOfAny(":*?(){}[]\\+") >= 0)
				return null;

			converted[i] = segment;
		}

		return "/" + string.Join('/', converted);
	}

	/// <summary>
	/// True when the text is a well-formed parameter name.
	/// </summary>
	/// <param name="name"></param>
	static bool IsName(string name)
	{
		if (name.Length == 0)
			return false;

		if (char.IsLetter(name[0]) == false && name[0] != '_')
			return false;

		foreach (var c in name)
			if (char.IsLetterOrDigit(c) == false && c != '_')
				return false;

		return true;
	}

}
