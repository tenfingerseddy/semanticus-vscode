import { useEffect, useRef } from 'react';
import { isTopSurface, popSurface, pushSurface } from './surface-stack';

// The React half of surface-stack.ts. It is its own file so the RULE stays runnable in a plain Node test
// without React on the import path.
//
// The subtlety this exists to hold, found by DRIVING the journey and not by reading the code: a surface
// must take its place in the stack ONCE, when it opens. The first version registered inside an effect that
// depended on the close handler, and the handler is a new function on every render of its parent, so
// opening the Connections hub re-rendered the Tests page, the drawer's effect re-ran, and the drawer
// pushed itself back on top of the hub. Escape then closed the drawer and left the hub standing, which is
// the original bug with the two surfaces swapped. The handler lives in a ref and the effect depends on
// nothing that changes while the surface is open.
export function useSurfaceEscape(onEscape: () => void, active = true): void {
  const handler = useRef(onEscape);
  handler.current = onEscape;
  useEffect(() => {
    if (!active) return undefined;
    const token = pushSurface();
    const onKey = (event: globalThis.KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      if (!isTopSurface(token)) return;
      handler.current();
    };
    window.addEventListener('keydown', onKey);
    return () => { window.removeEventListener('keydown', onKey); popSurface(token); };
  }, [active]);
}
