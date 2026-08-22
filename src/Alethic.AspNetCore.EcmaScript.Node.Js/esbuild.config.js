import esbuild from 'esbuild';
import esbuildPluginTsc from 'esbuild-plugin-tsc';

esbuild.build({
	entryPoints: ['src/HttpNodeInstanceEntryPoint.ts'],
	outfile: 'dist/entrypoint-http.js',
	bundle: true,
	minify: true,
	sourcemap: true,
	platform: 'node',
	plugins: [
		esbuildPluginTsc({
			force: true
		}),
	]
});

esbuild.build({
	entryPoints: ['src/InProcessNodeInstanceEntryPoint.ts'],
	outfile: 'dist/entrypoint-inproc.js',
	bundle: true,
	minify: true,
	sourcemap: true,
	platform: 'node',
	plugins: [
		esbuildPluginTsc({
			force: true
		}),
	]
});
