import { hydrateRoot } from 'react-dom/client';
import App, { loadPark } from './App.jsx';
import { match } from './router.jsx';

const path = window.location.pathname;
const matched = match(path);

// The same promise shape the server rendered against, off the same route table, so hydration matches.
const dataPromise = matched?.route.id === 'park' ? loadPark(matched.params.parkRef) : null;

hydrateRoot(document.getElementById('app'), <App path={path} dataPromise={dataPromise} />);
