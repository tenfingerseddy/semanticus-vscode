// The two fonts the exported document embeds, so the file reads the same with no network. Vite inlines each file as a
// data: URI at build time. Both are SIL Open Font License 1.1; the licence texts sit beside the files in media/fonts
// and are declared in third-party-manifest.json. Kept in its own module so the pure renderer (docrender.ts) never
// touches the bundler: the node tests render without fonts and the CSS falls back to system fonts.
import sans from '../../media/fonts/InstrumentSans-latin-variable.woff2?inline';
import mono from '../../media/fonts/JetBrainsMono-400.woff2?inline';
import type { DocFonts } from './docrender';

export const DOC_FONTS: DocFonts = { sans, mono };
