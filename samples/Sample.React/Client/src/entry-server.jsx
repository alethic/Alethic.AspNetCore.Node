import { renderToPipeableStream } from 'react-dom/server';
import { PassThrough, Readable } from 'node:stream';
import App, { loadPark } from './App.jsx';

/**
 * Renders one request to a full document.
 *
 * onAllReady rather than onShellReady: this sample optimizes for crawlers, so the suspended park
 * content must be in the markup rather than streamed in behind it. A page meant for people first
 * would flip that switch and stream its shell.
 */
function render(url, signal) {
	const path = url.pathname;
	const parkRef = path.startsWith('/parks/') ? path.slice('/parks/'.length) : null;
	const dataPromise = parkRef ? loadPark(parkRef) : null;

	const sink = new PassThrough();

	const { pipe, abort } = renderToPipeableStream(
		<html lang="en">
			<head>
				<meta charSet="utf-8" />
				<title>{parkRef ? parkRef + ' — Sample' : 'Sample'}</title>
			</head>
			<body>
				<div id="app">
					<App path={path} dataPromise={dataPromise} />
				</div>
				<script src="/app.js" async />
			</body>
		</html>,
		{
			onAllReady() { pipe(sink); },
			onError(e) { console.error(e); },
		});

	signal.addEventListener('abort', () => abort(signal.reason));

	return new Response(Readable.toWeb(sink), {
		status: parkRef === 'missing' ? 404 : 200,
		headers: { 'content-type': 'text/html; charset=utf-8' },
	});
}

export default {

	fetch(request) {
		return render(new URL(request.url), request.signal);
	},

	/**
	 * The route manifest, in ASP.NET template syntax. This entry module is the one place that knows
	 * the application's own routing, so the translation lives here and nowhere in .NET.
	 */
	routes() {
		return [
			{ pattern: '/', renderMode: 'Server', id: 'home' },
			{ pattern: '/about', renderMode: 'Prerender', id: 'about' },
			{ pattern: '/parks/{parkRef}', renderMode: 'Server', id: 'park' },
		];
	},

};
