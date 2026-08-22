// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

using Alethic.AspNetCore.EcmaScript.SpaServices.Abstractions;
using Alethic.AspNetCore.EcmaScript.SpaServices.Core;

using Microsoft.AspNetCore.Builder;

namespace Alethic.AspNetCore.EcmaScript.SpaServices.Internal;

internal sealed class DefaultSpaBuilder : ISpaBuilder
{

	public IApplicationBuilder ApplicationBuilder { get; }

	public ISpaOptions Options { get; }

	public DefaultSpaBuilder(IApplicationBuilder applicationBuilder, SpaOptions options)
	{
		ApplicationBuilder = applicationBuilder ?? throw new ArgumentNullException(nameof(applicationBuilder));
		Options = options ?? throw new ArgumentNullException(nameof(options));
	}

}
