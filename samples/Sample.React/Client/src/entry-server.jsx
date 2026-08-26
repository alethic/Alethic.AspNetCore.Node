import { renderToPipeableStream } from 'react-dom/server';
import { PassThrough, Readable } from 'node:stream';
import App, { loadPark } from './App.jsx';
import { routes, match } from './router.jsx';

/**
 * Renders one request to a full document.
 *
 * onAllReady rather than onShellReady: this sample optimizes for crawlers, so the suspended park
 * content must be in the markup rather than streamed in behind it. A page meant for people first
 * would flip that switch and stream its shell.
 */
function render(url, signal) {
    const path = url.pathname;
    const matched = match(path);

    // The route decides what to load, so adding a route is one edit rather than three.
    const parkRef = matched?.route.id === 'park' ? matched.params.parkRef : null;
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

    // The application's router decides what exists; a miss is a real 404 on the wire rather than a
    // soft 200 with not-found copy in it.
    const found = matched !== null && parkRef !== 'missing';

    return new Response(Readable.toWeb(sink), {
        status: found ? 200 : 404,
        headers: { 'content-type': 'text/html; charset=utf-8' },
    });
}

export default {

    /**
     * The router itself, reachable by the host — not a description of it.
     *
     * No convention says this should be here or what it should be called: the convention is `fetch`,
     * and routes are outside it entirely. The host's route provider is written against this
     * application and knows where to look, which is exactly the arrangement that keeps route
     * knowledge in one place.
     */
    router: routes,

    /** The Web-standard handler: `fetch(request, env, ctx)`, as Workers, Deno and Bun define it. */
    fetch(request, env, ctx) {
        return render(new URL(request.url), request.signal);
    },

};
