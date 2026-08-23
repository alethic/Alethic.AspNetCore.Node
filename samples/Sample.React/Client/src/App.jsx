import { Suspense, use, useState } from 'react';

/**
 * The sample's data access. On the server the promise resolves before rendering completes, so the
 * park's name lands in the markup; on the client the same component hydrates over it.
 */
function Park({ promise }) {
	const park = use(promise);
	return (
		<article>
			<h1>{park.name}</h1>
			<p>{park.city}, {park.state}</p>
		</article>
	);
}

/** A little interactivity, to prove hydration produces a live page rather than an inert one. */
function Counter() {
	const [count, setCount] = useState(0);
	return <button onClick={() => setCount(count + 1)}>clicked {count}</button>;
}

export default function App({ path, dataPromise }) {
	if (path.startsWith('/parks/')) {
		return (
			<main>
				<Suspense fallback={<p>loading…</p>}>
					<Park promise={dataPromise} />
				</Suspense>
				<Counter />
			</main>
		);
	}

	if (path === '/about') {
		return (
			<main>
				<h1>About</h1>
				<p>A sample React application rendered inside a .NET process.</p>
				<Counter />
			</main>
		);
	}

	return (
		<main>
			<h1>Home</h1>
			<p>Try <a href="/parks/enchanted-rock">a park</a> or <a href="/about">about</a>.</p>
			<Counter />
		</main>
	);
}

/** Stands in for a real data source; the server and client both go through it. */
export function loadPark(parkRef) {
	return new Promise(resolve => setTimeout(() => resolve({
		name: parkRef.replace(/-/g, ' ').replace(/\b\w/g, c => c.toUpperCase()),
		city: 'Fredericksburg',
		state: 'Texas',
	}), 25));
}
