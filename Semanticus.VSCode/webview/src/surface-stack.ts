// Which floating surface owns the Escape key.
//
// Astra, 2026-09-15 (P1): the New check drawer and the Connections hub each registered their own window
// keydown listener and each closed itself on Escape. Opening Connections from inside a half-finished check
// and pressing Escape therefore threw the check away as well as the hub, which is the exact thing the
// redesign set out to stop. Neither listener could tell it was underneath something else.
//
// A stack fixes that without either surface having to know the other exists: a surface takes a token while
// it is mounted, and acts on Escape only while its token is on top. No React here on purpose, so the rule
// can be run in a test rather than matched in a screenshot.
//
// Only surfaces that DISMISS on Escape belong here. A menu inside a surface closes itself and does not
// push: it is part of its own surface, not a new one.

const stack: symbol[] = [];

/** Take the Escape key while this surface is mounted. Release it with popSurface in the same cleanup. */
export function pushSurface(): symbol {
  const token = Symbol('surface');
  stack.push(token);
  return token;
}

/** Release the key. Safe to call twice, and safe to call out of order: a surface unmounting from under
 *  another one must never strand the stack and leave nobody able to close. */
export function popSurface(token: symbol): void {
  const at = stack.lastIndexOf(token);
  if (at >= 0) stack.splice(at, 1);
}

/** True while this surface is the one a person sees on top, so it is the one Escape means. */
export function isTopSurface(token: symbol): boolean {
  return stack.length > 0 && stack[stack.length - 1] === token;
}

/** How many surfaces are open. For tests, and for nothing else. */
export function surfaceCount(): number {
  return stack.length;
}

/** Empty the stack. For tests, and for nothing else. */
export function resetSurfaces(): void {
  stack.length = 0;
}
