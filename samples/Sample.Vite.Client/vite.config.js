import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig(({ mode, isSsrBuild }) => {
    return {
        plugins: [react()],
        build: {
            rollupOptions: {
                output: {}
            }
        }
    };
});
