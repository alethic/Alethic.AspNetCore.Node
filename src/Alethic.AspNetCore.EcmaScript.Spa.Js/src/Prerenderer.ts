import * as path from 'path';

// This function is invoked by .NET code (via NodeServices). Its job is to hand off execution to the application's
// prerendering boot function. It can operate in two modes:
// [1] Legacy mode
//     This is for backward compatibility with projects created with templates older than the generator version 0.6.0.
//     In this mode, we don't really do anything here - we just load the 'aspnet-prerendering' NPM module (which must
//     exist in node_modules, and must be v1.x (not v2+)), and pass through all the parameters to it. Code in
//     'aspnet-prerendering' v1.x will locate the boot function and invoke it.
//     The drawback to this mode is that, for it to work, you have to deploy node_modules to production.
// [2] Current mode
//     This is for projects created with the Yeoman generator 0.6.0+ (or projects manually updated). In this mode,
//     we don't invoke 'require' at runtime at all. All our dependencies are bundled into the NuGet package, so you
//     don't have to deploy node_modules to production.
// To determine whether we're in mode [1] or [2], the code locates your prerendering boot function, and checks whether
// a certain flag is attached to the function instance.
export async function renderToString(applicationBasePath, bootModule, absoluteRequestUrl, requestPathAndQuery, customDataParameter, overrideTimeoutMilliseconds) {
	try {
		const renderToStringFunc = await findRenderToStringFunc(applicationBasePath, bootModule);
		renderToStringFunc.apply(null, arguments);
	} catch (ex) {
		throw new Error('Prerendering failed because of error: ' + ex.stack + '\nCurrent directory is: ' + process.cwd());
	}
};

async function findBootModule(applicationBasePath, bootModule) {
	const bootModuleNameFullPath = path.resolve(applicationBasePath, bootModule.moduleName);
	return await import(bootModuleNameFullPath);
}

async function findRenderToStringFunc(applicationBasePath, bootModule) {
	// First try to load the module
	const foundBootModule = await findBootModule(applicationBasePath, bootModule);
	if (foundBootModule === null) {
		return null; // Must be legacy mode
	}

	// Now try to pick out the function they want us to invoke
	let renderToStringFunc;
	if (bootModule.exportName) {
		// Explicitly-named export
		renderToStringFunc = foundBootModule[bootModule.exportName];
	} else if (typeof foundBootModule !== 'function') {
		// TypeScript-style default export
		renderToStringFunc = foundBootModule.default;
	} else {
		// Native default export
		renderToStringFunc = foundBootModule;
	}

	// Validate the result
	if (typeof renderToStringFunc !== 'function') {
		if (bootModule.exportName) {
			throw new Error(`The module at ${bootModule.moduleName} has no function export named ${bootModule.exportName}.`);
		} else {
			throw new Error(`The module at ${bootModule.moduleName} does not export a default function, and you have not specified which export to invoke.`);
		}
	}

	return renderToStringFunc;
}
