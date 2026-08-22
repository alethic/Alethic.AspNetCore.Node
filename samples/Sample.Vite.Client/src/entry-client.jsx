import { StrictMode } from 'react';
import { hydrateRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router';

import App from './App';

const getConfigAsync = async () => {
    return await (await fetch('config.json', { headers: { Accept: 'application/json' } })).json()
}

hydrateRoot(
    document.getElementById('app'),
    <StrictMode>
        <BrowserRouter>
            <App location={window.location} getConfigAsync={getConfigAsync} />
        </BrowserRouter>
    </StrictMode>
);
