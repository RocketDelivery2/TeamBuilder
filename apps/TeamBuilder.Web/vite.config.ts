/// <reference types="vitest/config" />
import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

// The dev server proxies /api to a locally running TeamBuilder API, so the browser talks to
// one origin and no CORS change is needed. Override the target with TEAMBUILDER_API_PROXY.
const apiTarget = process.env.TEAMBUILDER_API_PROXY ?? 'http://localhost:5076';

export default defineConfig(({ mode }) => {
  // A release artifact (TEAMBUILDER_WEB_RELEASE=true, as the release workflow builds it) is
  // deployed to QA and Production, where manual developer tokens must never be compiled in.
  const env = loadEnv(mode, process.cwd(), 'VITE_');
  if (process.env.TEAMBUILDER_WEB_RELEASE === 'true' && env.VITE_ALLOW_DEV_TOKEN === 'true') {
    throw new Error('A release build must not set VITE_ALLOW_DEV_TOKEN=true (developer tokens are local-only).');
  }

  return {
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
  };
});
