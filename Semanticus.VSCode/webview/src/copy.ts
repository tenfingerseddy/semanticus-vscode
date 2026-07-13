/** Convert an engine identifier or enum value into user-facing words. */
export function uiLabel(value: unknown, fallback = 'Not available'): string {
  const raw = String(value ?? '').trim();
  if (!raw) return fallback;
  const words = raw
    .replace(/[_-]+/g, ' ')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/\s+/g, ' ')
    .trim()
    .split(' ')
    .map((word, index) => /^[A-Z0-9]{2,}$/.test(word)
      ? word
      : index === 0
        ? word.charAt(0).toUpperCase() + word.slice(1).toLowerCase()
        : word.toLowerCase())
    .join(' ');
  return words;
}
