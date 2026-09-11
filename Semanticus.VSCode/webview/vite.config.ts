import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

// Builds the Studio webview into ../media/studio. The entry keeps the fixed names studio.js / studio.css so the
// extension can construct a CSP-safe HTML document with asWebviewUri + a nonce. Output is an ES module (not IIFE)
// so dynamic import() code-splits: the heavy M-language cluster (@microsoft/powerquery-* + the M std-library
// dataset) lands in its own studio-*.js chunk that only downloads when that tab opens. `base: './'` makes the
// entry's chunk imports relative to its own URL, so they resolve correctly under the webview's asWebviewUri origin.
// The entry <script> must therefore be type="module" and the CSP must allow 'strict-dynamic' (see studioHtml in
// the extension + the uishot harness.html) so the nonce'd entry can pull in its sibling chunks.
// Vite can minify an escaped control character into its raw byte. Escape those bytes again in the emitted JS;
// this keeps every runtime string unchanged, including Graphlib's internal delimiter, while the committed bundle
// stays reviewable by the byte-hygiene gate.
const escapeRawControlBytes = (code: string) => {
  let escaped = '';
  for (const ch of code) {
    const point = ch.charCodeAt(0);
    const forbidden = (point < 0x20 && point !== 0x09 && point !== 0x0a && point !== 0x0d) || point === 0x7f;
    escaped += forbidden ? `\\x${point.toString(16).padStart(2, '0')}` : ch;
  }
  return escaped;
};

const printableBundleSource = (): Plugin => ({
  name: 'printable-bundle-source',
  generateBundle(_options, bundle) {
    for (const artifact of Object.values(bundle)) {
      if (artifact.type === 'chunk') artifact.code = escapeRawControlBytes(artifact.code);
    }
  },
});

export default defineConfig({
  base: './',
  plugins: [react(), tailwindcss(), printableBundleSource()],
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
