/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The dev server proxies /api to a locally running TeamBuilder API, so the browser talks to
// one origin and no CORS change is needed. Override the target with TEAMBUILDER_API_PROXY.
const apiTarget = process.env.TEAMBUILDER_API_PROXY ?? 'http://localhost:5076';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test-setup.ts'],
  },
});
