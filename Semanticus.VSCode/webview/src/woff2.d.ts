// Vite inlines a font imported with ?inline as a data: URI string (see docfonts.ts).
declare module '*.woff2?inline' {
  const dataUri: string;
  export default dataUri;
}
