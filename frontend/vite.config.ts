/// <reference types="vitest" />
import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

// The browser only talks to this server (port 3000). Requests to /api and /ws are forwarded to the
// API service, so the browser never makes a cross-origin call and the API needs no CORS setup.
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', '');
  const api = env.VITE_API_URL || 'http://localhost:8000';
  const proxy = {
    '/api': { target: api, changeOrigin: true },
    '/ws': { target: api.replace(/^http/, 'ws'), ws: true, changeOrigin: true },
  };
  return {
    plugins: [react()],
    server: { port: 3000, strictPort: true, proxy },
    preview: { port: 3000, strictPort: true, proxy },
    test: { environment: 'node', include: ['src/**/*.test.ts'] },
  };
});
