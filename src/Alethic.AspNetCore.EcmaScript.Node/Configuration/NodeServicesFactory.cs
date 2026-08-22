// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;

namespace Alethic.AspNetCore.EcmaScript.Node;

/// <summary>
/// Supplies INodeServices instances.
/// </summary>
public static class NodeServicesFactory
{

	/// <summary>
	/// Create an <see cref="INodeService"/> instance according to the supplied options.
	/// </summary>
	/// <param name="options">Options for creating the <see cref="INodeService"/> instance.</param>
	/// <returns>An <see cref="INodeService"/> instance.</returns>
	public static INodeService CreateNodeServices(NodeServicesOptions options)
	{
		if (options == null)
			throw new ArgumentNullException(nameof(options));

		return new DefaultNodeService(options.NodeInstanceFactory);
	}

}
