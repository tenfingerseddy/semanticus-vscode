import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { printableBundleSource } from './vite.config';

// A separate entry keeps the shared card in the Properties view without loading Studio's page styles.
export default defineConfig({
  define: { 'process.env.NODE_ENV': JSON.stringify('production') },
  plugins: [react(), printableBundleSource()],
  build: {
    outDir: '../media/propgrid', emptyOutDir: false, target: 'es2020',
    lib: { entry: 'src/checked-grid.tsx', formats: ['iife'], name: 'SemanticusCheckedGrid', fileName: () => 'checked-ui.js', cssFileName: 'checked-ui' },
  },
});
