// "Fix N names that Copilot reads as the same word" opens Find and replace with those names filled in (plan statement check 2,
// defect 6). The engine hands the names in the row's jump (ModelPlanJump.Find); the Plan turns them into a navigation target
// and Find and replace turns the target into the words in its box. Pure, so a test can read every step.

/** The jump the engine puts on a Plan row: the page, its label, and, for the collisions row, the names to look for. */
export interface NamedJump { tool: string; label?: string; find?: string[] | null }

const PREFIX = 'find:';

/** The navigation target for a jump: `find:` and the names as JSON, or undefined when the jump names none. */
export function jumpTarget(jump: NamedJump | null | undefined): string | undefined {
  const names = (jump?.find ?? []).filter((n) => typeof n === 'string' && n.trim().length > 0);
  return jump?.tool === 'search' && names.length > 0 ? PREFIX + JSON.stringify(names) : undefined;
}

/** The names a navigation target carries, or null when it is not one of ours (a plain object ref, a typed word). */
export function namesOfTarget(target: string | null | undefined): string[] | null {
  if (!target || !target.startsWith(PREFIX)) return null;
  try {
    const parsed: unknown = JSON.parse(target.slice(PREFIX.length));
    const names = Array.isArray(parsed) ? parsed.filter((n): n is string => typeof n === 'string' && n.trim().length > 0) : [];
    return names.length > 0 ? names : null;
  } catch { return null; }
}

const escapeRegex = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** What goes in the Find box for these names. One name is plain text; several are one pattern that matches any of them, and the
 *  Regular expression switch must be on for it to read that way. */
export function findBoxFor(names: string[]): { text: string; regex: boolean } {
  return names.length === 1 ? { text: names[0], regex: false } : { text: '(?:' + names.map(escapeRegex).join('|') + ')', regex: true };
}
