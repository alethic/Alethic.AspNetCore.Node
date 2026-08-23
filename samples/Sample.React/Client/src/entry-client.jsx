import { hydrateRoot } from 'react-dom/client';
import App, { loadPark } from './App.jsx';

const path = window.location.pathname;
const parkRef = path.startsWith('/parks/') ? path.slice('/parks/'.length) : null;

// The same promise shape the server rendered against, so hydration matches.
const dataPromise = parkRef ? loadPark(parkRef) : null;

hydrateRoot(document.getElementById('app'), <App path={path} dataPromise={dataPromise} />);
