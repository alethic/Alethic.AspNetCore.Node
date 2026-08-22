using System;

namespace Alethic.AspNetCore.EcmaScript.SpaServices.Routing.Exceptions;

public class SpaRouteNotFoundException : Exception
{

	public SpaRouteNotFoundException(string routeName) :
		base($"Route with name {routeName} not found.")
	{

	}

}
