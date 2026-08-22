import fs from 'node:fs/promises';

import { StrictMode } from 'react';
import { renderToPipeableStream } from 'react-dom/server';
import { StaticRouter } from 'react-router';

import App from './App';

const getConfigAsync = async () => {
    return JSON.parse(await fs.readFile('config.json', 'utf-8'));
};

/**
 * @param {string} url
 * @param {import('react-dom/server').RenderToPipeableStreamOptions} [options]
 */
export function render(location, options) {
    return renderToPipeableStream(
        <StrictMode>
            <StaticRouter location={location.pathname}>
                <App location={location} getConfigAsync={getConfigAsync} />
            </StaticRouter>
        </StrictMode>,
        options,
    );
};
