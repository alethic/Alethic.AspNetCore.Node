import esbuild from 'esbuild';
import esbuildPluginTsc from 'esbuild-plugin-tsc';

esbuild.build({
	entryPoints: ['src/Prerenderer.ts'],
	outfile: 'dist/prerenderer.js',
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
