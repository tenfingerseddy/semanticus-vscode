import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

// Builds the Studio webview into ../media/studio. The entry keeps the fixed names studio.js / studio.css so the
// extension can construct a CSP-safe HTML document with asWebviewUri + a nonce. Output is an ES module (not IIFE)
// so dynamic import() code-splits: the heavy M-language cluster (@microsoft/powerquery-* + the M std-library
// dataset) lands in its own studio-*.js chunk that only downloads when that tab opens. `base: './'` makes the
// entry's chunk imports relative to its own URL, so they resolve correctly under the webview's asWebviewUri origin.
// The entry <script> must therefore be type="module" and the CSP must allow 'strict-dynamic' (see studioHtml in
// the extension + the uishot harness.html) so the nonce'd entry can pull in its sibling chunks.
export default defineConfig({
  base: './',
  plugins: [react(), tailwindcss()],
  build: {
    outDir: '../media/studio',
    emptyOutDir: true,
    cssCodeSplit: false,
    target: 'es2020',
    rollupOptions: {
      input: 'src/main.tsx',
      output: {
        format: 'es',
        entryFileNames: 'studio.js',
        // Stable (un-hashed) chunk names: media/studio is a committed build artifact, so a content hash would
        // churn the filename on every rebuild and orphan the old chunk. emptyOutDir wipes the dir each build,
        // and the entry imports chunks by relative name internally, so no hash is needed.
        chunkFileNames: 'studio-[name].js',
        assetFileNames: 'studio.[ext]',
      },
    },
  },
});
